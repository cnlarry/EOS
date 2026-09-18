using System.Data;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data.Effects;

/// <summary>Validation chain rejected the event; message is user-facing.</summary>
public sealed class EffectValidationException(string message) : Exception(message);

/// <summary>
/// Runs the configured validation rule chain for one stage. Every failed validation
/// blocks (ADR: validation has no WARN mode). Implementations compile the closed
/// PARAM_STRUCT shapes of each template key into parameterized SQL; no user values are
/// ever concatenated into SQL.
/// </summary>
public sealed class EffectValidationExecutor
{
    private readonly EffectConditionCompiler _conditions = new();

    public async Task ValidateAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        ModuleEffectPlan plan,
        string stage,
        CancellationToken token,
        IReadOnlyList<string>? masterKeyValues = null)
    {
        var keyValues = masterKeyValues ?? Array.Empty<string>();
        foreach (var rule in plan.Rules)
        {
            if (!rule.Enabled || !rule.Stage.Equals(stage, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!await RuleAppliesAsync(connection, transaction, plan, rule, keyValues, token))
                continue;
            var violation = rule.ValidationKey.ToLowerInvariant() switch
            {
                "qty-not-exceed" => await CheckQuantityNotExceedAsync(connection, transaction, plan, rule, stage, keyValues, token),
                "reference-exists" => await CheckReferenceExistsAsync(connection, transaction, plan, rule, keyValues, token),
                "duplicate-check" => await CheckDuplicateAsync(connection, transaction, plan, rule, keyValues, token),
                "period-overlap" => await CheckPeriodOverlapAsync(connection, transaction, rule, keyValues, token),
                "line-require" => await CheckLineRequireAsync(connection, transaction, plan, rule, keyValues, token),
                "no-cycle" => await CheckNoCycleAsync(connection, transaction, plan, rule, keyValues, token),
                _ => throw new EffectConfigException($"校验键 '{rule.ValidationKey}' 尚未实现运行时执行。"),
            };
            if (violation is not null)
            {
                // 各校验返回的已是最终文案（含自身渲染的占位符替换）；rule.Message 的覆盖
                // 已在各校验内部完成，这里只做兜底，避免用原始模板盖掉渲染结果。
                throw new EffectValidationException(violation);
            }
        }
    }

    /// <summary>
    /// 规则级适用条件（params.when，可选）：条件不成立即跳过该校验。旧域规则普遍先读当前
    /// 单据的判据字段、为空则直接放行，这条键就是该语义的通用承载；条件走闭式条件编译器，
    /// 三值逻辑与 SQL 一致（NULL 比较为 UNKNOWN，即视为不适用）。
    /// </summary>
    private async Task<bool> RuleAppliesAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        ModuleEffectPlan plan,
        EffectValidationPlan rule,
        IReadOnlyList<string> masterKeyValues,
        CancellationToken token)
    {
        if (!rule.Params.TryGetProperty("when", out var when) || when.ValueKind != JsonValueKind.Object)
            return true;

        var compiled = new EffectConditionCompiler().Compile(
            when,
            (scope, _) => scope.ToUpperInvariant() switch
            {
                "MASTER" => "M",
                // 纯主表模块的明细表可能是空串（而非 null），按"无明细表"处理。
                "DETAIL" => string.IsNullOrWhiteSpace(plan.DetailTable)
                    ? throw new EffectConfigException("校验 when 来源域 DETAIL 不可用（模块无明细表）。")
                    : "D",
                _ => null,
            },
            _ => true,
            outerAlias: "M");
        var (documentScope, parameters) = BuildDocumentScopeParts(plan, masterKeyValues, "M");
        var from = "dbo." + EffectConditionCompiler.Identifier(plan.MasterTable!) + " M"
            + (string.IsNullOrWhiteSpace(plan.DetailTable)
                ? string.Empty
                : " CROSS JOIN dbo." + EffectConditionCompiler.Identifier(plan.DetailTable) + " D");
        var sql = "SELECT TOP 1 1 FROM " + from
            + " WHERE " + string.Join(" AND ", documentScope)
            + " AND (" + compiled.Sql + ")";
        await using var command = new SqlCommand(sql, connection, transaction);
        foreach (var parameter in parameters.Concat(compiled.Parameters))
            command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        return await command.ExecuteScalarAsync(token) is not null;
    }

    /// <summary>
    /// Optional per-check gate (closed operator, same semantics as the condition compiler
    /// switch): the check only applies while every declared gate matches. Two shapes are
    /// accepted — the legacy single gate <c>{"key":"&lt;SYSSS column&gt;","expect":1}</c> and
    /// the list form <c>{"gates":[{"scope":"SYSSS|MODULE","key":"...","expect":1}, …]}</c>
    /// (all gates must hold). The MODULE scope reads the current module's own column
    /// (e.g. ERROR_NO_SAVE), which is how the legacy per-module validation switch is
    /// carried over without making the switch inert: flag on = rule applies, flag off =
    /// rule skipped, exactly as the retired C# branch behaved. Unknown scopes and unknown
    /// columns fail closed.
    /// </summary>
    private static async Task<bool> ShouldSkipOnSwitchAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        ModuleEffectPlan plan,
        JsonElement check,
        CancellationToken token)
    {
        if (!check.TryGetProperty("switch", out var gate) || gate.ValueKind != JsonValueKind.Object)
            return false;
        foreach (var entry in EnumerateGates(gate))
        {
            if (await GateFailsAsync(connection, transaction, plan, entry, token))
                return true;
        }
        return false;
    }

    private static IEnumerable<JsonElement> EnumerateGates(JsonElement gate)
    {
        if (gate.TryGetProperty("key", out _))
            yield return gate;
        if (gate.TryGetProperty("gates", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in list.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    throw new EffectConfigException("校验 switch.gates 项必须是对象。");
                yield return item;
            }
        }
    }

    private static async Task<bool> GateFailsAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        ModuleEffectPlan plan,
        JsonElement entry,
        CancellationToken token)
    {
        var key = entry.TryGetProperty("key", out var keyElement) && keyElement.ValueKind == JsonValueKind.String
            ? keyElement.GetString()!.Trim()
            : throw new EffectConfigException("校验 switch 门控缺少 key。");
        var expect = entry.TryGetProperty("expect", out var expectElement) && expectElement.ValueKind == JsonValueKind.Number
            ? expectElement.GetInt32()
            : 1;
        var scope = entry.TryGetProperty("scope", out var scopeElement) && scopeElement.ValueKind == JsonValueKind.String
            ? scopeElement.GetString()!.Trim().ToUpperInvariant()
            : "SYSSS";
        var column = EffectConditionCompiler.Identifier(key);
        string sql;
        SqlCommand command;
        if (scope == "SYSSS")
        {
            sql = $"SELECT COALESCE(MAX(CAST({column} AS int)), 0) FROM dbo.SYSSS WITH (NOLOCK)";
            command = new SqlCommand(sql, connection, transaction);
        }
        else if (scope == "MODULE")
        {
            sql = $"SELECT COALESCE(MAX(CAST({column} AS int)), 0) FROM dbo.MODULES WITH (NOLOCK) WHERE M_IDX = @ModuleId";
            command = new SqlCommand(sql, connection, transaction);
            command.Parameters.Add("@ModuleId", SqlDbType.Int).Value = plan.ModuleId;
        }
        else
        {
            throw new EffectConfigException($"校验 switch 门控来源域 '{scope}' 不支持（仅 SYSSS / MODULE）。");
        }
        await using (command)
        {
            var actual = await command.ExecuteScalarAsync(token);
            return Convert.ToInt32(actual) != expect;
        }
    }

    private record CheckPlan(
        IReadOnlyList<EffectMatchItem> Match,
        IReadOnlyList<EffectTerm> ThisQty,
        IReadOnlyList<string> UsageFields,
        IReadOnlyList<string> LimitFields);

    private async Task<string?> CheckQuantityNotExceedAsync(
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
            if (await ShouldSkipOnSwitchAsync(connection, transaction, plan, check, token))
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
                    : "SELECT TOP (" + MaxRowsOf(check).ToString(CultureInfo.InvariantCulture) + ") "
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
                        row.Add(reader.IsDBNull(index) ? string.Empty : FormatCell(reader, index));
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
        return "SELECT TOP (" + MaxRowsOf(check).ToString(CultureInfo.InvariantCulture) + ") "
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

    /// <summary>分组形态诊断列的源列聚合声明。</summary>
    private sealed record DiagnosticAggregate(int FieldIndex, string Field, string Aggregate, string Alias);

    /// <summary>
    /// no-cycle（成环检测）：从当前单据主表行的起点值出发，按层展开"父列 → 子列"的引用关系
    /// （第 N 层 = 以第 N-1 层的子项作为父项继续展开），只要某一层出现"子项 = 起点"即判为循环，
    /// 回报该层父项值。逐层扫描、层内任取一行与旧过程 `P_BOM_CHECK` 一致。
    /// `maxDepth`（默认 100）用于把旧过程"没有回到起点的环会无限展开、把保存挂死"的形态
    /// 换成 fail-closed 拒绝；命中值经 `{ROWS}` 占位符渲染进文案。
    /// </summary>
    private async Task<string?> CheckNoCycleAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        ModuleEffectPlan plan,
        EffectValidationPlan rule,
        IReadOnlyList<string> masterKeyValues,
        CancellationToken token)
    {
        if (!rule.Params.TryGetProperty("checks", out var checks) || checks.ValueKind != JsonValueKind.Array)
            throw new EffectConfigException("no-cycle 缺少 checks 数组。");
        foreach (var check in checks.EnumerateArray())
        {
            if (await ShouldSkipOnSwitchAsync(connection, transaction, plan, check, token))
                continue;
            var table = RequiredText(check, "table", "no-cycle");
            var parentField = RequiredText(check, "parentField", "no-cycle");
            var childField = RequiredText(check, "childField", "no-cycle");
            if (!check.TryGetProperty("start", out var start) || start.ValueKind != JsonValueKind.Object)
                throw new EffectConfigException("no-cycle.check 缺少 start。");
            var startScope = (start.TryGetProperty("scope", out var startScopeElement)
                    && startScopeElement.ValueKind == JsonValueKind.String
                        ? startScopeElement.GetString()!.Trim().ToUpperInvariant()
                        : "MASTER");
            if (startScope != "MASTER")
                throw new EffectConfigException($"no-cycle.start.scope '{startScope}' 不在闭集内（仅 MASTER）。");
            var startField = RequiredText(start, "field", "no-cycle.start");
            var maxDepth = check.TryGetProperty("maxDepth", out var depthElement)
                && depthElement.ValueKind == JsonValueKind.Number && depthElement.TryGetInt32(out var depth)
                    ? depth
                    : 100;
            if (maxDepth is < 1 or > 1000)
                throw new EffectConfigException("no-cycle.check.maxDepth 必须在 1..1000 之间。");

            // 起点取值：当前单据主表行的指定列
            var (scopeParts, parameters) = BuildDocumentScopeParts(plan, masterKeyValues, "M");
            string? startValue;
            await using (var read = new SqlCommand("SELECT TOP 1 M." + EffectConditionCompiler.Identifier(startField)
                + " FROM dbo." + EffectConditionCompiler.Identifier(plan.MasterTable!) + " M WHERE "
                + string.Join(" AND ", scopeParts) + ";", connection, transaction))
            {
                foreach (var parameter in parameters)
                    read.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
                startValue = (await read.ExecuteScalarAsync(token))?.ToString()?.Trim();
            }
            if (string.IsNullOrEmpty(startValue))
                continue;

            // 逐层展开（#临时表放在嵌套作用域里，结束即释放，避免污染调用方会话）
            var cycleSql = """
                DECLARE @found NVARCHAR(100) = NULL, @step INT = 1;
                IF OBJECT_ID('tempdb..#NoCycleLevel') IS NOT NULL DROP TABLE #NoCycleLevel;
                SELECT @step AS StepNo, __PARENT__ AS ParentValue, __CHILD__ AS ChildValue
                  INTO #NoCycleLevel FROM dbo.__TABLE__ WHERE __PARENT__ = @start;
                WHILE 1 = 1
                BEGIN
                    SELECT TOP 1 @found = CONVERT(NVARCHAR(100), ParentValue) FROM #NoCycleLevel
                     WHERE ChildValue = @start AND StepNo = @step;
                    IF ISNULL(@found, N'') <> N'' BREAK;
                    SET @step = @step + 1;
                    INSERT INTO #NoCycleLevel
                    SELECT @step, b.__PARENT__, b.__CHILD__ FROM dbo.__TABLE__ b, #NoCycleLevel t
                     WHERE b.__PARENT__ = t.ChildValue AND t.StepNo = @step - 1;
                    IF @@ROWCOUNT <= 0 BREAK;
                    IF @step >= @maxDepth
                    BEGIN
                        SELECT TOP 1 @found = CONVERT(NVARCHAR(100), ParentValue) FROM #NoCycleLevel WHERE StepNo = @step;
                        BREAK;
                    END
                END
                SELECT @found;
                """
                .Replace("__TABLE__", EffectConditionCompiler.Identifier(table))
                .Replace("__PARENT__", EffectConditionCompiler.Identifier(parentField))
                .Replace("__CHILD__", EffectConditionCompiler.Identifier(childField));
            string? found;
            await using (var command = new SqlCommand(cycleSql, connection, transaction))
            {
                command.Parameters.AddWithValue("@start", startValue);
                command.Parameters.AddWithValue("@maxDepth", maxDepth);
                found = (await command.ExecuteScalarAsync(token))?.ToString()?.Trim();
            }
            if (string.IsNullOrEmpty(found)) continue;

            var message = check.TryGetProperty("message", out var messageElement)
                && messageElement.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(messageElement.GetString())
                    ? messageElement.GetString()!
                    : rule.Message ?? "{ROWS}";
            var rowSeparator = check.TryGetProperty("diagnosticRowSeparator", out var separatorElement)
                && separatorElement.ValueKind == JsonValueKind.String ? separatorElement.GetString()! : "\r\n";
            return message.Contains("{ROWS}", StringComparison.Ordinal)
                ? message.Replace("{ROWS}", string.Join(rowSeparator, found))
                : message;
        }
        return null;
    }

    /// <summary>读取校验 check 的必填字符串字段（fail-closed）。</summary>
    private static string RequiredText(JsonElement element, string name, string label)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(value.GetString())
                ? value.GetString()!.Trim()
                : throw new EffectConfigException($"{label} 缺少字符串字段 {name}。");

    /// <summary>
    /// Restricts the source rows to the current document by master primary-key values;
    /// comparing without the document scope would double-count previously received rows.
    /// </summary>
    private static (string Sql, List<EffectSqlParameter> Parameters) BuildDocumentScope(
        ModuleEffectPlan plan,
        IReadOnlyList<string> masterKeyValues)
    {
        var (parts, parameters) = BuildDocumentScopeParts(plan, masterKeyValues, "S");
        return (string.Join(" AND ", parts), parameters);
    }

    /// <summary>
    /// Scopes a statement to the current document by master primary-key values. The alias
    /// names the relation that carries the master key columns (master table, or the detail
    /// table which repeats them).
    /// </summary>
    private static (List<string> Parts, List<EffectSqlParameter> Parameters) BuildDocumentScopeParts(
        ModuleEffectPlan plan,
        IReadOnlyList<string> masterKeyValues,
        string alias)
    {
        if (plan.MasterPkOrder.Count == 0 || masterKeyValues.Count == 0)
        {
            throw new EffectConfigException("校验缺少单据主键上下文，禁止跨单比较。");
        }
        var keys = Math.Min(plan.MasterPkOrder.Count, masterKeyValues.Count);
        var parts = new List<string>();
        var parameters = new List<EffectSqlParameter>();
        for (var index = 0; index < keys; index++)
        {
            var name = "@mk" + index;
            parameters.Add(new EffectSqlParameter(name, masterKeyValues[index]));
            parts.Add($"{alias}.{EffectConditionCompiler.Identifier(plan.MasterPkOrder[index])} = {name}");
        }
        return (parts, parameters);
    }

    /// <summary>
    /// reference-exists 逐项断言"当前单据引用的资料存在"：来源行限定在当前单据内
    /// （主表按主键参数、明细按同一单据关联），否则他单的历史坏引用会拦下本次保存。
    /// 配置 lineField 时，命中项把缺失行的行号回填进消息的 {ROWS} 占位符。
    /// </summary>
    private async Task<string?> CheckReferenceExistsAsync(
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
                lines.Add(reader.IsDBNull(0) ? string.Empty : FormatCell(reader, 0));
            return message.Replace("{ROWS}", string.Join("\r\n", lines));
        }
        return null;
    }

    /// <summary>Compiled single reference-exists assertion plus its user-facing message.</summary>
    internal sealed record ReferenceExistsSql(
        string Sql,
        IReadOnlyList<EffectSqlParameter> Parameters,
        string? Message,
        string? LineSql = null);

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
        // （对应旧实现把多张表 UNION ALL 后做一次存在性判断）。
        var missingParts = new List<string>();
        var usesDetail = false;
        var conditionParameters = new List<EffectSqlParameter>();
        foreach (var target in targets)
        {
            var refTable = RequiredString(target, "refTable", "reference-exists.check 缺少 refTable。");
            var (match, matchUsesDetail) = BuildReferenceMatch(target);
            var (mismatch, mismatchUsesDetail) = BuildReferenceMismatch(target);
            var (refCondition, refConditionUsesDetail, refConditionParameters) = BuildReferenceCondition(target);
            usesDetail |= matchUsesDetail || mismatchUsesDetail || refConditionUsesDetail;
            conditionParameters.AddRange(refConditionParameters);
            // 无 mismatch ＝ 断言"引用必须存在"；带 mismatch ＝ 断言"引用存在时该列必须与来源一致"
            // （旧实现的反向一致性断言，命中条件是存在一行且两列不等）；
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

        var (documentScope, scopeParameters) = BuildDocumentScopeParts(plan, masterKeyValues, "M");
        var scopeParts = new List<string>(documentScope);
        var fromDetail = string.Empty;
        if (usesDetail)
        {
            if (plan.DetailTable is null)
                throw new EffectConfigException("reference-exists 明细级断言需要模块明细表（模块形态不足）。");
            // 明细表按约定携带主表主键列，据此把来源行限定在当前单据内；参数与主表作用域同名同值，只登记一次。
            scopeParts.AddRange(BuildDocumentScopeParts(plan, masterKeyValues, "D").Parts);
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
                : "SELECT TOP (" + MaxRowsOf(check).ToString(CultureInfo.InvariantCulture) + ") " + lineExpression
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
                var targetColumn = RequiredString(pair, "target", "reference-exists.join 缺少 target。");
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
        var field = RequiredString(refKey, "field", "reference-exists.refKey 缺少 field。");
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
        var column = RequiredString(mismatch, "target", "reference-exists.mismatch 缺少 target。");
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
        var field = RequiredString(active, "field", "reference-exists.activeTag 缺少 field。");
        var expect = active.TryGetProperty("expect", out var declared) && declared.ValueKind == JsonValueKind.Number
            ? declared.GetInt32()
            : 0;
        return " AND R." + EffectConditionCompiler.Identifier(field)
            + " = " + expect.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// allowEmpty：来源侧键为空即放行本项（旧实现 "ISNULL(类型,'')&lt;&gt;''" 的语义）。
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
        var field = RequiredString(source, "field", "reference-exists 来源列缺少 field。");
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

    /// <summary>
    /// master-detail 形态：跨单据的"主表维度 × 明细分组键"唯一。以当前单据行 cur 为锚，
    /// 在与其主表维度相同的其它单据（m）中按明细分组键分组，命中组数大于 1 即重复。
    /// 可选 documentDetailFields 追加"本单员工"限定（只统计本单出现过的分组键），
    /// 未提供时与旧实现的整月扫描一致。诊断列按组聚合（MAX）成多行，供消息 {ROWS} 回填。
    /// </summary>
    internal static DuplicateCheckSql BuildMasterDetailUniqueSql(
        ModuleEffectPlan plan,
        JsonElement root,
        IReadOnlyList<string> masterKeyValues)
    {
        var masterTableName = RequiredString(root, "masterTable", "duplicate-check master-detail 缺少 masterTable。");
        var detailTableName = RequiredString(root, "detailTable", "duplicate-check master-detail 缺少 detailTable。");
        var groupFields = ParseStringArray(root, "groupFields");
        if (groupFields.Length == 0)
            throw new EffectConfigException("duplicate-check master-detail 缺少 groupFields。");
        var masterGroupFields = ParseStringArray(root, "masterGroupFields");
        if (masterGroupFields.Length == 0)
            throw new EffectConfigException("duplicate-check master-detail 缺少 masterGroupFields。");

        if (!root.TryGetProperty("joinFields", out var joinFields) || joinFields.ValueKind != JsonValueKind.Object)
            throw new EffectConfigException("duplicate-check master-detail 缺少 joinFields。");
        var joinMaster = ParseStringArray(joinFields, "master");
        var joinDetail = ParseStringArray(joinFields, "detail");
        if (joinMaster.Length == 0 || joinMaster.Length != joinDetail.Length)
            throw new EffectConfigException("duplicate-check master-detail joinFields.master/detail 必须等长非空。");

        var (documentScope, parameters) = BuildDocumentScopeParts(plan, masterKeyValues, "cur");
        var on = string.Join(" AND ", joinMaster.Select((field, index) =>
            $"d.{EffectConditionCompiler.Identifier(joinDetail[index])} = m.{EffectConditionCompiler.Identifier(field)}"));

        var predicates = new List<string>();
        foreach (var field in masterGroupFields)
        {
            predicates.Add($"m.{EffectConditionCompiler.Identifier(field)} = cur.{EffectConditionCompiler.Identifier(field)}");
        }

        var documentDetailFields = ParseStringArray(root, "documentDetailFields");
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

        var diagnostics = ParseStringArray(root, "diagnosticFields");
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
    /// period-overlap：同一维度（人／险种／证件）的期间不得与**其它单据**的期间相交。
    /// 判据为闭区间相交（端点相接即算重叠）；结束日为空视为无限期（至今有效）。
    /// 当前单据由 scopeFields 与单据主键参数限定；诊断列取自冲突行所在单据。
    /// </summary>
    private async Task<string?> CheckPeriodOverlapAsync(
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
                cells.Add(FormatCell(reader, index));
            }
            lines.Add(string.Join("\t", cells) + "\t");
        }
        if (lines.Count == 0)
            return null;
        return (rule.Message ?? "期间与其它单据重叠。").Replace("{ROWS}", string.Join("\r\n", lines));
    }

    /// <summary>Compiled period-overlap statement plus the conflicting-row columns echoed into the message.</summary>
    internal sealed record PeriodOverlapSql(
        string Sql,
        IReadOnlyList<EffectSqlParameter> Parameters,
        IReadOnlyList<string> Diagnostics);

    internal static PeriodOverlapSql BuildPeriodOverlapSql(
        JsonElement root,
        IReadOnlyList<string> masterKeyValues)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw new EffectConfigException("period-overlap 参数必须是对象。");
        var detailTable = RequiredString(root, "detailTable", "period-overlap 缺少 detailTable。");
        if (!root.TryGetProperty("rangeFields", out var range) || range.ValueKind != JsonValueKind.Object)
            throw new EffectConfigException("period-overlap 缺少 rangeFields。");
        var beginField = RequiredString(range, "begin", "period-overlap rangeFields 缺少 begin。");
        var endField = RequiredString(range, "end", "period-overlap rangeFields 缺少 end。");

        var scopeFields = ParseStringArray(root, "scopeFields");
        if (scopeFields.Length == 0)
            throw new EffectConfigException("period-overlap 缺少 scopeFields。");
        if (masterKeyValues.Count == 0)
            throw new EffectConfigException("period-overlap 缺少单据主键上下文，禁止跨单比较。");
        if (scopeFields.Length != masterKeyValues.Count)
        {
            throw new EffectConfigException(
                $"period-overlap scopeFields 数量（{scopeFields.Length}）与单据主键数量（{masterKeyValues.Count}）不一致。");
        }
        var groupFields = ParseStringArray(root, "groupFields");
        if (groupFields.Length == 0)
            throw new EffectConfigException("period-overlap 缺少 groupFields。");

        var diagnostics = ParseStringArray(root, "diagnosticFields");
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
            var lookupTable = RequiredString(lookup, "table", "period-overlap displayLookup 缺少 table。");
            var linkField = RequiredString(lookup, "linkField", "period-overlap displayLookup 缺少 linkField。");
            var displayField = RequiredString(lookup, "displayField", "period-overlap displayLookup 缺少 displayField。");
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

    /// <summary>诊断显示名连接（可选）：以冲突行/明细行的关联列去查一张档案表取显示名。</summary>
    internal readonly record struct DisplayLookup(string JoinSql, string DisplayCell);

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
        var table = RequiredString(lookup, "table", "displayLookup 缺少 table。");
        var linkField = RequiredString(lookup, "linkField", "displayLookup 缺少 linkField。");
        var displayField = RequiredString(lookup, "displayField", "displayLookup 缺少 displayField。");
        var join = " LEFT JOIN dbo." + EffectConditionCompiler.Identifier(table)
            + " dp ON dp." + EffectConditionCompiler.Identifier(linkField)
            + " = D." + EffectConditionCompiler.Identifier(linkField);
        return new DisplayLookup(join, "dp." + EffectConditionCompiler.Identifier(displayField));
    }

    private static int MaxRowsOf(JsonElement root) =>
        root.TryGetProperty("maxRows", out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out var declared)
            ? Math.Clamp(declared, 1, 100)
            : 10;

    private static string RequiredString(JsonElement element, string name, string error)
    {
        return element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(value.GetString())
                ? value.GetString()!.Trim()
                : throw new EffectConfigException(error);
    }

    /// <summary>
    /// duplicate-check 以当前单据的主键上下文为锚：entity 模式把已保存行（主表 M，必要时
    /// 加明细 D）的键值同目标表的其它行逐一比对，within-doc 模式只看当前单据的明细行。
    /// 两种模式都必须限定在当前单据内——脱离单据范围会把其它单据的历史重复算到本次保存头上。
    /// </summary>
    private async Task<string?> CheckDuplicateAsync(
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
            // 每行把诊断列以制表符相连并保留行尾制表符，与旧实现逐行拼接一致。
            var lines = new List<string>();
            do
            {
                var cells = new List<string>(compiled.Diagnostics.Count);
                for (var index = 0; index < compiled.Diagnostics.Count; index++)
                    cells.Add(FormatCell(reader, index));
                lines.Add(string.Join("\t", cells) + "\t");
            }
            while (await reader.ReadAsync(token));
            return (rule.Message ?? "数据重复。").Replace("{ROWS}", string.Join("\r\n", lines));
        }

        var values = new List<string?>(compiled.Diagnostics.Count);
        for (var index = 0; index < compiled.Diagnostics.Count; index++)
            values.Add(reader.IsDBNull(index) ? null : FormatCell(reader, index));
        return RenderDiagnosticMessage(rule.Message, compiled.Diagnostics, values);
    }

    /// <summary>
    /// 诊断列的显示文本：日期按 yyyy-MM-dd 输出（避免把 ASP.NET 文化格式带进用户文案），
    /// 其余类型按不变文化转写并去掉首尾空白。
    /// </summary>
    private static string FormatCell(SqlDataReader reader, int index)
    {
        var value = reader.GetValue(index);
        return value switch
        {
            DateTime date => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            string text => text.Trim(),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim() ?? string.Empty,
        };
    }

    /// <summary>Compiled duplicate-check statement plus the candidate columns echoed into the message.</summary>
    internal sealed record DuplicateCheckSql(
        string Sql,
        IReadOnlyList<EffectSqlParameter> Parameters,
        IReadOnlyList<string> Diagnostics,
        bool MultiRow = false);

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

        var keyFields = ParseStringArray(root, "keyFields");
        if (keyFields.Length == 0)
            throw new EffectConfigException("duplicate-check 缺少 keyFields。");
        var diagnostics = ParseStringArray(root, "diagnostics");

        if (mode.Equals("within-doc", StringComparison.OrdinalIgnoreCase))
        {
            if (plan.DetailTable is null)
                throw new EffectConfigException("duplicate-check within-doc 需要明细表（模块形态不足）。");
            if (diagnostics.Length > 0)
                throw new EffectConfigException("duplicate-check within-doc 不支持 diagnostics（请用 diagnosticFields）。");
            // 明细表按约定携带主表主键列，据此把分组限定在当前单据内。
            var (detailScope, detailParameters) = BuildDocumentScopeParts(plan, masterKeyValues, "D");
            var diagnosticFields = ParseStringArray(root, "diagnosticFields");
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
            var withinDocSql = "SELECT TOP " + MaxRowsOf(root) + " "
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
            sourceFields = ParseStringArray(keySource, "fields");
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

        var (documentScope, parameters) = BuildDocumentScopeParts(plan, masterKeyValues, "M");
        // 自排除：候选行主键等于当前单据主键，即刚保存的这行本身。
        var selfExclusion = new List<string>();
        if (root.TryGetProperty("excludeSelf", out var excludeSelf) && excludeSelf.ValueKind == JsonValueKind.Object)
        {
            var selfKeys = ParseStringArray(excludeSelf, "keyFields");
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
            var viaTable = RequiredString(excludeVia, "table", "duplicate-check excludeVia 缺少 table。");
            var viaJoin = new List<string>();
            if (excludeVia.TryGetProperty("join", out var viaJoinArray) && viaJoinArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var pair in viaJoinArray.EnumerateArray())
                {
                    var targetColumn = RequiredString(pair, "target", "duplicate-check excludeVia.join 缺少 target。");
                    var source = pair.TryGetProperty("source", out var declaredSource) ? declaredSource : default;
                    var viaScopeName = source.ValueKind == JsonValueKind.Object
                        && source.TryGetProperty("scope", out var scopeValue) && scopeValue.ValueKind == JsonValueKind.String
                            ? scopeValue.GetString()!.Trim().ToUpperInvariant()
                            : "TARGET";
                    var sourceField = RequiredString(source, "field", "duplicate-check excludeVia.join 缺少 source.field。");
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
            var (viaScope, _) = BuildDocumentScopeParts(plan, masterKeyValues, "V");
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

    /// <summary>
    /// line-require: 命中所配置触发条件的行必须满足字段断言——默认断言是"字段已填"
    /// （如批管品明细必填批号），给出 assert 时改为数值比较（如盘点数不得小于零、
    /// 本位币汇率必须为一）。scope=MASTER 时断言行是当前主表行，供无明细表的模块使用。
    /// </summary>
    private async Task<string?> CheckLineRequireAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        ModuleEffectPlan plan,
        EffectValidationPlan rule,
        IReadOnlyList<string> masterKeyValues,
        CancellationToken token)
    {
        if (!rule.Params.TryGetProperty("checks", out var checks) || checks.ValueKind != JsonValueKind.Array)
            throw new EffectConfigException("line-require 缺少 checks 数组。");
        foreach (var check in checks.EnumerateArray())
        {
            var field = check.TryGetProperty("field", out var fieldElement) && fieldElement.ValueKind == JsonValueKind.String
                ? fieldElement.GetString()!.Trim()
                : throw new EffectConfigException("line-require.check 缺少 field。");
            // 断言行默认是当前明细行；scope=MASTER 时改为主表行，供"无明细表"的模块（如货币资料）
            // 表达行字段约束。
            var masterScope = (check.TryGetProperty("scope", out var scopeElement)
                    && scopeElement.ValueKind == JsonValueKind.String
                        ? scopeElement.GetString()!.Trim().ToUpperInvariant()
                        : "DETAIL") switch
            {
                "DETAIL" => false,
                "MASTER" => true,
                var other => throw new EffectConfigException(
                    $"line-require.check.scope '{other}' 不在闭集内（DETAIL/MASTER）。"),
            };
            var rowTable = masterScope ? plan.MasterTable : plan.DetailTable;
            if (rowTable is null)
                throw new EffectConfigException(masterScope
                    ? "line-require 需要主表（模块形态不足）。"
                    : "line-require 需要明细表（模块形态不足）。");
            var rowAlias = masterScope ? "M" : "S";
            var documentScopeParts = BuildDocumentScopeParts(plan, masterKeyValues, rowAlias);
            var parameters = documentScopeParts.Parameters;
            var documentScope = string.Join(" AND ", documentScopeParts.Parts);
            // 触发条件二选一或并用：triggers（明细列与常量的数值比较）与 condition
            // （结构化条件，走闭式条件编译器；DETAIL 域即本行别名 S，可表达"产品为批管"这类
            // 跨表存在性判据）。两者取 OR，任一成立即要求 field 已填。
            var triggerSql = new List<string>();
            if (check.TryGetProperty("triggers", out var triggers) && triggers.ValueKind == JsonValueKind.Array)
            {
                foreach (var trigger in triggers.EnumerateArray())
                {
                    var triggerColumn = trigger.TryGetProperty("field", out var triggerFieldElement) && triggerFieldElement.ValueKind == JsonValueKind.String
                        ? triggerFieldElement.GetString()!.Trim()
                        : throw new EffectConfigException("line-require.trigger 缺少 field。");
                    // 触发器作用域必须与断言行一致：两者作用在不同的关系上会把判据写成无意义的笛卡尔比较。
                    var triggerScope = trigger.TryGetProperty("scope", out var triggerScopeElement)
                        && triggerScopeElement.ValueKind == JsonValueKind.String
                            ? triggerScopeElement.GetString()!.Trim().ToUpperInvariant()
                            : "DETAIL";
                    if (triggerScope != (masterScope ? "MASTER" : "DETAIL"))
                        throw new EffectConfigException(
                            "line-require.trigger.scope 必须与同一 check 的 scope 一致。");
                    var op = trigger.TryGetProperty("op", out var opElement) && opElement.ValueKind == JsonValueKind.String
                        ? opElement.GetString()!.Trim().ToUpperInvariant()
                        : throw new EffectConfigException("line-require.trigger 缺少 op。");
                    var sqlOp = op switch
                    {
                        "GT" => ">",
                        "GE" => ">=",
                        "LT" => "<",
                        "LE" => "<=",
                        "EQ" => "=",
                        "NEQ" => "<>",
                        _ => throw new EffectConfigException($"line-require.trigger 比较符 '{op}' 不在封闭集内。"),
                    };
                    var value = trigger.TryGetProperty("value", out var valueElement) && valueElement.ValueKind == JsonValueKind.Number
                        ? valueElement.GetDouble()
                        : 0;
                    triggerSql.Add($"COALESCE({rowAlias}.{EffectConditionCompiler.Identifier(triggerColumn)}, 0) {sqlOp} {value.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
                }
            }
            if (check.TryGetProperty("condition", out var conditionElement) && conditionElement.ValueKind == JsonValueKind.Object)
            {
                var compiled = new EffectConditionCompiler().Compile(
                    conditionElement,
                    (scope, _) => scope.ToUpperInvariant() switch
                    {
                        // 条件里的作用域名必须与断言行同域：明细行只认 DETAIL/TARGET，
                        // 主表行只认 MASTER/TARGET，错域即配置失败（不静默取错别名）。
                        "DETAIL" => masterScope ? null : rowAlias,
                        "MASTER" => masterScope ? rowAlias : null,
                        "TARGET" => rowAlias,
                        _ => null,
                    },
                    _ => true,
                    outerAlias: rowAlias);
                triggerSql.Add("(" + compiled.Sql + ")");
                parameters.AddRange(compiled.Parameters);
            }
            // 断言形态二选一：默认"字段已填"；给出 assert 时改为数值比较（未命中即违规），
            // 此时触发器/条件可省略（无条件生效）。旧实现里的 `ISNULL(x,0)` 语义由 COALESCE 复刻。
            string? assertSql = null;
            if (check.TryGetProperty("assert", out var assertElement) && assertElement.ValueKind == JsonValueKind.Object)
            {
                var assertOp = assertElement.TryGetProperty("op", out var assertOpElement)
                    && assertOpElement.ValueKind == JsonValueKind.String
                        ? assertOpElement.GetString()!.Trim().ToUpperInvariant()
                        : throw new EffectConfigException("line-require.assert 缺少 op。");
                var assertSqlOp = assertOp switch
                {
                    "GT" => ">",
                    "GE" => ">=",
                    "LT" => "<",
                    "LE" => "<=",
                    "EQ" => "=",
                    "NEQ" => "<>",
                    _ => throw new EffectConfigException($"line-require.assert.op '{assertOp}' 不在封闭集内。"),
                };
                var assertCompareField = assertElement.TryGetProperty("compareField", out var compareFieldElement)
                    && compareFieldElement.ValueKind == JsonValueKind.String
                        ? compareFieldElement.GetString()!.Trim()
                        : null;
                if (!string.IsNullOrWhiteSpace(assertCompareField))
                {
                    // 与同行另一列比较：任一为空都不算违规（复刻旧实现"可空日期比较为 false"），
                    // 因此断言形态写成"空 或 关系成立"，违规谓词取其反。
                    var compareIdentifier = EffectConditionCompiler.Identifier(assertCompareField);
                    assertSql = "(" + rowAlias + "." + EffectConditionCompiler.Identifier(field) + " IS NULL"
                        + " OR " + rowAlias + "." + compareIdentifier + " IS NULL"
                        + " OR " + rowAlias + "." + EffectConditionCompiler.Identifier(field)
                        + " " + assertSqlOp + " " + rowAlias + "." + compareIdentifier + ")";
                }
                else
                {
                    if (!assertElement.TryGetProperty("value", out var assertValue) || assertValue.ValueKind != JsonValueKind.Number)
                        throw new EffectConfigException("line-require.assert 缺少数值 value 或字符串 compareField。");
                    // 默认把空值当 0（复刻旧实现的 ISNULL(x,0)）；`nullSkips=true` 改为按原值比较，
                    // 于是空值行为 UNKNOWN ⇒ 不算违规——用于旧实现写成 `col <= 0` 这类"空值放行"的判据
                    // （把空值当 0 会把它误判成违规）。
                    var assertNullSkips = assertElement.TryGetProperty("nullSkips", out var nullSkipsElement)
                        && nullSkipsElement.ValueKind == JsonValueKind.True;
                    var assertValueSql = assertNullSkips
                        ? rowAlias + "." + EffectConditionCompiler.Identifier(field)
                        : "COALESCE(" + rowAlias + "." + EffectConditionCompiler.Identifier(field) + ", 0)";
                    assertSql = assertValueSql + " " + assertSqlOp + " "
                        + assertValue.GetDouble().ToString(System.Globalization.CultureInfo.InvariantCulture);
                }
            }
            if (triggerSql.Count == 0 && assertSql is null)
                throw new EffectConfigException("line-require.check 缺少 triggers 或 condition。");
            // 违规谓词：断言形态取反（未命中即违规），默认形态为字段空值。
            var violationSql = assertSql is null
                ? $"({rowAlias}.{EffectConditionCompiler.Identifier(field)} IS NULL OR {rowAlias}.{EffectConditionCompiler.Identifier(field)} = '')"
                : "NOT (" + assertSql + ")";
            var triggerPredicate = triggerSql.Count == 0 ? string.Empty : "(" + string.Join(" OR ", triggerSql) + ") AND ";
            var sql = "SELECT TOP 1 1 FROM dbo." + EffectConditionCompiler.Identifier(rowTable)
                + " " + rowAlias + " WHERE " + documentScope
                + " AND " + triggerPredicate + violationSql;
            await using var command = new SqlCommand(sql, connection, transaction);
            foreach (var parameter in parameters)
            {
                command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
            }
            var hit = await command.ExecuteScalarAsync(token);
            if (hit is not null)
            {
                var message = rule.Message
                    ?? (check.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.String
                        ? msg.GetString()
                        : assertSql is null ? "明细行必填字段缺失。" : "明细行字段取值不符合要求。");
                // 逐行诊断：配置 diagnosticFields（明细行列名）时，把命中行按"每行一列值、行间 \r\n"
                // 追加到消息后（与既有实现的消息形态一致，最多 10 行）。
                var diagnostics = ParseStringArray(check, "diagnosticFields");
                if (diagnostics.Length > 0)
                {
                    var selectList = string.Join(", ", diagnostics.Select(name => rowAlias + "." + EffectConditionCompiler.Identifier(name)));
                    var diagnosticSql = "SELECT TOP 10 " + selectList
                        + " FROM dbo." + EffectConditionCompiler.Identifier(rowTable)
                        + " " + rowAlias + " WHERE " + documentScope
                        + " AND " + triggerPredicate + violationSql
                        + " ORDER BY " + rowAlias + "." + EffectConditionCompiler.Identifier(diagnostics[0]);
                    await using var diagnosticCommand = new SqlCommand(diagnosticSql, connection, transaction);
                    foreach (var parameter in parameters)
                    {
                        diagnosticCommand.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
                    }
                    var rows = new List<string>();
                    await using var reader = await diagnosticCommand.ExecuteReaderAsync(token);
                    while (await reader.ReadAsync(token))
                    {
                        var cells = new List<string>();
                        for (var index = 0; index < diagnostics.Length; index++)
                        {
                            cells.Add(reader.IsDBNull(index) ? string.Empty : Convert.ToString(reader.GetValue(index))!.Trim());
                        }
                        rows.Add(string.Join("    ", cells));
                    }
                    if (rows.Count > 0)
                        message = message + "\r\n" + string.Join("\r\n", rows);
                }
                return message;
            }
        }
        return null;
    }

    /// <summary>
    /// 数量判据的一个"量纲"：本单量（thisQty）、已发生量与限额三组字段，外加可选容差。
    /// 一个 check 可以带多个量纲（旧实现把"数量"与"备品"合成 `WHERE a OR b`），
    /// 也可只带一个（直接写在 check 上，与既有配置完全兼容）。
    /// </summary>
    private sealed record QuantityDimension(
        IReadOnlyList<EffectTerm> Terms,
        IReadOnlyList<string> UsageFields,
        IReadOnlyList<string> LimitFields,
        decimal Offset,
        bool Aggregate);

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

    private IReadOnlyList<EffectMatchItem> ParseMatchPairs(JsonElement check)
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
        return ParseStringArray(usage, "fields");
    }

    private static string[] ParseStringArray(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();
        return array.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString()!.Trim())
            .ToArray();
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
