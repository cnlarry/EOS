using System.Text.Json;

namespace EOS.API.Data.ValidationRules;

/// <summary>
/// 校验模板注册与参数 Schema 校验（v0）：
/// 目前只做结构与闭式集合校验（Key/阶段/参数形态），物理表字段、关系注册校验由接入
/// Definition 校验器时补充。禁止按模块号分支。
/// </summary>
public static class ValidationRuleRegistry
{
    private static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> ParamKeysByTemplate =
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["qty-not-exceed"] = KeySet("mode", "checks"),
            ["reference-exists"] = KeySet("checks"),
            ["duplicate-check"] = KeySet("mode", "table", "keyFields", "excludeSelf"),
            ["line-require"] = KeySet("checks"),
        };

    private static readonly IReadOnlySet<string> QtyCheckKeys = KeySet("targetTable", "match", "thisQty", "usage", "limit", "offset", "message", "switch");
    private static readonly IReadOnlySet<string> QtySwitchKeys = KeySet("key", "expect");
    private static readonly IReadOnlySet<string> LineRequireCheckKeys = KeySet("scope", "field", "triggers", "message");
    private static readonly IReadOnlySet<string> LineRequireTriggerKeys = KeySet("scope", "field", "op", "value");
    private static readonly IReadOnlySet<string> LineRequireOps = KeySet("GT", "GE", "LT", "LE", "EQ", "NEQ");
    private static readonly IReadOnlySet<string> QtyBlockKeys = KeySet("scope", "terms", "fields");
    private static readonly IReadOnlySet<string> ReferenceCheckKeys = KeySet("refTable", "allowEmpty", "join", "refKey", "activeTag", "message", "lineField");
    private static readonly IReadOnlySet<string> ReferencePairKeys = KeySet("target", "source");
    private static readonly IReadOnlySet<string> ExcludeSelfKeys = KeySet("keyFields", "source");
    private static readonly IReadOnlySet<string> SourceBlockKeys = KeySet("scope", "fields");

    private static readonly HashSet<string> KnownKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "qty-not-exceed",
        "reference-exists",
        "duplicate-check",
        "line-require",
    };

    private static readonly HashSet<string> KnownStages = new(StringComparer.OrdinalIgnoreCase)
    {
        "SAVE",
        "APPROVE",
        "DEAPPROVE",
    };

    private static readonly HashSet<string> KnownScopes = new(StringComparer.OrdinalIgnoreCase)
    {
        "MASTER",
        "DETAIL",
        "TABLE",
        "TARGET",
        "CONSTANT",
    };

    public static bool IsKnownKey(string key) => KnownKeys.Contains(key);

    public static bool IsKnownStage(string stage) => KnownStages.Contains(stage);

    public static void Validate(ValidationRuleConfig rule, List<string> issues)
    {
        if (string.IsNullOrWhiteSpace(rule.RuleId))
            issues.Add($"校验规则缺少 ruleId");
        if (!IsKnownKey(rule.ValidationKey))
            issues.Add($"校验规则 {Label(rule)}：未知 validationKey '{rule.ValidationKey}'");
        if (!IsKnownStage(rule.Stage))
            issues.Add($"校验规则 {Label(rule)}：未知 stage '{rule.Stage}'（仅 SAVE/APPROVE/DEAPPROVE）");
        if (rule.Params is not { } p || p.ValueKind != JsonValueKind.Object)
        {
            issues.Add($"校验规则 {Label(rule)}：params 必须是 JSON 对象");
            return;
        }
        if (ParamKeysByTemplate.TryGetValue(rule.ValidationKey, out var allowedParams))
            RejectUnknownKeys(rule, p, allowedParams, "params", issues);
        switch (rule.ValidationKey.ToLowerInvariant())
        {
            case "qty-not-exceed":
                ValidateQtyNotExceed(rule, p, issues);
                break;
            case "reference-exists":
                ValidateReferenceExists(rule, p, issues);
                break;
            case "duplicate-check":
                ValidateDuplicateCheck(rule, p, issues);
                break;
            case "line-require":
                ValidateLineRequire(rule, p, issues);
                break;
        }
    }

    private static void ValidateQtyNotExceed(ValidationRuleConfig rule, JsonElement p, List<string> issues)
    {
        var mode = GetString(p, "mode");
        if (mode is not ("usage-not-exceed" or "not-below-progress" or "this-not-exceed"))
            issues.Add($"校验规则 {Label(rule)}：qty-not-exceed.mode 仅允许 usage-not-exceed / not-below-progress / this-not-exceed");
        var requireLimit = mode is null or "usage-not-exceed" or "this-not-exceed";
        var checks = GetArray(p, "checks");
        if (checks is not { } arr || arr.GetArrayLength() == 0)
        {
            issues.Add($"校验规则 {Label(rule)}：qty-not-exceed.checks 必须是非空数组");
            return;
        }
        var index = 0;
        foreach (var check in arr.EnumerateArray())
        {
            var where = $"checks[{index}]";
            if (check.ValueKind != JsonValueKind.Object)
            {
                issues.Add($"校验规则 {Label(rule)}：{where} 必须是对象");
                index++;
                continue;
            }
            RejectUnknownKeys(rule, check, QtyCheckKeys, where, issues);
            var switchElement = GetObject(check, "switch");
            if (switchElement is not null)
                ValidateSwitchShape(rule, switchElement.Value, $"{where}.switch", issues);
            var match = GetArray(check, "match");
            if (match is null || match.Value.GetArrayLength() == 0)
                issues.Add($"校验规则 {Label(rule)}：{where}.match 必须是非空数组");
            else
                ValidateMatchItems(rule, match.Value, $"{where}.match", issues);
            if (!ValidateQtyBlock(rule, check, "thisQty", $"{where}.thisQty", issues))
                issues.Add($"校验规则 {Label(rule)}：{where}.thisQty 缺失");
            var requireUsage = mode is null or "usage-not-exceed" or "not-below-progress";
            if (requireUsage && !ValidateQtyBlock(rule, check, "usage", $"{where}.usage", issues))
                issues.Add($"校验规则 {Label(rule)}：{where}.usage 缺失");
            var hasLimit = GetObject(check, "limit") is not null;
            if (requireLimit && !hasLimit)
                issues.Add($"校验规则 {Label(rule)}：{where}.limit 缺失（{mode ?? "usage-not-exceed"} 模式必须给出限额）");
            if (hasLimit)
                ValidateQtyBlock(rule, check, "limit", $"{where}.limit", issues);
            var offset = GetObject(check, "offset");
            if (offset is not null)
                ValidateQtyBlock(rule, offset.Value, "offset", $"{where}.offset", issues);
            index++;
        }
    }

    private static void ValidateSwitchShape(ValidationRuleConfig rule, JsonElement element, string where, List<string> issues)
    {
        RejectUnknownKeys(rule, element, QtySwitchKeys, where, issues);
        if (string.IsNullOrWhiteSpace(GetString(element, "key")))
            issues.Add($"校验规则 {Label(rule)}：{where}.key 不能为空（SYSSS 开关列名）");
        if (element.TryGetProperty("expect", out var expect) && expect.ValueKind != JsonValueKind.Number)
            issues.Add($"校验规则 {Label(rule)}：{where}.expect 必须是数字（0/1）");
    }

    /// <summary>
    /// line-require: document lines matching any trigger must carry a non-empty
    /// field (v1: DETAIL scope only, e.g. bad-quantity lines require a bad depot).
    /// </summary>
    private static void ValidateLineRequire(ValidationRuleConfig rule, JsonElement p, List<string> issues)
    {
        var checks = GetArray(p, "checks");
        if (checks is not { } arr || arr.GetArrayLength() == 0)
        {
            issues.Add($"校验规则 {Label(rule)}：line-require.checks 必须是非空数组");
            return;
        }
        var index = 0;
        foreach (var check in arr.EnumerateArray())
        {
            var where = $"checks[{index}]";
            if (check.ValueKind != JsonValueKind.Object)
            {
                issues.Add($"校验规则 {Label(rule)}：{where} 必须是对象");
                index++;
                continue;
            }
            RejectUnknownKeys(rule, check, LineRequireCheckKeys, where, issues);
            var scope = GetString(check, "scope");
            if (!string.Equals(scope, "DETAIL", StringComparison.OrdinalIgnoreCase))
                issues.Add($"校验规则 {Label(rule)}：{where}.scope 仅允许 DETAIL");
            if (string.IsNullOrWhiteSpace(GetString(check, "field")))
                issues.Add($"校验规则 {Label(rule)}：{where}.field 不能为空");
            var triggers = GetArray(check, "triggers");
            if (triggers is not { } triggersArr || triggersArr.GetArrayLength() == 0)
            {
                issues.Add($"校验规则 {Label(rule)}：{where}.triggers 必须是非空数组");
            }
            else
            {
                var triggerIndex = 0;
                foreach (var trigger in triggersArr.EnumerateArray())
                {
                    var triggerWhere = $"{where}.triggers[{triggerIndex}]";
                    if (trigger.ValueKind != JsonValueKind.Object)
                    {
                        issues.Add($"校验规则 {Label(rule)}：{triggerWhere} 必须是对象");
                    }
                    else
                    {
                        RejectUnknownKeys(rule, trigger, LineRequireTriggerKeys, triggerWhere, issues);
                        if (!string.Equals(GetString(trigger, "scope"), "DETAIL", StringComparison.OrdinalIgnoreCase))
                            issues.Add($"校验规则 {Label(rule)}：{triggerWhere}.scope 仅允许 DETAIL");
                        if (string.IsNullOrWhiteSpace(GetString(trigger, "field")))
                            issues.Add($"校验规则 {Label(rule)}：{triggerWhere}.field 不能为空");
                        var op = GetString(trigger, "op");
                        if (op is null || !LineRequireOps.Contains(op))
                            issues.Add($"校验规则 {Label(rule)}：{triggerWhere}.op 仅允许 GT/GE/LT/LE/EQ/NEQ");
                        if (trigger.TryGetProperty("value", out var value) && value.ValueKind != JsonValueKind.Number)
                            issues.Add($"校验规则 {Label(rule)}：{triggerWhere}.value 必须是数字");
                    }
                    triggerIndex++;
                }
            }
            index++;
        }
    }

    private static bool ValidateQtyBlock(ValidationRuleConfig rule, JsonElement parent, string prop, string where, List<string> issues)
    {
        var block = GetObject(parent, prop);
        if (block is null)
        {
            return false;
        }
        RejectUnknownKeys(rule, block.Value, QtyBlockKeys, where, issues);
        var scope = GetString(block.Value, "scope");
        if (scope is null || !KnownScopes.Contains(scope))
            issues.Add($"校验规则 {Label(rule)}：{where}.scope 必须是 MASTER/DETAIL/TABLE/TARGET/CONSTANT");
        var terms = GetArray(block.Value, "terms");
        if (terms is { } t && t.GetArrayLength() > 0)
        {
            foreach (var term in t.EnumerateArray())
            {
                var field = GetString(term, "field");
                if (string.IsNullOrWhiteSpace(field))
                    issues.Add($"校验规则 {Label(rule)}：{where}.terms[].field 不能为空");
                var coef = GetInt32(term, "coef");
                if (coef is not (1 or -1))
                    issues.Add($"校验规则 {Label(rule)}：{where}.terms[].coef 仅允许 1 / -1");
            }
        }
        else
        {
            var fields = GetArray(block.Value, "fields");
            if (fields is not { } f || f.GetArrayLength() == 0)
                issues.Add($"校验规则 {Label(rule)}：{where} 需要 terms 或 fields 之一");
        }
        return true;
    }

    private static void ValidateMatchItems(ValidationRuleConfig rule, JsonElement match, string where, List<string> issues)
    {
        foreach (var item in match.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                issues.Add($"校验规则 {Label(rule)}：{where}[] 必须是对象");
                continue;
            }
            RejectUnknownKeys(rule, item, ReferencePairKeys, where + "[]", issues);
            if (string.IsNullOrWhiteSpace(GetString(item, "target")))
                issues.Add($"校验规则 {Label(rule)}：{where}[].target 不能为空");
            var source = GetObject(item, "source");
            if (source is null || string.IsNullOrWhiteSpace(GetString(source.Value, "field")))
                issues.Add($"校验规则 {Label(rule)}：{where}[].source.field 不能为空");
            if (source is { } sourceObject)
            {
                var allowedSource = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "scope", "field", "table", "constant",
                };
                RejectUnknownKeys(rule, sourceObject, allowedSource, where + "[].source", issues);
            }
        }
    }

    private static void ValidateReferenceExists(ValidationRuleConfig rule, JsonElement p, List<string> issues)
    {
        var checks = GetArray(p, "checks");
        if (checks is not { } arr || arr.GetArrayLength() == 0)
        {
            issues.Add($"校验规则 {Label(rule)}：reference-exists.checks 必须是非空数组");
            return;
        }
        var index = 0;
        foreach (var check in arr.EnumerateArray())
        {
            var where = $"checks[{index}]";
            if (check.ValueKind != JsonValueKind.Object)
            {
                issues.Add($"校验规则 {Label(rule)}：{where} 必须是对象");
                index++;
                continue;
            }
            RejectUnknownKeys(rule, check, ReferenceCheckKeys, where, issues);
            if (string.IsNullOrWhiteSpace(GetString(check, "refTable")))
                issues.Add($"校验规则 {Label(rule)}：{where}.refTable 不能为空");
            var refKey = GetObject(check, "refKey");
            var join = GetArray(check, "join");
            if (refKey is null && (join is null || join.Value.GetArrayLength() == 0))
                issues.Add($"校验规则 {Label(rule)}：{where} 需要 refKey 或 join");
            if (refKey is not null && string.IsNullOrWhiteSpace(GetString(refKey.Value, "field")))
                issues.Add($"校验规则 {Label(rule)}：{where}.refKey.field 不能为空");
            if (refKey is { } refKeyObject)
            {
                var allowedRefKey = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "scope", "field" };
                RejectUnknownKeys(rule, refKeyObject, allowedRefKey, where + ".refKey", issues);
            }
            if (join is { } joinArray)
                foreach (var pair in joinArray.EnumerateArray())
                {
                    if (pair.ValueKind != JsonValueKind.Object)
                    {
                        issues.Add($"校验规则 {Label(rule)}：{where}.join[] 必须是对象");
                        continue;
                    }
                    RejectUnknownKeys(rule, pair, ReferencePairKeys, where + ".join[]", issues);
                    if (string.IsNullOrWhiteSpace(GetString(pair, "target")))
                        issues.Add($"校验规则 {Label(rule)}：{where}.join[].target 不能为空");
                    var pairSource = GetObject(pair, "source");
                    if (pairSource is null || string.IsNullOrWhiteSpace(GetString(pairSource.Value, "field")))
                        issues.Add($"校验规则 {Label(rule)}：{where}.join[].source.field 不能为空");
                    if (pairSource is { } pairSourceObject)
                    {
                        var allowedPairSource = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                        {
                            "scope", "field", "table", "constant",
                        };
                        RejectUnknownKeys(rule, pairSourceObject, allowedPairSource, where + ".join[].source", issues);
                    }
                }
            if (check.TryGetProperty("allowEmpty", out var allowEmpty)
                && allowEmpty.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                issues.Add($"校验规则 {Label(rule)}：{where}.allowEmpty 必须是布尔值");
            if (check.TryGetProperty("activeTag", out var activeTag))
            {
                if (activeTag.ValueKind != JsonValueKind.Object)
                {
                    issues.Add($"校验规则 {Label(rule)}：{where}.activeTag 必须是对象");
                }
                else
                {
                    var activeKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "field", "expect" };
                    RejectUnknownKeys(rule, activeTag, activeKeys, where + ".activeTag", issues);
                    if (string.IsNullOrWhiteSpace(GetString(activeTag, "field")))
                        issues.Add($"校验规则 {Label(rule)}：{where}.activeTag.field 不能为空");
                    if (activeTag.TryGetProperty("expect", out var expect) && expect.ValueKind != JsonValueKind.Number)
                        issues.Add($"校验规则 {Label(rule)}：{where}.activeTag.expect 必须是数字");
                }
            }
            index++;
        }
    }

    private static void ValidateDuplicateCheck(ValidationRuleConfig rule, JsonElement p, List<string> issues)
    {
        var mode = GetString(p, "mode");
        if (mode is not ("within-doc" or "entity"))
            issues.Add($"校验规则 {Label(rule)}：duplicate-check.mode 仅允许 within-doc / entity");
        if (mode == "entity" && string.IsNullOrWhiteSpace(GetString(p, "table")))
            issues.Add($"校验规则 {Label(rule)}：duplicate-check.table 必填（entity 模式）");
        var keyFields = GetArray(p, "keyFields");
        if (keyFields is not { } k || k.GetArrayLength() == 0)
            issues.Add($"校验规则 {Label(rule)}：duplicate-check.keyFields 必须是非空数组");
        var exclude = GetObject(p, "excludeSelf");
        if (exclude is not null)
        {
            RejectUnknownKeys(rule, exclude.Value, ExcludeSelfKeys, "excludeSelf", issues);
            var excludeKeys = GetArray(exclude.Value, "keyFields");
            if (excludeKeys is null || excludeKeys.Value.GetArrayLength() == 0)
                issues.Add($"校验规则 {Label(rule)}：excludeSelf.keyFields 必须是非空数组");
            var excludeSource = GetObject(exclude.Value, "source");
            if (excludeSource is not null)
                RejectUnknownKeys(rule, excludeSource.Value, SourceBlockKeys, "excludeSelf.source", issues);
        }
    }

    private static void RejectUnknownKeys(
        ValidationRuleConfig rule,
        JsonElement element,
        IReadOnlySet<string> allowedKeys,
        string where,
        ICollection<string> issues)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (!allowedKeys.Contains(property.Name))
                issues.Add($"校验规则 {Label(rule)}：未知参数键 {where}.{property.Name}");
        }
    }

    private static IReadOnlySet<string> KeySet(params string[] keys) =>
        new HashSet<string>(keys, StringComparer.OrdinalIgnoreCase);

    private static string Label(ValidationRuleConfig rule) =>
        string.IsNullOrWhiteSpace(rule.RuleId) ? $"({rule.ValidationKey})" : rule.RuleId;

    private static string? GetString(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static JsonElement? GetObject(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Object ? v : null;

    private static JsonElement? GetArray(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Array ? v : null;

    private static int? GetInt32(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : null;
}
