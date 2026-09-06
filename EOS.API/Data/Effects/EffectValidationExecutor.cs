using System.Data;
using System.Text;
using System.Text.Json;
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
        CancellationToken token)
    {
        foreach (var rule in plan.Rules)
        {
            if (!rule.Enabled || !rule.Stage.Equals(stage, StringComparison.OrdinalIgnoreCase))
                continue;
            var violation = rule.ValidationKey.ToLowerInvariant() switch
            {
                "qty-not-exceed" => await CheckQuantityNotExceedAsync(connection, transaction, plan, rule, stage, token),
                "reference-exists" => await CheckReferenceExistsAsync(connection, transaction, plan, rule, token),
                "duplicate-check" => await CheckDuplicateAsync(connection, transaction, plan, rule, token),
                _ => throw new EffectConfigException($"校验键 '{rule.ValidationKey}' 尚未实现运行时执行。"),
            };
            if (violation is not null)
                throw new EffectValidationException(rule.Message ?? violation);
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
            var thisSql = BuildTermSql(thisQty, "S");
            var correlation = BuildMatchCorrelation(match, "S", "T");
            var comparison = mode.Equals("not-below-progress", StringComparison.OrdinalIgnoreCase)
                ? $"{limitSql} + {thisSql} < {usageSql}" // reduction would fall below accumulated progress
                : $"{usageSql} + {thisSql} > {limitSql}";

            var sql = new StringBuilder("SELECT TOP 1 1 FROM dbo.")
                .Append(EffectConditionCompiler.Identifier(sourceTable)).Append(" S CROSS JOIN dbo.")
                .Append(EffectConditionCompiler.Identifier(targetTable)).Append(" T WHERE ")
                .Append(correlation).Append(" AND ").Append(comparison);
            await using var command = new SqlCommand(sql.ToString(), connection, transaction);
            if (await command.ExecuteScalarAsync(token) is not null)
                return $"存在超出{(mode == "not-below-progress" ? "进度" : "限额")}的明细行（{stage}）。";
        }
        return null;
    }

    private async Task<string?> CheckReferenceExistsAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        ModuleEffectPlan plan,
        EffectValidationPlan rule,
        CancellationToken token)
    {
        if (!rule.Params.TryGetProperty("checks", out var checks) || checks.ValueKind != JsonValueKind.Array)
            throw new EffectConfigException("reference-exists 缺少 checks 数组。");
        foreach (var check in checks.EnumerateArray())
        {
            var refTable = check.TryGetProperty("refTable", out var rt) && rt.ValueKind == JsonValueKind.String
                ? rt.GetString()!.Trim()
                : throw new EffectConfigException("reference-exists.check 缺少 refTable。");
            var allowEmpty = check.TryGetProperty("allowEmpty", out var ae) && ae.ValueKind == JsonValueKind.True;

            string sourceSql;
            string referenceSql;
            if (check.TryGetProperty("join", out var join) && join.ValueKind == JsonValueKind.Array)
            {
                var pairs = new List<string>();
                foreach (var pair in join.EnumerateArray())
                {
                    var targetColumn = pair.TryGetProperty("target", out var t) && t.ValueKind == JsonValueKind.String
                        ? t.GetString()!.Trim()
                        : throw new EffectConfigException("reference-exists.join 缺少 target。");
                    var source = pair.TryGetProperty("source", out var s) ? s : default;
                    var scope = source.TryGetProperty("scope", out var sc) && sc.ValueKind == JsonValueKind.String
                        ? sc.GetString()!.Trim().ToUpperInvariant()
                        : string.Empty;
                    var field = source.TryGetProperty("field", out var f) && f.ValueKind == JsonValueKind.String
                        ? f.GetString()!.Trim()
                        : throw new EffectConfigException("reference-exists.join 缺少 source.field。");
                    pairs.Add($"R.{EffectConditionCompiler.Identifier(targetColumn)} = "
                        + (scope == "MASTER" ? "M." : "D.") + EffectConditionCompiler.Identifier(field));
                }
                referenceSql = string.Join(" AND ", pairs);
            }
            else
            {
                if (!check.TryGetProperty("refKey", out var refKey) || refKey.ValueKind != JsonValueKind.Object)
                    throw new EffectConfigException("reference-exists.check 缺少 refKey。");
                var scope = refKey.GetProperty("scope").GetString()!.Trim().ToUpperInvariant();
                var field = refKey.GetProperty("field").GetString()!.Trim();
                var sourceSide = (scope == "MASTER" ? "M." : "D.") + EffectConditionCompiler.Identifier(field);
                referenceSql = $"R.{EffectConditionCompiler.Identifier(field)} = {sourceSide}";
                if (allowEmpty)
                    referenceSql = $"({sourceSide} IS NULL OR {sourceSide} = '' OR {referenceSql})";
            }

            var activeTag = string.Empty;
            if (check.TryGetProperty("activeTag", out var active) && active.ValueKind == JsonValueKind.Object)
            {
                var field = active.GetProperty("field").GetString()!.Trim();
                var expect = active.TryGetProperty("expect", out var e) && e.ValueKind == JsonValueKind.Number
                    ? e.GetInt32()
                    : 0;
                activeTag = $" AND R.{EffectConditionCompiler.Identifier(field)} = {expect}";
            }

            var fromMaster = "dbo." + EffectConditionCompiler.Identifier(plan.MasterTable!) + " M";
            var fromDetail = plan.DetailTable is null
                ? string.Empty
                : " CROSS JOIN dbo." + EffectConditionCompiler.Identifier(plan.DetailTable) + " D";
            var sql = "SELECT TOP 1 1 FROM " + fromMaster + fromDetail
                + " WHERE NOT EXISTS (SELECT 1 FROM dbo." + EffectConditionCompiler.Identifier(refTable)
                + " R WITH (NOLOCK) WHERE " + referenceSql + activeTag + ")";
            await using var command = new SqlCommand(sql, connection, transaction);
            if (await command.ExecuteScalarAsync(token) is not null)
                return check.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.String
                    ? msg.GetString()
                    : "引用数据不存在。";
        }
        return null;
    }

    private async Task<string?> CheckDuplicateAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        ModuleEffectPlan plan,
        EffectValidationPlan rule,
        CancellationToken token)
    {
        var root = rule.Params;
        var mode = root.TryGetProperty("mode", out var modeElement) && modeElement.ValueKind == JsonValueKind.String
            ? modeElement.GetString()!
            : "entity";
        if (!root.TryGetProperty("table", out var tableElement) || tableElement.ValueKind != JsonValueKind.String)
            throw new EffectConfigException("duplicate-check 缺少 table。");
        var table = tableElement.GetString()!.Trim();
        var keyFields = ParseStringArray(root, "keyFields");

        string sql;
        if (mode.Equals("within-doc", StringComparison.OrdinalIgnoreCase))
        {
            var columns = string.Join(", ", keyFields.Select(field => "D." + EffectConditionCompiler.Identifier(field)));
            sql = "SELECT TOP 1 1 FROM dbo." + EffectConditionCompiler.Identifier(plan.DetailTable!)
                + " D GROUP BY " + columns + " HAVING COUNT(*) > 1";
        }
        else
        {
            if (keyFields.Length == 0
                || !root.TryGetProperty("excludeSelf", out var excludeSelf)
                || !excludeSelf.TryGetProperty("source", out var source))
                throw new EffectConfigException("duplicate-check 缺少 keyFields/excludeSelf.source。");
            var sourceScope = source.TryGetProperty("scope", out var sc) && sc.ValueKind == JsonValueKind.String
                ? sc.GetString()!.Trim().ToUpperInvariant()
                : "MASTER";
            var sourceFields = ParseStringArray(source, "fields");
            if (sourceFields.Length != keyFields.Length)
                throw new EffectConfigException("duplicate-check keyFields 与 excludeSelf.source.fields 数量不一致。");
            var alias = sourceScope == "MASTER" ? "M" : "D";
            var conditions = new List<string>();
            for (var index = 0; index < keyFields.Length; index++)
                conditions.Add($"X.{EffectConditionCompiler.Identifier(keyFields[index])} = {alias}.{EffectConditionCompiler.Identifier(sourceFields[index])}");
            var selfExclusion = new List<string>();
            if (excludeSelf.TryGetProperty("keyFields", out var selfKeys)
                && selfKeys.ValueKind == JsonValueKind.Array && selfKeys.GetArrayLength() > 0)
            {
                var selfKeyList = ParseStringArray(excludeSelf, "keyFields");
                var selfSourceFields = ParseStringArray(source, "fields");
                for (var index = 0; index < Math.Min(selfKeyList.Length, selfSourceFields.Length); index++)
                    selfExclusion.Add($"X.{EffectConditionCompiler.Identifier(selfKeyList[index])} = {alias}.{EffectConditionCompiler.Identifier(selfSourceFields[index])}");
            }
            var exclusion = selfExclusion.Count > 0
                ? " AND NOT (" + string.Join(" AND ", selfExclusion) + ")"
                : string.Empty;
            sql = "SELECT TOP 1 1 FROM dbo." + EffectConditionCompiler.Identifier(table) + " X WITH (NOLOCK) WHERE "
                + string.Join(" AND ", conditions) + exclusion;
        }
        await using var command = new SqlCommand(sql, connection, transaction);
        if (await command.ExecuteScalarAsync(token) is not null)
            return rule.Message ?? "数据重复。";
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
