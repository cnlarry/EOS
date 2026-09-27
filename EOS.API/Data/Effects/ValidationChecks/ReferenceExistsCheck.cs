using System.Globalization;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data.Effects.ValidationChecks;

/// <summary>
/// reference-exists 逐项断言"当前单据引用的资料存在"：来源行限定在当前单据内
/// （主表按主键参数、明细按同一单据关联），否则他单的历史坏引用会拦下本次保存。
/// 配置 lineField 时，命中项把缺失行的行号回填进消息的 {ROWS} 占位符。
/// </summary>
internal static class ReferenceExistsCheck
{
    /// <summary>Compiled single reference-exists assertion plus its user-facing message.</summary>
    internal sealed record ReferenceExistsSql(
        string Sql,
        IReadOnlyList<EffectSqlParameter> Parameters,
        string? Message,
        string? LineSql = null);

    /// <summary>执行引用存在性断言的闭集；返回违规文案（含 {ROWS} 渲染），null 即全部通过。</summary>
    internal static async Task<string?> RunAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        ModuleEffectPlan plan,
        EffectValidationPlan rule,
        IReadOnlyList<string> masterKeyValues,
        CancellationToken token)
    {
        if (!rule.Params.TryGetProperty("checks", out var checks) || checks.ValueKind != JsonValueKind.Array)
            throw new EffectConfigException("reference-exists 缺少 checks 数组。");
        foreach (var check in checks.EnumerateArray())
        {
            var compiled = BuildReferenceExistsCheckSql(plan, check, masterKeyValues);
            await using (var command = new SqlCommand(compiled.Sql, connection, transaction))
            {
                foreach (var parameter in compiled.Parameters)
                    command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
                if (await command.ExecuteScalarAsync(token) is null)
                    continue;
            }

            // 断言自带文案优先：一条规则可含多条断言（如"库别不存在/产品不存在"），
            // 规则级 message 只是工作区里的概述，盖住断言文案会让用户看到错的那一条。
            var message = compiled.Message ?? rule.Message ?? "引用数据不存在。";
            if (compiled.LineSql is null)
                return message;
            var lines = new List<string>();
            await using var lineCommand = new SqlCommand(compiled.LineSql, connection, transaction);
            foreach (var parameter in compiled.Parameters)
                lineCommand.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
            await using var reader = await lineCommand.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
                lines.Add(reader.IsDBNull(0) ? string.Empty : CheckSupport.FormatCell(reader, 0));
            return message.Replace("{ROWS}", string.Join("\r\n", lines));
        }
        return null;
    }

    internal static ReferenceExistsSql BuildReferenceExistsCheckSql(
        ModuleEffectPlan plan,
        JsonElement check,
        IReadOnlyList<string> masterKeyValues)
    {
        var targets = check.TryGetProperty("targets", out var declared) && declared.ValueKind == JsonValueKind.Array
            ? declared.EnumerateArray().ToList()
            : [check];
        if (targets.Count == 0)
            throw new EffectConfigException("reference-exists.targets 不能为空数组。");

        // 每个目标表一条断言、彼此取 AND：整行只要在任一目标表里存在即视为通过
        // （对应既有实现把多张表 UNION ALL 后做一次存在性判断）。
        var missingParts = new List<string>();
        var usesDetail = false;
        var conditionParameters = new List<EffectSqlParameter>();
        foreach (var target in targets)
        {
            var refTable = CheckSupport.RequiredString(target, "refTable", "reference-exists.check 缺少 refTable。");
            var (match, matchUsesDetail) = BuildReferenceMatch(target);
            var (mismatch, mismatchUsesDetail) = BuildReferenceMismatch(target);
            var (refCondition, refConditionUsesDetail, refConditionParameters) = BuildReferenceCondition(target);
            usesDetail |= matchUsesDetail || mismatchUsesDetail || refConditionUsesDetail;
            conditionParameters.AddRange(refConditionParameters);
            // 无 mismatch ＝ 断言"引用必须存在"；带 mismatch ＝ 断言"引用存在时该列必须与来源一致"
            // （既有实现的反向一致性断言，命中条件是存在一行且两列不等）；
            // refCondition ＝ 断言"引用行存在且满足该条件即命中"（同样是 EXISTS 形态）。
            var body = match + BuildActiveTag(target);
            var extra = mismatch is null ? refCondition : mismatch;
            missingParts.Add(extra is null
                ? "NOT EXISTS (SELECT 1 FROM dbo." + EffectConditionCompiler.Identifier(refTable)
                    + " R WITH (NOLOCK) WHERE " + body + ")"
                : "EXISTS (SELECT 1 FROM dbo." + EffectConditionCompiler.Identifier(refTable)
                    + " R WITH (NOLOCK) WHERE " + body + " AND " + extra + ")");
        }

        var (emptyParts, allowEmptyUsesDetail) = BuildAllowEmptyParts(check);
        usesDetail |= allowEmptyUsesDetail;
        // 断言查的是"缺失行"：来源键为空时该行不参与判定，故守卫是否定型（为空 ⇒ 条件为假 ⇒ 不命中），
        // 写成 OR 会让"未引用"的行反而命中。
        var guard = emptyParts.Count == 0
            ? string.Empty
            : "NOT (" + string.Join(" OR ", emptyParts.Select(part => part + " IS NULL OR " + part + " = ''")) + ") AND ";

        var lineExpression = BuildReferenceLineExpression(check);
        usesDetail |= lineExpression is not null;

        var (documentScope, scopeParameters) = CheckSupport.BuildDocumentScopeParts(plan, masterKeyValues, "M");
        var scopeParts = new List<string>(documentScope);
        var fromDetail = string.Empty;
        if (usesDetail)
        {
            if (plan.DetailTable is null)
                throw new EffectConfigException("reference-exists 明细级断言需要模块明细表（模块形态不足）。");
            // 明细表按约定携带主表主键列，据此把来源行限定在当前单据内；参数与主表作用域同名同值，只登记一次。
            scopeParts.AddRange(CheckSupport.BuildDocumentScopeParts(plan, masterKeyValues, "D").Parts);
            fromDetail = " CROSS JOIN dbo." + EffectConditionCompiler.Identifier(plan.DetailTable) + " D";
        }

        var fromMaster = "dbo." + EffectConditionCompiler.Identifier(plan.MasterTable!) + " M";
        var predicate = guard + string.Join(" AND ", scopeParts)
            + " AND " + string.Join(" AND ", missingParts);
        var sql = "SELECT TOP 1 1 FROM " + fromMaster + fromDetail + " WHERE " + predicate;
        var message = check.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.String
            ? msg.GetString()
            : null;
        return new ReferenceExistsSql(sql, [.. scopeParameters, .. conditionParameters], message,
            lineExpression is null
                ? null
                : "SELECT TOP (" + CheckSupport.MaxRowsOf(check).ToString(CultureInfo.InvariantCulture) + ") " + lineExpression
                    + " FROM " + fromMaster + fromDetail + " WHERE " + predicate + " ORDER BY " + lineExpression);
    }

    /// <summary>单个目标表的匹配条件：join 逐列配对，refKey 为"两侧同名列"的简写。</summary>
    private static (string Sql, bool UsesDetail) BuildReferenceMatch(JsonElement target)
    {
        if (target.TryGetProperty("join", out var join) && join.ValueKind == JsonValueKind.Array)
        {
            var pairs = new List<string>();
            var usesDetail = false;
            foreach (var pair in join.EnumerateArray())
            {
                var targetColumn = CheckSupport.RequiredString(pair, "target", "reference-exists.join 缺少 target。");
                var source = pair.TryGetProperty("source", out var declaredSource) ? declaredSource : default;
                var (expression, sourceUsesDetail) = SourceExpression(source);
                usesDetail |= sourceUsesDetail;
                pairs.Add("R." + EffectConditionCompiler.Identifier(targetColumn) + " = " + expression);
            }
            if (pairs.Count == 0)
                throw new EffectConfigException("reference-exists.join 不能为空数组。");
            return (string.Join(" AND ", pairs), usesDetail);
        }
        if (!target.TryGetProperty("refKey", out var refKey) || refKey.ValueKind != JsonValueKind.Object)
            throw new EffectConfigException("reference-exists.check 缺少 refKey。");
        var field = CheckSupport.RequiredString(refKey, "field", "reference-exists.refKey 缺少 field。");
        var (keyExpression, keyUsesDetail) = SourceExpression(refKey);
        return ("R." + EffectConditionCompiler.Identifier(field) + " = " + keyExpression, keyUsesDetail);
    }

    /// <summary>
    /// 反向一致性断言（可选）：引用行与来源行的某一列必须相等，不等即命中。
    /// 用 EXISTS + &lt;&gt; 表达，而不是"NOT EXISTS 相等"，否则引用本身缺失时会被误报成"不一致"。
    /// </summary>
    private static (string? Sql, bool UsesDetail) BuildReferenceMismatch(JsonElement target)
    {
        if (!target.TryGetProperty("mismatch", out var mismatch) || mismatch.ValueKind != JsonValueKind.Object)
            return (null, false);
        var column = CheckSupport.RequiredString(mismatch, "target", "reference-exists.mismatch 缺少 target。");
        var source = mismatch.TryGetProperty("source", out var declared) ? declared : default;
        var (expression, usesDetail) = SourceExpression(source);
        return ("R." + EffectConditionCompiler.Identifier(column) + " <> " + expression, usesDetail);
    }

    /// <summary>
    /// 被引用行的闭式条件（可选，`refCondition`）：与 mismatch 同形（EXISTS + 条件），
    /// 用于"引用行存在**且**满足某条件即命中"的断言——如"被引用的送货/退货行已有回执号
    /// （CALLBACK_NO 非空）"。条件走闭式条件编译器，R 即被引用行别名。
    /// </summary>
    private static (string? Sql, bool UsesDetail, IReadOnlyList<EffectSqlParameter> Parameters) BuildReferenceCondition(
        JsonElement target)
    {
        if (!target.TryGetProperty("refCondition", out var condition) || condition.ValueKind != JsonValueKind.Object)
            return (null, false, Array.Empty<EffectSqlParameter>());
        var usesDetail = false;
        var compiled = new EffectConditionCompiler().Compile(
            condition,
            (scope, _) => scope.ToUpperInvariant() switch
            {
                "TARGET" => "R",
                "DETAIL" => DetailAlias(),
                "MASTER" => "M",
                _ => null,
            },
            _ => true,
            outerAlias: "R");
        return (compiled.Sql, usesDetail, compiled.Parameters);

        string DetailAlias()
        {
            usesDetail = true;
            return "D";
        }
    }

    private static string BuildActiveTag(JsonElement target)
    {
        if (!target.TryGetProperty("activeTag", out var active) || active.ValueKind != JsonValueKind.Object)
            return string.Empty;
        var field = CheckSupport.RequiredString(active, "field", "reference-exists.activeTag 缺少 field。");
        var expect = active.TryGetProperty("expect", out var declared) && declared.ValueKind == JsonValueKind.Number
            ? declared.GetInt32()
            : 0;
        return " AND R." + EffectConditionCompiler.Identifier(field)
            + " = " + expect.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// allowEmpty：来源侧键为空即放行本项（既有实现 "ISNULL(类型,'')&lt;&gt;''" 的语义）。
    /// true 表示 refKey 的来源列；数组形式逐列声明，用于复合键里允许为空的列
    /// （如"订单别 + 订单号"中订单别为空即整行不校验）。
    /// </summary>
    private static (List<string> Parts, bool UsesDetail) BuildAllowEmptyParts(JsonElement check)
    {
        var parts = new List<string>();
        if (!check.TryGetProperty("allowEmpty", out var allow)
            || allow.ValueKind is JsonValueKind.Null or JsonValueKind.False or JsonValueKind.Undefined)
            return (parts, false);
        var usesDetail = false;
        if (allow.ValueKind == JsonValueKind.True)
        {
            if (!check.TryGetProperty("refKey", out var refKey) || refKey.ValueKind != JsonValueKind.Object)
                throw new EffectConfigException("reference-exists.allowEmpty=true 需要配合 refKey 使用。");
            var (expression, sourceUsesDetail) = SourceExpression(refKey);
            parts.Add(expression);
            return (parts, sourceUsesDetail);
        }
        if (allow.ValueKind != JsonValueKind.Array)
            throw new EffectConfigException("reference-exists.allowEmpty 必须是布尔或数组。");
        foreach (var item in allow.EnumerateArray())
        {
            var (expression, sourceUsesDetail) = SourceExpression(item);
            usesDetail |= sourceUsesDetail;
            parts.Add(expression);
        }
        return (parts, usesDetail);
    }

    /// <summary>来源侧表达式：缺省按明细列解析，显式 MASTER 才取主表列。</summary>
    private static (string Sql, bool UsesDetail) SourceExpression(JsonElement source)
    {
        var scope = source.TryGetProperty("scope", out var declared) && declared.ValueKind == JsonValueKind.String
            ? declared.GetString()!.Trim().ToUpperInvariant()
            : "DETAIL";
        var field = CheckSupport.RequiredString(source, "field", "reference-exists 来源列缺少 field。");
        var usesDetail = !string.Equals(scope, "MASTER", StringComparison.OrdinalIgnoreCase);
        return ((usesDetail ? "D." : "M.") + EffectConditionCompiler.Identifier(field), usesDetail);
    }

    /// <summary>缺失行行号集合：逐行渲染时取明细行号（字符串写法即明细列）。</summary>
    private static string? BuildReferenceLineExpression(JsonElement check)
    {
        if (!check.TryGetProperty("lineField", out var line) || line.ValueKind == JsonValueKind.Null)
            return null;
        if (line.ValueKind == JsonValueKind.String)
            return "D." + EffectConditionCompiler.Identifier(line.GetString()!.Trim());
        return SourceExpression(line).Sql;
    }
}
