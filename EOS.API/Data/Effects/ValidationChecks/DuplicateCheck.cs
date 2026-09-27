using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data.Effects.ValidationChecks;

/// <summary>
/// duplicate-check（重复校验），两种形态共用一条锚定规则：**必须以当前单据的主键上下文为锚**，
/// 脱离单据范围会把其它单据的历史重复算到本次保存头上。
///
/// · within-doc：只看当前单据的明细行（明细表按约定携带主表主键列）；
/// · entity：把已保存行（主表 M，必要时加明细 D）的键值同目标表的其它行逐一比对，
///   目标表与模块主表同名时必须显式声明 excludeSelf，否则刚保存的行必然命中自身。
///
/// master-detail 形态另走 <see cref="BuildMasterDetailUniqueSql"/>：跨单据的"主表维度 × 明细分组键"
/// 唯一，按分组命中数 &gt; 1 判重复。
/// </summary>
internal static class DuplicateCheck
{
    /// <summary>Compiled duplicate-check statement plus the candidate columns echoed into the message.</summary>
    internal sealed record DuplicateCheckSql(
        string Sql,
        IReadOnlyList<EffectSqlParameter> Parameters,
        IReadOnlyList<string> Diagnostics,
        bool MultiRow = false);

    /// <summary>诊断显示名连接（可选）：以冲突行/明细行的关联列去查一张档案表取显示名。</summary>
    internal readonly record struct DisplayLookup(string JoinSql, string DisplayCell);

