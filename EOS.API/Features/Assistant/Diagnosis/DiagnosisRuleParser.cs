using System.Text.Json;
using System.Text.RegularExpressions;

namespace EOS.API.Features.Assistant.Diagnosis;

/// <summary>
/// 已发布定义快照里"校验规则"段的解析：取规则身份（阶段 / 键 / 序号 / 启停 / 消息）与
/// **参数摘要**（触发条件的人话说明）。只读元数据，不做业务判定。
///
/// <para>
/// 唯一一处"复核判定"是 <c>reference-exists</c> 的单键形态：被引用表 + 单键 + 无 join/targets/mismatch，
/// 语义就是"找不到匹配行即违规"，一次点查即可定论。其余形态（多键、条件命中、跨表数量比较等）
/// 只能在保存时由引擎判定，**这里一律不判**——判错比不判害处大得多。
/// </para>
/// </summary>
internal static class DiagnosisRuleParser
{
    private static readonly Regex SafeIdentifierPattern = new("^[A-Za-z0-9_]+$", RegexOptions.Compiled);

    /// <summary>可只读判定的引用目标（单键、主表域、无 join/targets/mismatch/refCondition）。</summary>
    internal sealed record ReferenceTarget(
        string Table,
        string KeyField,
        bool AllowEmpty,
        string? ActiveTagField,
        int ActiveTagExpect);

    internal static bool IsSafeIdentifier(string? value) =>
        !string.IsNullOrWhiteSpace(value) && SafeIdentifierPattern.IsMatch(value);

    internal static IReadOnlyList<DiagnosisRule> Parse(JsonElement? rules)
    {
        if (rules is not { } element || element.ValueKind != JsonValueKind.Array) return [];
        var parsed = new List<DiagnosisRule>();
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var stage = GetString(item, "stage");
            var key = GetString(item, "validationKey");
            if (string.IsNullOrWhiteSpace(stage) || string.IsNullOrWhiteSpace(key)) continue;
            var parameters = ReadParameters(item);
            parsed.Add(new DiagnosisRule(
                stage.Trim(),
                GetInt32(item, "seq") ?? 0,
                key.Trim(),
                !item.TryGetProperty("enabled", out var enabled) || enabled.ValueKind != JsonValueKind.False,
                GetString(item, "message"),
                Describe(key.Trim(), parameters),
                parameters));
        }

