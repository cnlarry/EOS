using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data.Effects.ValidationChecks;

/// <summary>
/// qty-not-exceed：按 match 定位键把本单侧数量与被引用侧的使用量/限额比较，命中即违规。
/// 四种模式共用一条比较式骨架（超限额 / 不得低于进度 / 不得低于已发生量 / 不得超过上限），
/// 由 <c>mode</c> 选择。三种形态会改变取数方式，都必须显式声明、不允许隐式推导：
/// · <c>thisQty.agg=SUM</c>：源侧按 match 键分组求和后再比（逐行比会悄悄放宽约束）；
/// · <c>targetAgg</c>：被引用侧先按定位键聚合成一行（取任意一行会比旧判据更严）；
/// · <c>usageOnly</c>：判据两侧都取自被引用侧，本单项取常量 0。
/// </summary>
internal static class QuantityNotExceedCheck
{
    /// <summary>数量判据的一个"量纲"：本单量（thisQty）、已发生量与限额三组字段，外加可选容差。</summary>
    private sealed record QuantityDimension(
        IReadOnlyList<EffectTerm> Terms,
        IReadOnlyList<string> UsageFields,
        IReadOnlyList<string> LimitFields,
        decimal Offset,
        bool Aggregate);

    /// <summary>分组形态诊断列的源列聚合声明。</summary>
    private sealed record DiagnosticAggregate(int FieldIndex, string Field, string Aggregate, string Alias);

