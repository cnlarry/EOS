using System.Text.Json;

namespace EOS.API.Data.ValidationRules;

/// <summary>
/// 从模块 Definition JSON 中提取 validationRules 段并做 schema 校验（最小实现）。
/// 只解释通用段，不含任何模块号分支。
/// </summary>
public static class ModuleValidationDefinitionLoader
{
    public static ModuleValidationLoadResult Load(string json)
    {
        var issues = new List<string>();
        var rules = new List<ValidationRuleConfig>();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            issues.Add("Definition 根节点必须是 JSON 对象");
            return new ModuleValidationLoadResult(false, rules, issues);
        }
        if (!root.TryGetProperty("validationRules", out var array))
            return new ModuleValidationLoadResult(true, rules, issues);
        if (array.ValueKind != JsonValueKind.Array)
        {
            issues.Add("validationRules 必须是数组");
            return new ModuleValidationLoadResult(false, rules, issues);
        }
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                issues.Add("validationRules[] 中存在非对象项");
                continue;
            }
            var ruleId = GetString(item, "ruleId") ?? string.Empty;
            var key = GetString(item, "validationKey") ?? string.Empty;
            var stage = GetString(item, "stage") ?? string.Empty;
            var enabled = !item.TryGetProperty("enabled", out var en) || en.ValueKind != JsonValueKind.False;
            var message = GetString(item, "message");
            JsonElement? parameters = item.TryGetProperty("params", out var pa) ? pa.Clone() : null;
            var rule = new ValidationRuleConfig(ruleId, key, stage, enabled, message, parameters);
            rules.Add(rule);
            ValidationRuleRegistry.Validate(rule, issues);
        }
        return new ModuleValidationLoadResult(issues.Count == 0, rules, issues);
    }

    private static string? GetString(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