        return parsed;
    }

    /// <summary>可只读判定的引用目标；形态不满足时返回空集（调用方据此记为"无法判定"，不猜）。</summary>
    internal static IReadOnlyList<ReferenceTarget> ReadReferenceTargets(JsonElement? parameters)
    {
        var targets = new List<ReferenceTarget>();
        if (parameters is not { } p || GetArray(p, "checks") is not { } checks) return targets;
        foreach (var check in checks.EnumerateArray())
        {
            if (check.ValueKind != JsonValueKind.Object) continue;
            var table = GetString(check, "refTable");
            if (!IsSafeIdentifier(table)) continue;
            if (GetArray(check, "join") is not null || GetArray(check, "targets") is not null) continue;
            if (GetObject(check, "mismatch") is not null || GetObject(check, "refCondition") is not null) continue;
            if (GetObject(check, "refKey") is not { } refKey) continue;
            var field = GetString(refKey, "field");
            var scope = GetString(refKey, "scope") ?? "MASTER";
            if (!scope.Trim().Equals("MASTER", StringComparison.OrdinalIgnoreCase)) continue;
            if (!IsSafeIdentifier(field)) continue;

            var activeTag = GetObject(check, "activeTag");
            targets.Add(new ReferenceTarget(
                table!.Trim(),
                field!.Trim(),
                check.TryGetProperty("allowEmpty", out var allow) && allow.ValueKind == JsonValueKind.True,
                activeTag is { } tag ? GetString(tag, "field") : null,
                activeTag is { } tagged && GetInt32(tagged, "expect") is { } expect ? expect : 1));
        }

        return targets;
    }

    /// <summary>触发条件摘要（来自规则参数；解析不出来就不写，不编）。</summary>
    internal static string? Describe(string validationKey, JsonElement? parameters)
    {
        if (parameters is not { } p || p.ValueKind != JsonValueKind.Object) return null;
        return validationKey.ToLowerInvariant() switch
        {
            "reference-exists" => DescribeReference(p),
            "qty-not-exceed" => DescribeQuantity(p),
            "line-require" => DescribeLineRequire(p),
            "duplicate-check" => $"重复检查（模式 {GetString(p, "mode") ?? "未声明"}）",
            "period-overlap" => DescribePeriodOverlap(p),
            "no-cycle" => DescribeNoCycle(p),
            "custom-validation" => $"定制校验（handler {GetString(p, "handler") ?? "未声明"}）",
            _ => null,
        };
    }

    private static string? DescribeReference(JsonElement parameters)
    {
        if (GetArray(parameters, "checks") is not { } checks) return null;
        foreach (var check in checks.EnumerateArray())
        {
            if (check.ValueKind != JsonValueKind.Object) continue;
            var table = GetString(check, "refTable");
            if (string.IsNullOrWhiteSpace(table)) continue;
            var keys = new List<string>();
            if (GetObject(check, "refKey") is { } refKey && GetString(refKey, "field") is { } single)
            {
                keys.Add(single);
            }
            else if (GetArray(check, "join") is { } join)
            {
                foreach (var pair in join.EnumerateArray())
                {
                    if (pair.ValueKind == JsonValueKind.Object && GetString(pair, "target") is { } target)
                    {
                        keys.Add(target);
                    }
                }
            }

            var lineField = GetString(check, "lineField");
            var text = $"被引用表 {table.Trim()} 找不到匹配行（关联键 {string.Join(",", keys)}）";
            if (!string.IsNullOrWhiteSpace(lineField)) text += $"，命中行报 {lineField.Trim()}";
            return text;
        }

        return null;
    }

    private static string? DescribeQuantity(JsonElement parameters)
    {
        var mode = GetString(parameters, "mode") ?? "未声明";
        var target = GetArray(parameters, "checks") is { } checks
            ? checks.EnumerateArray()
                .Where(check => check.ValueKind == JsonValueKind.Object)
                .Select(check => GetString(check, "targetTable"))
                .FirstOrDefault(table => !string.IsNullOrWhiteSpace(table))
            : null;
        return string.IsNullOrWhiteSpace(target)
            ? $"数量不得超过限额（模式 {mode}）"
            : $"数量不得超过被引用表 {target.Trim()} 的可用量（模式 {mode}）";
    }

    private static string? DescribeLineRequire(JsonElement parameters)
    {
        if (GetArray(parameters, "checks") is not { } checks) return null;
        foreach (var check in checks.EnumerateArray())
        {
            if (check.ValueKind != JsonValueKind.Object) continue;
            var field = GetString(check, "field");
            if (string.IsNullOrWhiteSpace(field)) continue;
            var scope = GetString(check, "scope") ?? "DETAIL";
            return $"{scope.Trim()} 域字段 {field.Trim()} 必须非空或满足断言";
        }

        return null;
    }

    private static string? DescribePeriodOverlap(JsonElement parameters)
    {
        var detail = GetString(parameters, "detailTable");
        var range = GetObject(parameters, "rangeFields");
        var begin = range is { } fields ? GetString(fields, "begin") : null;
        var end = range is { } fields2 ? GetString(fields2, "end") : null;
        return string.IsNullOrWhiteSpace(detail)
            ? null
            : $"期间 {begin}–{end} 不得与其它单据重叠（明细表 {detail.Trim()}）";
    }

    private static string? DescribeNoCycle(JsonElement parameters)
    {
        if (GetArray(parameters, "checks") is not { } checks) return null;
        foreach (var check in checks.EnumerateArray())
        {
            if (check.ValueKind != JsonValueKind.Object) continue;
            var table = GetString(check, "table");
            if (string.IsNullOrWhiteSpace(table)) continue;
            return $"引用关系不得成环（关系表 {table.Trim()}）";
        }

        return null;
    }

    /// <summary>快照里的结构化列以**文本**形式承载（PARAM_STRUCT 为字符串）；对象形态一并兼容。</summary>
    private static JsonElement? ReadParameters(JsonElement rule)
    {
        if (!rule.TryGetProperty("params", out var value)) return null;
        if (value.ValueKind == JsonValueKind.Object) return value;
        if (value.ValueKind != JsonValueKind.String) return null;
        var text = value.GetString();
        if (string.IsNullOrWhiteSpace(text)) return null;
        try
        {
            return JsonDocument.Parse(text).RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? GetInt32(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out var number)
            ? number
            : null;

    private static JsonElement? GetObject(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object ? value : null;

    private static JsonElement? GetArray(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array ? value : null;
}
