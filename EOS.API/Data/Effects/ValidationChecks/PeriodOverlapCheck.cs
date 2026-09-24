using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data.Effects.ValidationChecks;

/// <summary>
/// period-overlap（期间重叠）：把当前单据的明细行与**其它单据**的明细行按"同组字段相等、
/// 范围字段闭区间相交"比对，命中即违规。范围端点相接算重叠；任一侧结束日为空＝无限期
/// （不引入哨兵日期，避免 smalldatetime 放不下 9999-12-31 而报转换错误）。
///
/// 单据主键上下文不可缺：scopeFields 必须与主键数量一一对应，否则拒绝——缺上下文就退化成
/// 跨单比较，那是错的语义。
/// </summary>
internal static class PeriodOverlapCheck
{
    /// <summary>Compiled period-overlap statement plus the conflicting-row columns echoed into the message.</summary>
    internal sealed record PeriodOverlapSql(
        string Sql,
        IReadOnlyList<EffectSqlParameter> Parameters,
        IReadOnlyList<string> Diagnostics);

    /// <summary>执行期间重叠校验；返回违规文案（已渲染 {ROWS} 占位符），null 即通过。</summary>
    internal static async Task<string?> RunAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        EffectValidationPlan rule,
        IReadOnlyList<string> masterKeyValues,
        CancellationToken token)
    {
        var compiled = BuildPeriodOverlapSql(rule.Params, masterKeyValues);
        await using var command = new SqlCommand(compiled.Sql, connection, transaction);
        foreach (var parameter in compiled.Parameters)
            command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);

        if (compiled.Diagnostics.Count == 0)
        {
            return await command.ExecuteScalarAsync(token) is not null
                ? rule.Message ?? "期间与其它单据重叠。"
                : null;
        }

        await using var reader = await command.ExecuteReaderAsync(token);
        var lines = new List<string>();
        while (await reader.ReadAsync(token))
        {
            var cells = new List<string>(compiled.Diagnostics.Count);
            for (var index = 0; index < compiled.Diagnostics.Count; index++)
            {
                cells.Add(CheckSupport.FormatCell(reader, index));
            }
            lines.Add(string.Join("\t", cells) + "\t");
        }
        if (lines.Count == 0)
            return null;
        return (rule.Message ?? "期间与其它单据重叠。").Replace("{ROWS}", string.Join("\r\n", lines));
    }

    internal static PeriodOverlapSql BuildPeriodOverlapSql(
        JsonElement root,
        IReadOnlyList<string> masterKeyValues)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw new EffectConfigException("period-overlap 参数必须是对象。");
        var detailTable = CheckSupport.RequiredString(root, "detailTable", "period-overlap 缺少 detailTable。");
        if (!root.TryGetProperty("rangeFields", out var range) || range.ValueKind != JsonValueKind.Object)
            throw new EffectConfigException("period-overlap 缺少 rangeFields。");
        var beginField = CheckSupport.RequiredString(range, "begin", "period-overlap rangeFields 缺少 begin。");
        var endField = CheckSupport.RequiredString(range, "end", "period-overlap rangeFields 缺少 end。");

        var scopeFields = CheckSupport.ParseStringArray(root, "scopeFields");
        if (scopeFields.Length == 0)
            throw new EffectConfigException("period-overlap 缺少 scopeFields。");
        if (masterKeyValues.Count == 0)
            throw new EffectConfigException("period-overlap 缺少单据主键上下文，禁止跨单比较。");
        if (scopeFields.Length != masterKeyValues.Count)
        {
            throw new EffectConfigException(
                $"period-overlap scopeFields 数量（{scopeFields.Length}）与单据主键数量（{masterKeyValues.Count}）不一致。");
        }
        var groupFields = CheckSupport.ParseStringArray(root, "groupFields");
        if (groupFields.Length == 0)
            throw new EffectConfigException("period-overlap 缺少 groupFields。");

        var diagnostics = CheckSupport.ParseStringArray(root, "diagnosticFields");
        var maxRows = root.TryGetProperty("maxRows", out var maxRowsElement)
            && maxRowsElement.ValueKind == JsonValueKind.Number
            && maxRowsElement.TryGetInt32(out var declaredMax)
                ? Math.Clamp(declaredMax, 1, 100)
                : 10;

        var parameters = new List<EffectSqlParameter>();
        var scopeOnCurrent = new List<string>();
        var scopeOnConflict = new List<string>();
        for (var index = 0; index < scopeFields.Length; index++)
        {
            var name = "@mk" + index;
            parameters.Add(new EffectSqlParameter(name, masterKeyValues[index]));
            scopeOnCurrent.Add($"d.{EffectConditionCompiler.Identifier(scopeFields[index])} = {name}");
            scopeOnConflict.Add($"x.{EffectConditionCompiler.Identifier(scopeFields[index])} = {name}");
        }

        var conflictRows = groupFields
            .Select(field => $"x.{EffectConditionCompiler.Identifier(field)} = d.{EffectConditionCompiler.Identifier(field)}")
            .ToList();
        conflictRows.Add("NOT (" + string.Join(" AND ", scopeOnConflict) + ")");
        // 闭区间相交（端点相接算重叠）；任一侧结束日为空＝无限期（不引入哨兵日期，
        // 避免 smalldatetime 等类型放不下 9999-12-31 而报转换错误）。
        conflictRows.Add($"({Begin("d")} <= {End("x")} OR {End("x")} IS NULL)");
        conflictRows.Add($"({Begin("x")} <= {End("d")} OR {End("d")} IS NULL)");

        var displayJoin = string.Empty;
        string? displayCell = null;
        if (root.TryGetProperty("displayLookup", out var lookup) && lookup.ValueKind == JsonValueKind.Object)
        {
            var lookupTable = CheckSupport.RequiredString(lookup, "table", "period-overlap displayLookup 缺少 table。");
            var linkField = CheckSupport.RequiredString(lookup, "linkField", "period-overlap displayLookup 缺少 linkField。");
            var displayField = CheckSupport.RequiredString(lookup, "displayField", "period-overlap displayLookup 缺少 displayField。");
            displayJoin = " LEFT JOIN dbo." + EffectConditionCompiler.Identifier(lookupTable)
                + " dp ON dp." + EffectConditionCompiler.Identifier(linkField)
                + " = x." + EffectConditionCompiler.Identifier(linkField);
            displayCell = "dp." + EffectConditionCompiler.Identifier(displayField);
        }

        var cells = new List<string>();
        foreach (var field in diagnostics)
        {
            if (field.Equals("@display", StringComparison.OrdinalIgnoreCase))
            {
                if (displayCell is null)
                    throw new EffectConfigException("period-overlap 诊断列使用 @display 时必须提供 displayLookup。");
                cells.Add(displayCell);
            }
            else
            {
                cells.Add("x." + EffectConditionCompiler.Identifier(field));
            }
        }

        var sql = new StringBuilder("SELECT TOP ").Append(maxRows).Append(' ')
            .Append(cells.Count > 0 ? string.Join(", ", cells) : "1")
            .Append(" FROM dbo.").Append(EffectConditionCompiler.Identifier(detailTable)).Append(" d")
            .Append(" JOIN dbo.").Append(EffectConditionCompiler.Identifier(detailTable)).Append(" x ON ")
            .Append(string.Join(" AND ", conflictRows))
            .Append(displayJoin)
            .Append(" WHERE ").Append(string.Join(" AND ", scopeOnCurrent))
            .Append($" AND {Begin("d")} IS NOT NULL");
        if (cells.Count > 0)
            sql.Append(" GROUP BY ").Append(string.Join(", ", cells));
        return new PeriodOverlapSql(sql.ToString(), parameters, diagnostics);

        string Begin(string alias) => $"{alias}.{EffectConditionCompiler.Identifier(beginField)}";
        string End(string alias) => $"{alias}.{EffectConditionCompiler.Identifier(endField)}";
    }
}