    internal static async Task<string?> RunAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        ModuleEffectPlan plan,
        EffectValidationPlan rule,
        string stage,
        IReadOnlyList<string> masterKeyValues,
        CancellationToken token)
    {
        var root = rule.Params;
        var mode = root.TryGetProperty("mode", out var modeElement) && modeElement.ValueKind == JsonValueKind.String
            ? modeElement.GetString()!
            : "usage-not-exceed";
        if (!root.TryGetProperty("checks", out var checks) || checks.ValueKind != JsonValueKind.Array)
            throw new EffectConfigException("qty-not-exceed 缺少 checks 数组。");

        foreach (var check in checks.EnumerateArray())
        {
            if (await CheckSupport.ShouldSkipOnSwitchAsync(connection, transaction, plan, check, token))
                continue;
            var match = ParseMatchPairs(check);
            if (match.Count == 0)
                throw new EffectConfigException("qty-not-exceed.check 缺少 match 定位键。");
            // 量纲：单量纲直接写在 check 上；多量纲写在 dimensions 数组里。旧实现常把"数量"与
            // "备品"两类判据合成一句 `WHERE a OR b`——多量纲正是这个形态，按 OR 合并成一个
            // 违规判据，命中时诊断行只输出一次（与旧实现的单条 SELECT 一致）。
            // usageOnly：判据两侧都取自被引用侧（本单侧贡献为 0），如"退料合计（跨单累计）> 收料合计"。
            // 此时比较式退化为 `usage(T) > limit(T)`，本单项取常量 0。
            var usageOnly = check.TryGetProperty("usageOnly", out var usageOnlyElement)
                && usageOnlyElement.ValueKind == JsonValueKind.True;
            var dimensions = ParseQuantityDimensions(check);
            var multiDimension = check.TryGetProperty("dimensions", out _);
            // not-below-usage 只比较"本单量 vs 已发生量"，没有上限列，故不要求 limit（与注册表口径一致）。
            var usesLimit = !mode.Equals("not-below-usage", StringComparison.OrdinalIgnoreCase);
            var usesUsage = !mode.Equals("this-not-exceed", StringComparison.OrdinalIgnoreCase);
            foreach (var dimension in dimensions)
            {
                if ((!usageOnly && dimension.Terms.Count == 0) || (usesLimit && dimension.LimitFields.Count == 0))
                    throw new EffectConfigException("qty-not-exceed.check 缺少 thisQty/limit。");
                if (usesUsage && dimension.UsageFields.Count == 0)
                    throw new EffectConfigException("qty-not-exceed.check 缺少 thisQty/usage。");
                if (usageOnly && dimension.Aggregate)
                    throw new EffectConfigException("qty-not-exceed.check 的 usageOnly 与 thisQty.agg=SUM 互斥。");
            }

            var targetTable = check.TryGetProperty("targetTable", out var tt) && tt.ValueKind == JsonValueKind.String
                ? tt.GetString()!.Trim()
                : throw new EffectConfigException("qty-not-exceed.check 缺少 targetTable（fail-closed，禁止隐式推导）。");
            var sourceTable = match[0].Source.Scope.Equals("DETAIL", StringComparison.OrdinalIgnoreCase)
                ? plan.DetailTable
                : plan.MasterTable;
            if (sourceTable is null)
                throw new EffectConfigException("qty-not-exceed 来源表不可用（模块形态不足）。");

            var termSqls = usageOnly
                ? dimensions.Select(_ => "0").ToList()
                : dimensions.Select(dimension => BuildTermSql(dimension.Terms, "S")).ToList();
            var (documentScope, parameters) = BuildDocumentScope(plan, masterKeyValues);

            // 聚合形态：旧实现多按单据分组求和后再比较（如"同一采购行的收料合计"）。
            // 逐行比较在"同一单据里同一引用键有多行"时会弱于旧判据（单行没超、合计已超），
            // 等于悄悄放宽约束，故 thisQty.agg=SUM 时先按 match 键分组求和再比。
            var grouped = dimensions.Any(dimension => dimension.Aggregate);
            var diagnosticAggregates = ParseDiagnosticAggregates(check, grouped);
            var sourceRowDiagnostics = ParseSourceRowDiagnostics(check, grouped);
            // 被引用行聚合：旧实现按定位键 `MAX(列)` 取值（如同一制令工序有多条制程行时取允许量的最大值），
            // 直接取"任意一行"会比旧判据更严，故需要时把被引用表先按定位键聚合成一行。
            var targetAggregate = ParseTargetAggregate(check);
            string fromSql;
            string correlation;
            if (grouped)
            {
                var groupBy = string.Join(", ", match.Select(pair =>
                    "S." + EffectConditionCompiler.Identifier(pair.Source.Field!)));
                correlation = string.Join(" AND ", match.Select(pair =>
                    "T." + EffectConditionCompiler.Identifier(pair.TargetColumn) + " = S1." + EffectConditionCompiler.Identifier(pair.Source.Field!)));
                // 分组形态的诊断列若要求对源列聚合（如"该批次的最大序号"），必须把该聚合放进分组子查询，
                // 外层再投影——分组键之外的原列在 S1 里取不到。
                var extras = string.Concat(diagnosticAggregates.Select(item =>
                    ", " + item.Aggregate + "(S." + EffectConditionCompiler.Identifier(item.Field)
                    + ") AS [" + item.Alias + "]"));
                var projections = new StringBuilder();
                for (var index = 0; index < termSqls.Count; index++)
                {
                    projections.Append(", SUM(").Append(termSqls[index]).Append(") AS ")
                        .Append(ThisQtyAlias(index, multiDimension));
                }
                fromSql = "(SELECT " + groupBy + projections + extras + " FROM dbo."
                    + EffectConditionCompiler.Identifier(sourceTable) + " S WHERE " + documentScope
                    + " GROUP BY " + groupBy + ") S1 CROSS JOIN "
                    + TargetSourceSql(targetTable, match, targetAggregate, dimensions, check);
            }
            else
            {
                correlation = BuildMatchCorrelation(match, "S", "T");
                fromSql = "dbo." + EffectConditionCompiler.Identifier(sourceTable) + " S CROSS JOIN "
                    + TargetSourceSql(targetTable, match, targetAggregate, dimensions, check);
            }

            // 比较形态：本条超限额（usage + this > limit）、按进度不得减少（limit + this < usage）、
            // 不得低于已发生量（this < usage，用于"变更后数量不得小于已发生量"且无上限列的场景）、
            // 本条不得超过被引用行上限（this > limit）。多量纲逐条编译后按 OR 合并。
            var comparisons = new List<string>(dimensions.Count);
            for (var index = 0; index < dimensions.Count; index++)
            {
                var dimension = dimensions[index];
                var usageSql = string.Join(" + ", dimension.UsageFields.Select(field =>
                    $"COALESCE(T.{EffectConditionCompiler.Identifier(field)}, 0)"));
                var limitSql = string.Join(" + ", dimension.LimitFields.Select(field =>
                    $"COALESCE(T.{EffectConditionCompiler.Identifier(field)}, 0)"));
                // 容差：旧实现常带 0.1 之类的余量（如收料不超采购 +0.1），注册表已允许 offset，此处落实。
                var limitWithOffset = dimension.Offset == 0m
                    ? limitSql
                    : $"({limitSql} + {dimension.Offset.ToString(CultureInfo.InvariantCulture)})";
                var thisSql = usageOnly ? "0" : grouped ? "S1." + ThisQtyAlias(index, multiDimension) : termSqls[index];
                comparisons.Add(mode.Equals("not-below-progress", StringComparison.OrdinalIgnoreCase)
                    ? $"{limitSql} + {thisSql} < {usageSql}" // reduction would fall below accumulated progress
                    : mode.Equals("this-not-exceed", StringComparison.OrdinalIgnoreCase)
                        ? $"{thisSql} > {limitWithOffset}" // document quantity must not exceed the referenced cap
                        : mode.Equals("not-below-usage", StringComparison.OrdinalIgnoreCase)
                            ? $"{thisSql} < {usageSql}" // document quantity must not fall below what already happened
                            : $"{usageSql} + {thisSql} > {limitWithOffset}");
            }
            var comparison = comparisons.Count == 1 ? comparisons[0] : "(" + string.Join(" OR ", comparisons) + ")";

            // 来源行的单据范围：分组形态已把范围写进子查询，逐行形态必须作为顶层谓词补上，
            // 否则比较的是全库历史行，他单的超量会拦下本次操作（同 reference-exists 的旧缺陷）。
            var predicate = grouped
                ? correlation + " AND " + comparison
                : correlation + " AND " + documentScope + " AND " + comparison;

            var sql = new StringBuilder("SELECT TOP 1 1 FROM ")
                .Append(fromSql).Append(" WHERE ").Append(predicate);
            await using var command = new SqlCommand(sql.ToString(), connection, transaction);
            foreach (var parameter in parameters)
            {
                command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
            }
            if (await command.ExecuteScalarAsync(token) is not null)
            {
                var message = rule.Message
                    ?? (check.TryGetProperty("message", out var checkMessage)
                        && checkMessage.ValueKind == JsonValueKind.String
                        && !string.IsNullOrWhiteSpace(checkMessage.GetString())
                            ? checkMessage.GetString()!
                            : $"存在{(mode == "not-below-progress" ? "低于进度" : mode == "not-below-usage" ? "低于已发生量" : "超出限额")}的明细行（{stage}）。");
                var cells = sourceRowDiagnostics
                    ? BuildSourceRowDiagnosticCells(check)
                    : BuildQtyDiagnosticCells(check, grouped, multiDimension, diagnosticAggregates);
                if (cells is null)
                    return message;
                // 诊断行的拼接方式各族不同：多为"列间 4 空格、行间 CRLF"，也有"单列多值、一行内联"（值间 2 空格）。
                var cellSeparator = check.TryGetProperty("diagnosticCellSeparator", out var cellElement)
                    && cellElement.ValueKind == JsonValueKind.String ? cellElement.GetString()! : "    ";
                var rowSeparator = check.TryGetProperty("diagnosticRowSeparator", out var rowElement)
                    && rowElement.ValueKind == JsonValueKind.String ? rowElement.GetString()! : "\r\n";
                // 诊断口径一：每个违规分组一行（默认）。口径二（diagnosticRows=SOURCE）：列出违规分组下的
                // **源明细行**，如"以下序号项数量超过允许值"要回报本单所有越界明细的序号。
                var diagnosticSql = sourceRowDiagnostics
                    ? BuildSourceRowDiagnosticSql(check, cells, match, sourceTable!, fromSql, predicate, documentScope)
                    : "SELECT TOP (" + CheckSupport.MaxRowsOf(check).ToString(CultureInfo.InvariantCulture) + ") "
                        + string.Join(", ", cells) + " FROM " + fromSql + " WHERE " + predicate;
                var lines = new List<string>();
                await using var lineCommand = new SqlCommand(diagnosticSql, connection, transaction);
                foreach (var parameter in parameters)
                    lineCommand.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
                await using var reader = await lineCommand.ExecuteReaderAsync(token);
                while (await reader.ReadAsync(token))
                {
                    var row = new List<string>(cells.Count);
                    for (var index = 0; index < cells.Count; index++)
                        row.Add(reader.IsDBNull(index) ? string.Empty : CheckSupport.FormatCell(reader, index));
                    // 旧实现的诊断行多为"列间 4 空格、行间 CRLF"（少数为单列内联），逐字保持。
                    lines.Add(string.Join(cellSeparator, row));
                }
                if (lines.Count == 0)
                    return message;
                return message.Replace("{ROWS}", string.Join(rowSeparator, lines));
            }
        }
        return null;
    }

    /// <summary>
    /// 被引用表的取数形式：默认直接引用（`dbo.TBL T`）；声明 `targetAgg` 时先按 match 的定位键
    /// 聚合成一行再参与比较（`MAX/MIN/SUM`，如"同一制令工序有多条制程行时取允许量最大值"）。
    /// 聚合列 = usage/limit 与 TARGET 诊断用到的列（定位键列原样投影）。
    /// </summary>
    private static string TargetSourceSql(
        string targetTable,
        IReadOnlyList<EffectMatchItem> match,
        string? targetAggregate,
        IReadOnlyList<QuantityDimension> dimensions,
        JsonElement check)
    {
        var table = "dbo." + EffectConditionCompiler.Identifier(targetTable);
        if (targetAggregate is null)
            return table + " T";
        var keys = match.Select(pair => pair.TargetColumn).ToList();
        var aggregated = new List<string>();
        foreach (var column in dimensions.SelectMany(item => item.UsageFields.Concat(item.LimitFields))
                     .Concat(DiagnosticTargetColumns(check)))
        {
            if (!keys.Contains(column, StringComparer.OrdinalIgnoreCase) && !aggregated.Contains(column, StringComparer.OrdinalIgnoreCase))
                aggregated.Add(column);
        }
        var projections = keys.Select(key => "T0." + EffectConditionCompiler.Identifier(key))
            .Concat(aggregated.Select(column => targetAggregate + "(T0." + EffectConditionCompiler.Identifier(column)
                + ") AS " + EffectConditionCompiler.Identifier(column)));
        return "(SELECT " + string.Join(", ", projections) + " FROM " + table + " T0 GROUP BY "
            + string.Join(", ", keys.Select(key => "T0." + EffectConditionCompiler.Identifier(key))) + ") T";
    }

    private static IEnumerable<string> DiagnosticTargetColumns(JsonElement check)
    {
        if (!check.TryGetProperty("diagnosticFields", out var fields) || fields.ValueKind != JsonValueKind.Array)
            yield break;
        foreach (var field in fields.EnumerateArray())
        {
            if (field.ValueKind != JsonValueKind.Object)
                continue;
            var scope = field.TryGetProperty("scope", out var scopeElement) && scopeElement.ValueKind == JsonValueKind.String
                ? scopeElement.GetString()!.Trim().ToUpperInvariant()
                : "SOURCE";
            if (scope != "TARGET")
                continue;
            if (field.TryGetProperty("field", out var nameElement) && nameElement.ValueKind == JsonValueKind.String)
                yield return nameElement.GetString()!.Trim();
        }
    }

    /// <summary>
    /// `targetAgg`（被引用行按定位键聚合后取值，闭集 MAX/MIN/SUM）：整条 check 共用一个聚合函数——
    /// 不同列用不同聚合会让"哪一列是什么口径"无从判断，配置侧也按此校验。
    /// </summary>
    private static string? ParseTargetAggregate(JsonElement check)
    {
        if (!check.TryGetProperty("targetAgg", out var element) || element.ValueKind != JsonValueKind.String)
            return null;
        var value = element.GetString()!.Trim().ToUpperInvariant();
        return value switch
        {
            "MAX" or "MIN" or "SUM" => value,
            _ => throw new EffectConfigException($"qty-not-exceed.targetAgg '{value}' 不在闭集内（MAX/MIN/SUM）。"),
        };
    }

    /// <summary>
    /// `diagnosticRows=SOURCE`：诊断不按"每组一行"输出，而是列出违规分组下的**源明细行**
    /// （如"以下序号项数量超过允许值"回报本单每条越界明细的序号）。要求来源走分组形态。
    /// </summary>
    private static bool ParseSourceRowDiagnostics(JsonElement check, bool grouped)
    {
        if (!check.TryGetProperty("diagnosticRows", out var element) || element.ValueKind != JsonValueKind.String)
            return false;
        var value = element.GetString()!.Trim().ToUpperInvariant();
        if (value != "SOURCE")
            throw new EffectConfigException($"qty-not-exceed.diagnosticRows '{value}' 不在闭集内（仅 SOURCE）。");
        if (!grouped)
            throw new EffectConfigException("qty-not-exceed.diagnosticRows=SOURCE 需要 thisQty.agg=SUM 的分组形态。");
        return true;
    }

    /// <summary>
    /// `diagnosticRows=SOURCE` 的诊断列：只能是**源明细列**（外层查询里没有被引用行、也没有分组
    /// 求和投影可引用，故一律取原始来源别名 `S`）。
    /// </summary>
    private static List<string>? BuildSourceRowDiagnosticCells(JsonElement check)
    {
        if (!check.TryGetProperty("diagnosticFields", out var fields) || fields.ValueKind != JsonValueKind.Array
            || fields.GetArrayLength() == 0)
            return null;
        var cells = new List<string>();
        foreach (var field in fields.EnumerateArray())
        {
            var name = field.ValueKind == JsonValueKind.String
                ? field.GetString()!.Trim()
                : field.ValueKind == JsonValueKind.Object
                    && field.TryGetProperty("field", out var nameElement)
                    && nameElement.ValueKind == JsonValueKind.String
                        ? nameElement.GetString()!.Trim()
                        : throw new EffectConfigException("qty-not-exceed.diagnosticFields 缺少 field。");
            cells.Add("S." + EffectConditionCompiler.Identifier(name));
        }
        return cells;
    }

    /// <summary>
    /// 逐源明细行的诊断查询：把违规分组（按 match 键定位）与源明细表重新连接，
    /// 列出每个违规分组下的明细行，按第一个诊断列排序（与旧实现 `ORDER BY SERIAL_NO` 一致）。
    /// </summary>
    private static string BuildSourceRowDiagnosticSql(
        JsonElement check,
        IReadOnlyList<string> cells,
        IReadOnlyList<EffectMatchItem> match,
        string sourceTable,
        string fromSql,
        string predicate,
        string documentScope)
    {
        var keys = string.Join(", ", match.Select(pair =>
            "S1." + EffectConditionCompiler.Identifier(pair.Source.Field!)));
        var join = string.Join(" AND ", match.Select(pair =>
            "V." + EffectConditionCompiler.Identifier(pair.Source.Field!) + " = S."
            + EffectConditionCompiler.Identifier(pair.Source.Field!)));
        return "SELECT TOP (" + CheckSupport.MaxRowsOf(check).ToString(CultureInfo.InvariantCulture) + ") "
            + string.Join(", ", cells)
            + " FROM dbo." + EffectConditionCompiler.Identifier(sourceTable) + " S"
            + " INNER JOIN (SELECT " + keys + " FROM " + fromSql + " WHERE " + predicate + ") V ON " + join
            + " WHERE " + documentScope
            + " ORDER BY " + cells[0];
    }

    /// <summary>
    /// 命中行的诊断列（可选）：SOURCE 指来源别名（分组形态下是分组键所在的子查询）、TARGET 指被引用行、
    /// THIS 指本单数量（分组时为求和值；多量纲时用 `dimension` 指定第几组，省略即第一组）。
    /// 未配置返回 null，此时消息只回表头。
    /// 分组形态下 `{"scope":"SOURCE","field":…,"agg":"MAX|MIN|SUM"}` 取该源列的聚合值（如批次最大序号）。
    /// </summary>
    private static List<string>? BuildQtyDiagnosticCells(
        JsonElement check,
        bool aggregate,
        bool multiDimension,
        IReadOnlyList<DiagnosticAggregate> diagnosticAggregates)
    {
        if (!check.TryGetProperty("diagnosticFields", out var fields) || fields.ValueKind != JsonValueKind.Array
            || fields.GetArrayLength() == 0)
            return null;
        var sourceAlias = aggregate ? "S1" : "S";
        var cells = new List<string>();
        var index = 0;
        foreach (var field in fields.EnumerateArray())
        {
            var aggregateColumn = diagnosticAggregates.FirstOrDefault(item => item.FieldIndex == index);
            index++;
            var scope = "SOURCE";
            var name = field.ValueKind == JsonValueKind.String ? field.GetString()!.Trim() : null;
            if (name is null)
            {
                scope = field.TryGetProperty("scope", out var scopeElement) && scopeElement.ValueKind == JsonValueKind.String
                    ? scopeElement.GetString()!.Trim().ToUpperInvariant()
                    : "SOURCE";
                // THIS 是"本单数量"，分组形态下即求和值，无需列名。
                name = field.TryGetProperty("field", out var nameElement) && nameElement.ValueKind == JsonValueKind.String
                    ? nameElement.GetString()!.Trim()
                    : scope == "THIS"
                        ? null
                        : throw new EffectConfigException("qty-not-exceed.diagnosticFields 缺少 field。");
            }            if (aggregateColumn is not null)
            {
                cells.Add(sourceAlias + ".[" + aggregateColumn.Alias + "]");
                continue;
            }
            cells.Add(scope switch
            {
                // 分组形态的"本单量"是求和后的投影列；多量纲时用 dimension 选第几组
                // （省略即第一个量纲，与既有单量纲配置一致；非分组形态仍按列名取原值）。
                "THIS" => aggregate
                    ? sourceAlias + "." + ThisQtyAlias(DiagnosticDimension(field) - 1, multiDimension)
                    : "S." + EffectConditionCompiler.Identifier(name ?? throw new EffectConfigException("qty-not-exceed.diagnosticFields 的 THIS 作用域在非分组形态下必须给出 field。")),
                "TARGET" => "T." + EffectConditionCompiler.Identifier(name!),
                _ => sourceAlias + "." + EffectConditionCompiler.Identifier(name!),
            });
        }
        return cells;
    }

    /// <summary>
    /// 诊断列里的"The 本单量属第几个量纲"（1 起；省略即第一组）。多量纲判据里
    /// "数量"与"备品"两类求和值要在同一诊断行分别输出，靠这个序号区分。
    /// </summary>
    private static int DiagnosticDimension(JsonElement field)
        => field.TryGetProperty("dimension", out var dimensionElement)
            && dimensionElement.ValueKind == JsonValueKind.Number
            && dimensionElement.TryGetInt32(out var dimension) && dimension >= 1
                ? dimension
                : 1;

    /// <summary>
    /// 诊断列里的源列聚合声明（分组形态专用）：字段序号 → 子查询里的聚合列别名。
    /// 非分组形态出现聚合声明即拒绝（那里没有 GROUP BY，聚合会把整表塌成一行）。
    /// </summary>
    private static IReadOnlyList<DiagnosticAggregate> ParseDiagnosticAggregates(JsonElement check, bool aggregate)
    {
        if (!check.TryGetProperty("diagnosticFields", out var fields) || fields.ValueKind != JsonValueKind.Array)
            return Array.Empty<DiagnosticAggregate>();
        var result = new List<DiagnosticAggregate>();
        var index = 0;
        foreach (var field in fields.EnumerateArray())
        {
            var current = index++;
            if (field.ValueKind != JsonValueKind.Object
                || !field.TryGetProperty("agg", out var aggElement) || aggElement.ValueKind != JsonValueKind.String)
                continue;
            var agg = aggElement.GetString()!.Trim().ToUpperInvariant();
            var function = agg switch
            {
                "MAX" or "DISTINCT" => "MAX",
                "MIN" => "MIN",
                "SUM" => "SUM",
                _ => throw new EffectConfigException($"qty-not-exceed.diagnosticFields.agg '{agg}' 不在闭集内（MAX/MIN/SUM/DISTINCT）。"),
            };
            if (!aggregate)
                throw new EffectConfigException("qty-not-exceed.diagnosticFields.agg 仅在 thisQty.agg=SUM 的分组形态下可用。");
            var name = field.TryGetProperty("field", out var nameElement) && nameElement.ValueKind == JsonValueKind.String
                ? nameElement.GetString()!.Trim()
                : throw new EffectConfigException("qty-not-exceed.diagnosticFields 带 agg 时必须给出 field。");
            var scope = field.TryGetProperty("scope", out var scopeElement) && scopeElement.ValueKind == JsonValueKind.String
                ? scopeElement.GetString()!.Trim().ToUpperInvariant()
                : "SOURCE";
            if (scope != "SOURCE")
                throw new EffectConfigException("qty-not-exceed.diagnosticFields 带 agg 时 scope 只能是 SOURCE。");
            result.Add(new DiagnosticAggregate(current, name, function, $"__DIAG_{current}"));
        }
        return result;
    }

    /// <summary>
    /// Restricts the source rows to the current document by master primary-key values;
    /// comparing without the document scope would double-count previously received rows.
    /// </summary>
    private static (string Sql, List<EffectSqlParameter> Parameters) BuildDocumentScope(
        ModuleEffectPlan plan,
        IReadOnlyList<string> masterKeyValues)
    {
        var (parts, parameters) = CheckSupport.BuildDocumentScopeParts(plan, masterKeyValues, "S");
        return (string.Join(" AND ", parts), parameters);
    }

    /// <summary>分组形态下"本单量"投影列：单量纲沿用 `[__THIS_QTY]`，多量纲按序号取别名。</summary>
    private static string ThisQtyAlias(int index, bool multiDimension)
        => multiDimension ? $"[__THIS_QTY_{index + 1}]" : "[__THIS_QTY]";

    private static QuantityDimension ParseQuantityDimension(JsonElement owner)
    {
        var offset = owner.TryGetProperty("offset", out var offsetElement) && offsetElement.ValueKind == JsonValueKind.Number
            && offsetElement.TryGetDecimal(out var declaredOffset)
                ? declaredOffset
                : 0m;
        var aggregate = owner.TryGetProperty("thisQty", out var thisQtyElement)
            && thisQtyElement.ValueKind == JsonValueKind.Object
            && thisQtyElement.TryGetProperty("agg", out var aggElement)
            && aggElement.ValueKind == JsonValueKind.String
            && aggElement.GetString()!.Equals("SUM", StringComparison.OrdinalIgnoreCase);
        return new QuantityDimension(
            ParseScopeTerms(owner, "thisQty"),
            ParseFieldList(owner, "usage"),
            ParseFieldList(owner, "limit"),
            offset,
            aggregate);
    }

    private static IReadOnlyList<QuantityDimension> ParseQuantityDimensions(JsonElement check)
    {
        if (!check.TryGetProperty("dimensions", out var dimensionsElement)
            || dimensionsElement.ValueKind != JsonValueKind.Array)
            return [ParseQuantityDimension(check)];
        var dimensions = new List<QuantityDimension>();
        foreach (var dimension in dimensionsElement.EnumerateArray())
        {
            if (dimension.ValueKind != JsonValueKind.Object)
                throw new EffectConfigException("qty-not-exceed.dimensions[] 必须是对象。");
            dimensions.Add(ParseQuantityDimension(dimension));
        }
        if (dimensions.Count == 0)
            throw new EffectConfigException("qty-not-exceed.dimensions 不能为空数组。");
        // 各量纲必须一致地分组或不分组：否则分组子查询与逐行比较混在一句 SQL 里无意义。
        if (dimensions.Select(item => item.Aggregate).Distinct().Count() > 1)
            throw new EffectConfigException("qty-not-exceed.dimensions 的各量纲必须一致地使用（或不使用）thisQty.agg=SUM。");
        return dimensions;
    }

    private static IReadOnlyList<EffectMatchItem> ParseMatchPairs(JsonElement check)
    {
        if (!check.TryGetProperty("match", out var match) || match.ValueKind != JsonValueKind.Array)
            return Array.Empty<EffectMatchItem>();
        var items = new List<EffectMatchItem>();
        foreach (var pair in match.EnumerateArray())
        {
            var target = pair.TryGetProperty("target", out var t) && t.ValueKind == JsonValueKind.String
                ? t.GetString()!.Trim()
                : null;
            var source = pair.TryGetProperty("source", out var s) ? s : default;
            var scope = source.ValueKind == JsonValueKind.Object
                && source.TryGetProperty("scope", out var sc) && sc.ValueKind == JsonValueKind.String
                    ? sc.GetString()!.Trim().ToUpperInvariant()
                    : null;
            var field = source.ValueKind == JsonValueKind.Object
                && source.TryGetProperty("field", out var f) && f.ValueKind == JsonValueKind.String
                    ? f.GetString()!.Trim()
                    : null;
            if (target is null || scope is null || field is null)
                throw new EffectConfigException("校验 match 结构非法。");
            items.Add(new EffectMatchItem(target, new EffectSourceRef(scope, null, field, null)));
        }
        return items;
    }

    private static IReadOnlyList<EffectTerm> ParseScopeTerms(JsonElement check, string name)
    {
        if (!check.TryGetProperty(name, out var scope) || scope.ValueKind != JsonValueKind.Object)
            return Array.Empty<EffectTerm>();
        if (!scope.TryGetProperty("terms", out var terms) || terms.ValueKind != JsonValueKind.Array)
            return Array.Empty<EffectTerm>();
        var result = new List<EffectTerm>();
        foreach (var term in terms.EnumerateArray())
        {
            var field = term.GetProperty("field").GetString()!.Trim();
            var coef = term.TryGetProperty("coef", out var c) && c.TryGetInt32(out var n) ? n : 1;
            result.Add(new EffectTerm(field, coef));
        }
        return result;
    }

    private static IReadOnlyList<string> ParseFieldList(JsonElement check, string name)
    {
        if (!check.TryGetProperty(name, out var usage) || usage.ValueKind != JsonValueKind.Object)
            return Array.Empty<string>();
        return CheckSupport.ParseStringArray(usage, "fields");
    }

    private static string BuildTermSql(IReadOnlyList<EffectTerm> terms, string alias)
    {
        var parts = new List<string>();
        foreach (var term in terms)
        {
            var column = $"COALESCE({alias}.{EffectConditionCompiler.Identifier(term.Field)}, 0)";
            parts.Add(term.Coef == -1 ? "- " + column : (parts.Count == 0 ? "" : "+ ") + column);
        }
        var expression = string.Join(" ", parts);
        return terms[0].Coef == -1 ? "(" + expression + ")" : expression;
    }

    private static string BuildMatchCorrelation(IReadOnlyList<EffectMatchItem> match, string sourceAlias, string targetAlias) =>
        string.Join(" AND ", match.Select(item =>
            $"{sourceAlias}.{EffectConditionCompiler.Identifier(item.Source.Field!)} = {targetAlias}.{EffectConditionCompiler.Identifier(item.TargetColumn)}"));
}
