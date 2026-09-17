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
    /// Optional per-check SYSSS gate (closed operator, same semantics as the condition
    /// compiler switch): the check only applies when the switch column equals expect
    /// (default 1). Unknown columns fail closed via Identifier validation / SQL error.
    /// </summary>
    private static async Task<bool> ShouldSkipOnSwitchAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        JsonElement check,
        CancellationToken token)
    {
        if (!check.TryGetProperty("switch", out var gate) || gate.ValueKind != JsonValueKind.Object)
            return false;
        var key = gate.TryGetProperty("key", out var keyElement) && keyElement.ValueKind == JsonValueKind.String
            ? keyElement.GetString()!.Trim()
            : throw new EffectConfigException("qty-not-exceed.check.switch 缺少 key。");
        var expect = gate.TryGetProperty("expect", out var expectElement) && expectElement.ValueKind == JsonValueKind.Number
            ? expectElement.GetInt32()
            : 1;
        var sql = $"SELECT COALESCE(MAX(CAST({EffectConditionCompiler.Identifier(key)} AS int)), 0) FROM dbo.SYSSS WITH (NOLOCK)";
        await using var command = new SqlCommand(sql, connection, transaction);
        var actual = await command.ExecuteScalarAsync(token);
        return Convert.ToInt32(actual) != expect;
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
            if (await ShouldSkipOnSwitchAsync(connection, transaction, check, token))
                continue;
            var match = ParseMatchPairs(check);
            var thisQty = ParseScopeTerms(check, "thisQty");
            var usage = ParseFieldList(check, "usage");
            var limit = ParseFieldList(check, "limit");
            if (match.Count == 0)
                throw new EffectConfigException("qty-not-exceed.check 缺少 match 定位键。");
            if (thisQty.Count == 0 || limit.Count == 0)
                throw new EffectConfigException("qty-not-exceed.check 缺少 thisQty/limit。");

            var targetTable = check.TryGetProperty("targetTable", out var tt) && tt.ValueKind == JsonValueKind.String
                ? tt.GetString()!.Trim()
                : throw new EffectConfigException("qty-not-exceed.check 缺少 targetTable（fail-closed，禁止隐式推导）。");
            var sourceTable = match[0].Source.Scope.Equals("DETAIL", StringComparison.OrdinalIgnoreCase)
                ? plan.DetailTable
                : plan.MasterTable;
            if (sourceTable is null)
                throw new EffectConfigException("qty-not-exceed 来源表不可用（模块形态不足）。");

            var usageSql = string.Join(" + ", usage.Select(field => $"COALESCE(T.{EffectConditionCompiler.Identifier(field)}, 0)"));
            var limitSql = string.Join(" + ", limit.Select(field => $"COALESCE(T.{EffectConditionCompiler.Identifier(field)}, 0)"));
            // 容差：旧实现常带 0.1 之类的余量（如收料不超采购 +0.1），注册表已允许 offset，此处落实。
            var offset = check.TryGetProperty("offset", out var offsetElement) && offsetElement.ValueKind == JsonValueKind.Number
                && offsetElement.TryGetDecimal(out var declaredOffset)
                    ? declaredOffset
                    : 0m;
            var limitWithOffset = offset == 0m
                ? limitSql
                : $"({limitSql} + {offset.ToString(CultureInfo.InvariantCulture)})";
            var termSql = BuildTermSql(thisQty, "S");
            var (documentScope, parameters) = BuildDocumentScope(plan, masterKeyValues);

            // 聚合形态：旧实现多按单据分组求和后再比较（如"同一采购行的收料合计"）。
            // 逐行比较在"同一单据里同一引用键有多行"时会弱于旧判据（单行没超、合计已超），
            // 等于悄悄放宽约束，故 thisQty.agg=SUM 时先按 match 键分组求和再比。
            var aggregate = check.TryGetProperty("thisQty", out var thisQtyElement)
                && thisQtyElement.ValueKind == JsonValueKind.Object
                && thisQtyElement.TryGetProperty("agg", out var aggElement)
                && aggElement.ValueKind == JsonValueKind.String
                && aggElement.GetString()!.Equals("SUM", StringComparison.OrdinalIgnoreCase);
            string fromSql;
            string correlation;
            string thisSql;
            if (aggregate)
            {
                var groupBy = string.Join(", ", match.Select(pair =>
                    "S." + EffectConditionCompiler.Identifier(pair.Source.Field!)));
                correlation = string.Join(" AND ", match.Select(pair =>
                    "T." + EffectConditionCompiler.Identifier(pair.TargetColumn) + " = S1." + EffectConditionCompiler.Identifier(pair.Source.Field!)));
                thisSql = "S1.[__THIS_QTY]";
                fromSql = "(SELECT " + groupBy + ", SUM(" + termSql + ") AS [__THIS_QTY] FROM dbo."
                    + EffectConditionCompiler.Identifier(sourceTable) + " S WHERE " + documentScope
                    + " GROUP BY " + groupBy + ") S1 CROSS JOIN dbo."
                    + EffectConditionCompiler.Identifier(targetTable) + " T";
            }
            else
            {
                correlation = BuildMatchCorrelation(match, "S", "T");
                thisSql = termSql;
                fromSql = "dbo." + EffectConditionCompiler.Identifier(sourceTable) + " S CROSS JOIN dbo."
                    + EffectConditionCompiler.Identifier(targetTable) + " T";
            }

            var comparison = mode.Equals("not-below-progress", StringComparison.OrdinalIgnoreCase)
                ? $"{limitSql} + {thisSql} < {usageSql}" // reduction would fall below accumulated progress
                : mode.Equals("this-not-exceed", StringComparison.OrdinalIgnoreCase)
                    ? $"{thisSql} > {limitWithOffset}" // document quantity must not exceed the referenced cap
                    : $"{usageSql} + {thisSql} > {limitWithOffset}";

            // 来源行的单据范围：分组形态已把范围写进子查询，逐行形态必须作为顶层谓词补上，
            // 否则比较的是全库历史行，他单的超量会拦下本次操作（同 reference-exists 的旧缺陷）。
            var predicate = aggregate
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
                            : $"存在超出{(mode == "not-below-progress" ? "进度" : "限额")}的明细行（{stage}）。");
                var cells = BuildQtyDiagnosticCells(check, fromSql, correlation, comparison, aggregate);
                if (cells is null)
                    return message;
                // 诊断行的拼接方式各族不同：多为"列间 4 空格、行间 CRLF"，也有"单列多值、一行内联"（值间 2 空格）。
                var cellSeparator = check.TryGetProperty("diagnosticCellSeparator", out var cellElement)
                    && cellElement.ValueKind == JsonValueKind.String ? cellElement.GetString()! : "    ";
                var rowSeparator = check.TryGetProperty("diagnosticRowSeparator", out var rowElement)
                    && rowElement.ValueKind == JsonValueKind.String ? rowElement.GetString()! : "\r\n";
                var lines = new List<string>();
                await using var lineCommand = new SqlCommand(
                    "SELECT TOP (" + MaxRowsOf(check).ToString(CultureInfo.InvariantCulture) + ") "
                    + string.Join(", ", cells) + " FROM " + fromSql + " WHERE " + predicate,
                    connection, transaction);
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
    /// 命中行的诊断列（可选）：SOURCE 指来源别名（分组形态下是分组键所在的子查询）、TARGET 指被引用行、
    /// THIS 指本单数量（分组时为求和值）。未配置返回 null，此时消息只回表头。
    /// </summary>
    private static List<string>? BuildQtyDiagnosticCells(
        JsonElement check,
        string fromSql,
        string correlation,
        string comparison,
        bool aggregate)
    {
        if (!check.TryGetProperty("diagnosticFields", out var fields) || fields.ValueKind != JsonValueKind.Array
            || fields.GetArrayLength() == 0)
            return null;
        var sourceAlias = aggregate ? "S1" : "S";
        var cells = new List<string>();
        foreach (var field in fields.EnumerateArray())
        {
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
            }
            cells.Add(scope switch
            {
                "THIS" => aggregate
                    ? sourceAlias + ".[__THIS_QTY]"
                    : "S." + EffectConditionCompiler.Identifier(name ?? throw new EffectConfigException("qty-not-exceed.diagnosticFields 的 THIS 作用域在非分组形态下必须给出 field。")),
                "TARGET" => "T." + EffectConditionCompiler.Identifier(name!),
                _ => sourceAlias + "." + EffectConditionCompiler.Identifier(name!),
            });
        }
        return cells;
    }

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
        foreach (var target in targets)
        {
            var refTable = RequiredString(target, "refTable", "reference-exists.check 缺少 refTable。");
            var (match, matchUsesDetail) = BuildReferenceMatch(target);
            var (mismatch, mismatchUsesDetail) = BuildReferenceMismatch(target);
            usesDetail |= matchUsesDetail || mismatchUsesDetail;
            // 无 mismatch ＝ 断言"引用必须存在"；带 mismatch ＝ 断言"引用存在时该列必须与来源一致"
            // （旧实现的反向一致性断言，命中条件是存在一行且两列不等）。
            var body = match + BuildActiveTag(target);
            missingParts.Add(mismatch is null
                ? "NOT EXISTS (SELECT 1 FROM dbo." + EffectConditionCompiler.Identifier(refTable)
                    + " R WITH (NOLOCK) WHERE " + body + ")"
                : "EXISTS (SELECT 1 FROM dbo." + EffectConditionCompiler.Identifier(refTable)
                    + " R WITH (NOLOCK) WHERE " + body + " AND " + mismatch + ")");
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
        return new ReferenceExistsSql(sql, scopeParameters, message,
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
    /// line-require: document detail lines matching any trigger must carry a
    /// non-empty field (e.g. lines with bad quantity require a bad depot).
    /// </summary>
    private async Task<string?> CheckLineRequireAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        ModuleEffectPlan plan,
        EffectValidationPlan rule,
        IReadOnlyList<string> masterKeyValues,
        CancellationToken token)
    {
        if (plan.DetailTable is null)
            throw new EffectConfigException("line-require 需要明细表（模块形态不足）。");
        if (!rule.Params.TryGetProperty("checks", out var checks) || checks.ValueKind != JsonValueKind.Array)
            throw new EffectConfigException("line-require 缺少 checks 数组。");
        var (documentScope, parameters) = BuildDocumentScope(plan, masterKeyValues);
        foreach (var check in checks.EnumerateArray())
        {
            var field = check.TryGetProperty("field", out var fieldElement) && fieldElement.ValueKind == JsonValueKind.String
                ? fieldElement.GetString()!.Trim()
                : throw new EffectConfigException("line-require.check 缺少 field。");
            if (!check.TryGetProperty("triggers", out var triggers) || triggers.ValueKind != JsonValueKind.Array)
                throw new EffectConfigException("line-require.check 缺少 triggers 数组。");
            var triggerSql = new List<string>();
            foreach (var trigger in triggers.EnumerateArray())
            {
                var triggerColumn = trigger.TryGetProperty("field", out var triggerFieldElement) && triggerFieldElement.ValueKind == JsonValueKind.String
                    ? triggerFieldElement.GetString()!.Trim()
                    : throw new EffectConfigException("line-require.trigger 缺少 field。");
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
                triggerSql.Add($"COALESCE(S.{EffectConditionCompiler.Identifier(triggerColumn)}, 0) {sqlOp} {value.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
            }
            var sql = "SELECT TOP 1 1 FROM dbo." + EffectConditionCompiler.Identifier(plan.DetailTable)
                + " S WHERE " + documentScope
                + " AND (" + string.Join(" OR ", triggerSql) + ")"
                + $" AND (S.{EffectConditionCompiler.Identifier(field)} IS NULL OR S.{EffectConditionCompiler.Identifier(field)} = '')";
            await using var command = new SqlCommand(sql, connection, transaction);
            foreach (var parameter in parameters)
            {
                command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
            }
            var hit = await command.ExecuteScalarAsync(token);
            if (hit is not null)
            {
                return rule.Message
                    ?? (check.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.String
                        ? msg.GetString()
                        : "明细行必填字段缺失。");
            }
        }
        return null;
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
