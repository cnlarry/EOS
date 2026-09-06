using System.Text.Json;

namespace EOS.API.Data.ValidationRules;

/// <summary>
/// 校验模板注册与参数 Schema 校验（v0）：
/// 目前只做结构与闭式集合校验（Key/阶段/参数形态），物理表字段、关系注册校验由接入
/// Definition 校验器时补充。禁止按模块号分支。
/// </summary>
public static class ValidationRuleRegistry
{
    private static readonly HashSet<string> KnownKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "qty-not-exceed",
        "reference-exists",
        "duplicate-check",
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
        }
    }

    private static void ValidateQtyNotExceed(ValidationRuleConfig rule, JsonElement p, List<string> issues)
    {
        var mode = GetString(p, "mode");
        if (mode is not ("usage-not-exceed" or "not-below-progress"))
            issues.Add($"校验规则 {Label(rule)}：qty-not-exceed.mode 仅允许 usage-not-exceed / not-below-progress");
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
            var match = GetArray(check, "match");
            if (match is null || match.Value.GetArrayLength() == 0)
                issues.Add($"校验规则 {Label(rule)}：{where}.match 必须是非空数组");
            else
                ValidateMatchItems(rule, match.Value, $"{where}.match", issues);
            ValidateQtyBlock(rule, check, "thisQty", $"{where}.thisQty", issues);
            ValidateQtyBlock(rule, check, "usage", $"{where}.usage", issues);
            ValidateQtyBlock(rule, check, "limit", $"{where}.limit", issues);
            var offset = GetObject(check, "offset");
            if (offset is not null)
                ValidateQtyBlock(rule, offset.Value, "offset", $"{where}.offset", issues);
            index++;
        }
    }

    private static void ValidateQtyBlock(ValidationRuleConfig rule, JsonElement parent, string prop, string where, List<string> issues)
    {
        var block = GetObject(parent, prop);
        if (block is null)
        {
            issues.Add($"校验规则 {Label(rule)}：缺少 {where}");
            return;
        }
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
    }

    private static void ValidateMatchItems(ValidationRuleConfig rule, JsonElement match, string where, List<string> issues)
    {
        foreach (var item in match.EnumerateArray())
        {
            if (string.IsNullOrWhiteSpace(GetString(item, "target")))
                issues.Add($"校验规则 {Label(rule)}：{where}[].target 不能为空");
            var source = GetObject(item, "source");
            if (source is null || string.IsNullOrWhiteSpace(GetString(source.Value, "field")))
                issues.Add($"校验规则 {Label(rule)}：{where}[].source.field 不能为空");
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
            if (string.IsNullOrWhiteSpace(GetString(check, "refTable")))
                issues.Add($"校验规则 {Label(rule)}：{where}.refTable 不能为空");
            var refKey = GetObject(check, "refKey");
            var join = GetArray(check, "join");
            if (refKey is null && (join is null || join.Value.GetArrayLength() == 0))
                issues.Add($"校验规则 {Label(rule)}：{where} 需要 refKey 或 join");
            if (refKey is not null && string.IsNullOrWhiteSpace(GetString(refKey.Value, "field")))
                issues.Add($"校验规则 {Label(rule)}：{where}.refKey.field 不能为空");
            index++;
        }
    }

    private static void ValidateDuplicateCheck(ValidationRuleConfig rule, JsonElement p, List<string> issues)
    {
        var mode = GetString(p, "mode");
        if (mode is not ("within-doc" or "entity"))
            issues.Add($"校验规则 {Label(rule)}：duplicate-check.mode 仅允许 within-doc / entity");
        var keyFields = GetArray(p, "keyFields");
        if (keyFields is not { } k || k.GetArrayLength() == 0)
            issues.Add($"校验规则 {Label(rule)}：duplicate-check.keyFields 必须是非空数组");
        var exclude = GetObject(p, "excludeSelf");
        if (exclude is not null)
        {
            var excludeKeys = GetArray(exclude.Value, "keyFields");
            if (excludeKeys is null || excludeKeys.Value.GetArrayLength() == 0)
                issues.Add($"校验规则 {Label(rule)}：excludeSelf.keyFields 必须是非空数组");
        }
    }

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