    /// <summary>执行重复校验；返回违规文案（含 {ROWS}/{COLUMN} 占位符渲染），null 即通过。</summary>
    internal static async Task<string?> RunAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        ModuleEffectPlan plan,
        EffectValidationPlan rule,
        IReadOnlyList<string> masterKeyValues,
        CancellationToken token)
    {
        var compiled = BuildDuplicateCheckSql(plan, rule.Params, masterKeyValues);
        await using var command = new SqlCommand(compiled.Sql, connection, transaction);
        foreach (var parameter in compiled.Parameters)
            command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);

        if (compiled.Diagnostics.Count == 0)
        {
            return await command.ExecuteScalarAsync(token) is not null
                ? (compiled.MultiRow ? (rule.Message ?? "数据重复。").Replace("{ROWS}", string.Empty) : rule.Message ?? "数据重复。")
                : null;
        }

        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token))
            return null;
        if (compiled.MultiRow)
        {
            // master-detail 形态命中多行（每组一行），按 {ROWS} 占位符整块回填：
            // 每行把诊断列以制表符相连并保留行尾制表符，与既有实现逐行拼接一致。
            var lines = new List<string>();
            do
            {
                var cells = new List<string>(compiled.Diagnostics.Count);
                for (var index = 0; index < compiled.Diagnostics.Count; index++)
                    cells.Add(CheckSupport.FormatCell(reader, index));
                lines.Add(string.Join("\t", cells) + "\t");
            }
            while (await reader.ReadAsync(token));
            return (rule.Message ?? "数据重复。").Replace("{ROWS}", string.Join("\r\n", lines));
        }

        var values = new List<string?>(compiled.Diagnostics.Count);
        for (var index = 0; index < compiled.Diagnostics.Count; index++)
            values.Add(reader.IsDBNull(index) ? null : CheckSupport.FormatCell(reader, index));
        return RenderDiagnosticMessage(rule.Message, compiled.Diagnostics, values);
    }

    /// <summary>
    /// master-detail 形态：跨单据的"主表维度 × 明细分组键"唯一。以当前单据行 cur 为锚，
    /// 在与其主表维度相同的其它单据（m）中按明细分组键分组，命中组数大于 1 即重复。
    /// 可选 documentDetailFields 追加"本单员工"限定（只统计本单出现过的分组键），
    /// 未提供时与既有实现的整月扫描一致。诊断列按组聚合（MAX）成多行，供消息 {ROWS} 回填。
    /// </summary>
    internal static DuplicateCheckSql BuildMasterDetailUniqueSql(
        ModuleEffectPlan plan,
        JsonElement root,
        IReadOnlyList<string> masterKeyValues)
    {
        var masterTableName = CheckSupport.RequiredString(root, "masterTable", "duplicate-check master-detail 缺少 masterTable。");
        var detailTableName = CheckSupport.RequiredString(root, "detailTable", "duplicate-check master-detail 缺少 detailTable。");
        var groupFields = CheckSupport.ParseStringArray(root, "groupFields");
        if (groupFields.Length == 0)
            throw new EffectConfigException("duplicate-check master-detail 缺少 groupFields。");
        var masterGroupFields = CheckSupport.ParseStringArray(root, "masterGroupFields");
        if (masterGroupFields.Length == 0)
            throw new EffectConfigException("duplicate-check master-detail 缺少 masterGroupFields。");

        if (!root.TryGetProperty("joinFields", out var joinFields) || joinFields.ValueKind != JsonValueKind.Object)
            throw new EffectConfigException("duplicate-check master-detail 缺少 joinFields。");
        var joinMaster = CheckSupport.ParseStringArray(joinFields, "master");
        var joinDetail = CheckSupport.ParseStringArray(joinFields, "detail");
        if (joinMaster.Length == 0 || joinMaster.Length != joinDetail.Length)
            throw new EffectConfigException("duplicate-check master-detail joinFields.master/detail 必须等长非空。");

        var (documentScope, parameters) = CheckSupport.BuildDocumentScopeParts(plan, masterKeyValues, "cur");
        var on = string.Join(" AND ", joinMaster.Select((field, index) =>
            $"d.{EffectConditionCompiler.Identifier(joinDetail[index])} = m.{EffectConditionCompiler.Identifier(field)}"));

        var predicates = new List<string>();
        foreach (var field in masterGroupFields)
        {
            predicates.Add($"m.{EffectConditionCompiler.Identifier(field)} = cur.{EffectConditionCompiler.Identifier(field)}");
        }

        var documentDetailFields = CheckSupport.ParseStringArray(root, "documentDetailFields");
        if (documentDetailFields.Length > 0)
        {
            if (string.IsNullOrWhiteSpace(plan.DetailTable))
                throw new EffectConfigException("duplicate-check master-detail documentDetailFields 需要明细表。");
            if (documentDetailFields.Length != parameters.Count)
            {
                throw new EffectConfigException(
                    $"duplicate-check master-detail documentDetailFields 数量（{documentDetailFields.Length}）与单据主键数量（{parameters.Count}）不一致。");
            }
            var scopeParts = new List<string>();
            for (var index = 0; index < documentDetailFields.Length; index++)
            {
                scopeParts.Add($"x.{EffectConditionCompiler.Identifier(documentDetailFields[index])} = @mk{index}");
            }
            var groupMatch = string.Join(" AND ", groupFields.Select(field =>
                $"x.{EffectConditionCompiler.Identifier(field)} = d.{EffectConditionCompiler.Identifier(field)}"));
            predicates.Add("EXISTS (SELECT 1 FROM dbo." + EffectConditionCompiler.Identifier(detailTableName)
                + " x WHERE " + string.Join(" AND ", scopeParts) + " AND " + groupMatch + ")");
        }

        var diagnostics = CheckSupport.ParseStringArray(root, "diagnosticFields");
        var maxRows = root.TryGetProperty("maxRows", out var maxRowsElement)
            && maxRowsElement.ValueKind == JsonValueKind.Number
            && maxRowsElement.TryGetInt32(out var declaredMax)
                ? Math.Clamp(declaredMax, 1, 100)
                : 10;
        var selected = diagnostics.Length > 0
            ? string.Join(", ", diagnostics.Select(field => $"MAX(d.{EffectConditionCompiler.Identifier(field)})"))
            : "1";
        var groupBy = string.Join(", ", groupFields.Select(field => "d." + EffectConditionCompiler.Identifier(field)));
        var sql = new StringBuilder("SELECT TOP ").Append(maxRows).Append(' ').Append(selected)
            .Append(" FROM dbo.").Append(EffectConditionCompiler.Identifier(masterTableName)).Append(" cur")
            .Append(" CROSS JOIN dbo.").Append(EffectConditionCompiler.Identifier(masterTableName)).Append(" m")
            .Append(" INNER JOIN dbo.").Append(EffectConditionCompiler.Identifier(detailTableName)).Append(" d ON ").Append(on)
            .Append(" WHERE ").Append(string.Join(" AND ", documentScope))
            .Append(" AND ").Append(string.Join(" AND ", predicates))
            .Append(" GROUP BY ").Append(groupBy)
            .Append(" HAVING COUNT(*) > 1");
        return new DuplicateCheckSql(sql.ToString(), parameters, diagnostics, MultiRow: true);
    }

    /// <summary>
    /// 解析可选的 displayLookup：{table, linkField, displayField}，三键齐备才生效。
    /// 连接列由配置显式声明（受标识符校验），不隐式假设外键。
    /// </summary>
    internal static DisplayLookup? ParseDisplayLookup(JsonElement root, bool diagnosticsRequired)
    {
        if (!root.TryGetProperty("displayLookup", out var lookup) || lookup.ValueKind != JsonValueKind.Object)
        {
            if (diagnosticsRequired)
                throw new EffectConfigException("诊断列使用 @display 时必须提供 displayLookup。");
            return null;
        }
        var table = CheckSupport.RequiredString(lookup, "table", "displayLookup 缺少 table。");
        var linkField = CheckSupport.RequiredString(lookup, "linkField", "displayLookup 缺少 linkField。");
        var displayField = CheckSupport.RequiredString(lookup, "displayField", "displayLookup 缺少 displayField。");
        var join = " LEFT JOIN dbo." + EffectConditionCompiler.Identifier(table)
            + " dp ON dp." + EffectConditionCompiler.Identifier(linkField)
            + " = D." + EffectConditionCompiler.Identifier(linkField);
        return new DisplayLookup(join, "dp." + EffectConditionCompiler.Identifier(displayField));
    }

    internal static DuplicateCheckSql BuildDuplicateCheckSql(
        ModuleEffectPlan plan,
        JsonElement root,
        IReadOnlyList<string> masterKeyValues)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw new EffectConfigException("duplicate-check 参数必须是对象。");
        var mode = root.TryGetProperty("mode", out var modeElement) && modeElement.ValueKind == JsonValueKind.String
            ? modeElement.GetString()!
            : "entity";

        if (mode.Equals("master-detail", StringComparison.OrdinalIgnoreCase))
            return BuildMasterDetailUniqueSql(plan, root, masterKeyValues);

        var keyFields = CheckSupport.ParseStringArray(root, "keyFields");
        if (keyFields.Length == 0)
            throw new EffectConfigException("duplicate-check 缺少 keyFields。");
        var diagnostics = CheckSupport.ParseStringArray(root, "diagnostics");

        if (mode.Equals("within-doc", StringComparison.OrdinalIgnoreCase))
        {
            if (plan.DetailTable is null)
                throw new EffectConfigException("duplicate-check within-doc 需要明细表（模块形态不足）。");
            if (diagnostics.Length > 0)
                throw new EffectConfigException("duplicate-check within-doc 不支持 diagnostics（请用 diagnosticFields）。");
            // 明细表按约定携带主表主键列，据此把分组限定在当前单据内。
            var (detailScope, detailParameters) = CheckSupport.BuildDocumentScopeParts(plan, masterKeyValues, "D");
            var diagnosticFields = CheckSupport.ParseStringArray(root, "diagnosticFields");
            var displayLookup = ParseDisplayLookup(root, diagnosticsRequired: false);
            var withinCells = new List<string>();
            foreach (var field in diagnosticFields)
            {
                if (field.Equals("@display", StringComparison.OrdinalIgnoreCase))
                {
                    if (displayLookup is null)
                        throw new EffectConfigException("duplicate-check 诊断列使用 @display 时必须提供 displayLookup。");
                    withinCells.Add(displayLookup.Value.DisplayCell);
                }
                else
                {
                    withinCells.Add("D." + EffectConditionCompiler.Identifier(field));
                }
            }
            var groupColumns = keyFields
                .Select(field => "D." + EffectConditionCompiler.Identifier(field))
                .Concat(withinCells)
                .ToList();
            var withinDocSql = "SELECT TOP " + CheckSupport.MaxRowsOf(root) + " "
                + (withinCells.Count > 0 ? string.Join(", ", withinCells) : "1")
                + " FROM dbo." + EffectConditionCompiler.Identifier(plan.DetailTable) + " D"
                + (displayLookup?.JoinSql ?? string.Empty)
                + " WHERE " + string.Join(" AND ", detailScope)
                + " GROUP BY " + string.Join(", ", groupColumns)
                + " HAVING COUNT(*) > 1";
            return new DuplicateCheckSql(
                withinDocSql, detailParameters, diagnosticFields, MultiRow: diagnosticFields.Length > 0);
        }

        if (!root.TryGetProperty("table", out var tableElement) || tableElement.ValueKind != JsonValueKind.String)
            throw new EffectConfigException("duplicate-check 缺少 table。");
        var table = tableElement.GetString()!.Trim();

        // 键来源：缺省即候选表同名列（MASTER 域）；显式 keySource 时按其域与列名对齐。
        var sourceScope = "MASTER";
        var sourceFields = keyFields;
        if (root.TryGetProperty("keySource", out var keySource) && keySource.ValueKind == JsonValueKind.Object)
        {
            sourceScope = keySource.TryGetProperty("scope", out var scopeElement) && scopeElement.ValueKind == JsonValueKind.String
                ? scopeElement.GetString()!.Trim().ToUpperInvariant()
                : "MASTER";
            sourceFields = CheckSupport.ParseStringArray(keySource, "fields");
            if (sourceFields.Length != keyFields.Length)
                throw new EffectConfigException("duplicate-check keyFields 与 keySource.fields 数量不一致。");
        }
        var sourceAlias = sourceScope switch
        {
            "MASTER" => "M",
            "DETAIL" => "D",
            _ => throw new EffectConfigException($"duplicate-check 来源域 '{sourceScope}' 仅允许 MASTER/DETAIL。"),
        };
        if (sourceAlias == "D" && plan.DetailTable is null)
            throw new EffectConfigException("duplicate-check 来源域 DETAIL 不可用（模块无明细表）。");

        var from = new StringBuilder("dbo.")
            .Append(EffectConditionCompiler.Identifier(plan.MasterTable!)).Append(" M");
        if (sourceAlias == "D")
            from.Append(" CROSS JOIN dbo.").Append(EffectConditionCompiler.Identifier(plan.DetailTable!)).Append(" D");
        from.Append(" CROSS JOIN dbo.").Append(EffectConditionCompiler.Identifier(table)).Append(" X WITH (NOLOCK)");

        var conditions = new List<string>();
        for (var index = 0; index < keyFields.Length; index++)
        {
            conditions.Add($"X.{EffectConditionCompiler.Identifier(keyFields[index])} = "
                + $"{sourceAlias}.{EffectConditionCompiler.Identifier(sourceFields[index])}");
        }

        var (documentScope, parameters) = CheckSupport.BuildDocumentScopeParts(plan, masterKeyValues, "M");
        // 自排除：候选行主键等于当前单据主键，即刚保存的这行本身。
        var selfExclusion = new List<string>();
        if (root.TryGetProperty("excludeSelf", out var excludeSelf) && excludeSelf.ValueKind == JsonValueKind.Object)
        {
            var selfKeys = CheckSupport.ParseStringArray(excludeSelf, "keyFields");
            if (selfKeys.Length == 0)
                throw new EffectConfigException("duplicate-check excludeSelf.keyFields 必须是非空数组。");
            if (selfKeys.Length != parameters.Count)
            {
                throw new EffectConfigException(
                    $"duplicate-check excludeSelf.keyFields 数量（{selfKeys.Length}）与单据主键数量（{parameters.Count}）不一致。");
            }
            for (var index = 0; index < selfKeys.Length; index++)
            {
                selfExclusion.Add($"X.{EffectConditionCompiler.Identifier(selfKeys[index])} = @mk{index}");
            }
        }
        if (selfExclusion.Count == 0
            && string.Equals(table, plan.MasterTable, StringComparison.OrdinalIgnoreCase))
        {
            // 候选表就是主表时，刚保存的行必然命中自身键值；缺自排除即等于永远拒绝保存。
            throw new EffectConfigException(
                $"duplicate-check 目标表与模块主表同名（{table}）时必须声明 excludeSelf.keyFields。");
        }

        // 被本单引用的候选行排除（excludeVia）：例如"客户订单号在本单所引用的原单之外不得重复"——
        // 排除表按约定携带本模块主表主键列，据此限定在当前单据内；join 把排除表的相关列
        // 与候选行（TARGET→X）或本单主表行（MASTER→M）对齐。
        if (root.TryGetProperty("excludeVia", out var excludeVia) && excludeVia.ValueKind == JsonValueKind.Object)
        {
            var viaTable = CheckSupport.RequiredString(excludeVia, "table", "duplicate-check excludeVia 缺少 table。");
            var viaJoin = new List<string>();
            if (excludeVia.TryGetProperty("join", out var viaJoinArray) && viaJoinArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var pair in viaJoinArray.EnumerateArray())
                {
                    var targetColumn = CheckSupport.RequiredString(pair, "target", "duplicate-check excludeVia.join 缺少 target。");
                    var source = pair.TryGetProperty("source", out var declaredSource) ? declaredSource : default;
                    var viaScopeName = source.ValueKind == JsonValueKind.Object
                        && source.TryGetProperty("scope", out var scopeValue) && scopeValue.ValueKind == JsonValueKind.String
                            ? scopeValue.GetString()!.Trim().ToUpperInvariant()
                            : "TARGET";
                    var sourceField = CheckSupport.RequiredString(source, "field", "duplicate-check excludeVia.join 缺少 source.field。");
                    var sourceSide = viaScopeName switch
                    {
                        "TARGET" => "X",
                        "MASTER" => "M",
                        _ => throw new EffectConfigException(
                            $"duplicate-check excludeVia.join 来源域 '{viaScopeName}' 仅允许 TARGET/MASTER。"),
                    };
                    viaJoin.Add($"V.{EffectConditionCompiler.Identifier(targetColumn)} = "
                        + $"{sourceSide}.{EffectConditionCompiler.Identifier(sourceField)}");
                }
            }
            if (viaJoin.Count == 0)
                throw new EffectConfigException("duplicate-check excludeVia.join 必须是非空数组。");
            // 排除表的单据范围与本单主表作用域同名同值（@mk*），只登记一次参数即可。
            var (viaScope, _) = CheckSupport.BuildDocumentScopeParts(plan, masterKeyValues, "V");
            conditions.Add("NOT EXISTS (SELECT 1 FROM dbo." + EffectConditionCompiler.Identifier(viaTable)
                + " V WITH (NOLOCK) WHERE " + string.Join(" AND ", viaScope.Concat(viaJoin)) + ")");
        }

        var filterSql = string.Empty;
        if (root.TryGetProperty("filter", out var filter) && filter.ValueKind == JsonValueKind.Object)
        {
            // 过滤条件只作用于候选行（TARGET→X）或当前单据行；算子与取值全走闭式条件编译器。
            var compiled = new EffectConditionCompiler().Compile(
                filter,
                (scope, _) => scope.ToUpperInvariant() switch
                {
                    "TARGET" => "X",
                    "MASTER" => "M",
                    "DETAIL" => sourceAlias == "D"
                        ? "D"
                        : throw new EffectConfigException("duplicate-check filter 来源域 DETAIL 不可用。"),
                    _ => null,
                },
                _ => true,
                outerAlias: "X");
            filterSql = compiled.Sql;
            parameters.AddRange(compiled.Parameters);
        }

        var selected = diagnostics.Length > 0
            ? string.Join(", ", diagnostics.Select(field => "X." + EffectConditionCompiler.Identifier(field)))
            : "1";
        var sql = new StringBuilder("SELECT TOP 1 ").Append(selected)
            .Append(" FROM ").Append(from)
            .Append(" WHERE ").Append(string.Join(" AND ", documentScope))
            .Append(" AND ").Append(string.Join(" AND ", conditions));
        if (selfExclusion.Count > 0)
            sql.Append(" AND NOT (").Append(string.Join(" AND ", selfExclusion)).Append(")");
        if (filterSql.Length > 0)
            sql.Append(" AND (").Append(filterSql).Append(")");

        return new DuplicateCheckSql(sql.ToString(), parameters, diagnostics);
    }

    /// <summary>
    /// Substitutes {COLUMN} placeholders in the configured message with the candidate row's
    /// values. Unknown placeholders stay verbatim so a misconfigured message is visible
    /// rather than silently emptied.
    /// </summary>
    internal static string RenderDiagnosticMessage(
        string? template,
        IReadOnlyList<string> diagnostics,
        IReadOnlyList<string?> values)
    {
        if (string.IsNullOrWhiteSpace(template))
            return "数据重复。";
        var text = template;
        for (var index = 0; index < diagnostics.Count; index++)
        {
            var value = index < values.Count ? values[index]?.Trim() ?? string.Empty : string.Empty;
            text = Regex.Replace(
                text,
                "\\{" + Regex.Escape(diagnostics[index]) + "\\}",
                value.Replace("$", "$$"),
                RegexOptions.IgnoreCase);
        }
        return text;
    }
}
