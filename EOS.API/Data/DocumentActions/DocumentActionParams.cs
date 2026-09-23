using System.Text.Json;
using EOS.API.Models;

namespace EOS.API.Data.DocumentActions;

/// <summary>
/// Input-parameter declaration of a document action (PARAM_STRUCT of a MANUAL row). The client renders
/// a small form from this declaration and posts the values back in the request's <c>params</c> object;
/// the server validates them against the same declaration before the handler runs.
///
/// Shape (closed):
/// <code>
/// { "fields": [ { "key": "relocateTo", "label": "目标库位", "type": "string",
///                 "required": true, "maxLength": 50 } ] }
/// </code>
/// The declaration is deliberately narrow: an action's parameters are user input, not physical column
/// references, so there is nothing here for SQL to be built from.
/// </summary>
public static class DocumentActionParams
{
    private const int MaxFields = 10;
    private const int MaxKeyLength = 40;
    private const int MaxLabelLength = 50;

    private static readonly IReadOnlySet<string> Types = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "string",
        "number",
        "date",
        "bool",
    };

    /// <summary>Validates a PARAM_STRUCT declaration (config save + publish gate); empty means valid.</summary>
    public static IReadOnlyList<string> Validate(string? declarationJson)
    {
        if (string.IsNullOrWhiteSpace(declarationJson))
        {
            return Array.Empty<string>();
        }
        try
        {
            using var document = JsonDocument.Parse(declarationJson);
            return ValidateDeclaration(document.RootElement.Clone());
        }
        catch (JsonException)
        {
            return ["操作参数声明不是合法 JSON。"];
        }
    }

    /// <summary>Validates an already-parsed declaration; empty means valid.</summary>
    public static IReadOnlyList<string> ValidateDeclaration(JsonElement? declaration)
    {
        var issues = new List<string>();
        if (declaration is null)
        {
            return issues;
        }
        if (declaration.Value.ValueKind != JsonValueKind.Object)
        {
            issues.Add("操作参数声明必须是 JSON 对象（{\"fields\":[…]}）。");
            return issues;
        }
        foreach (var property in declaration.Value.EnumerateObject())
        {
            if (!property.Name.Equals("fields", StringComparison.OrdinalIgnoreCase))
            {
                issues.Add($"操作参数声明含未登记键 '{property.Name}'（仅允许 fields）。");
            }
        }
        if (!declaration.Value.TryGetProperty("fields", out var fields) || fields.ValueKind != JsonValueKind.Array)
        {
            issues.Add("操作参数声明缺少 fields 数组。");
            return issues;
        }
        if (fields.GetArrayLength() > MaxFields)
        {
            issues.Add($"操作参数数量超过上限（{MaxFields}）。");
        }
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var index = 0;
        foreach (var field in fields.EnumerateArray())
        {
            index++;
            if (field.ValueKind != JsonValueKind.Object)
            {
                issues.Add($"操作参数第 {index} 项必须是对象。");
                continue;
            }
            foreach (var property in field.EnumerateObject())
            {
                if (property.Name is not ("key" or "label" or "type" or "required" or "maxLength"))
                {
                    issues.Add($"操作参数第 {index} 项含未登记键 '{property.Name}'。");
                }
            }
            var key = Text(field, "key");
            if (key.Length == 0 || key.Length > MaxKeyLength || !CSharpIdentifier(key))
            {
                issues.Add($"操作参数第 {index} 项的 key 非法（要求字母开头、仅含字母数字，≤{MaxKeyLength} 字符）。");
            }
            else if (!seen.Add(key))
            {
                issues.Add($"操作参数 key 重复：'{key}'。");
            }
            var label = Text(field, "label");
            if (label.Length == 0)
            {
                issues.Add($"操作参数 '{key}' 缺少 label。");
            }
            else if (label.Length > MaxLabelLength)
            {
                issues.Add($"操作参数 '{key}' 的 label 超过 {MaxLabelLength} 字符。");
            }
            var type = Text(field, "type");
            if (!Types.Contains(type))
            {
                issues.Add($"操作参数 '{key}' 的 type '{type}' 不在 string/number/date/bool 内。");
            }
            if (field.TryGetProperty("maxLength", out var maxLength)
                && maxLength.ValueKind != JsonValueKind.Null
                && (maxLength.ValueKind != JsonValueKind.Number
                    || !maxLength.TryGetInt32(out var length)
                    || length < 1
                    || length > 400))
            {
                issues.Add($"操作参数 '{key}' 的 maxLength 必须在 1~400 之间。");
            }
        }
        return issues;
    }

    /// <summary>
    /// Validates the posted values against the declaration and returns them as trimmed text.
    /// Unknown keys are rejected: the declaration is the whitelist, so a client cannot smuggle
    /// parameters a handler does not expect.
    /// </summary>
    public static (IReadOnlyDictionary<string, string?> Values, IReadOnlyList<FieldError> Errors) Read(
        JsonElement? request,
        JsonElement? declaration)
    {
        var errors = new List<FieldError>();
        // Fail-closed: a declaration that does not pass its own validation has no usable whitelist,
        // and running the handler on unchecked input is exactly what the whitelist exists to prevent.
        if (declaration is not null && ValidateDeclaration(declaration).Count > 0)
        {
            return (Empty, [new FieldError("params", "操作参数声明非法，请联系系统管理员。", "PARAM_DECLARATION_INVALID")]);
        }
        var fields = ReadFields(declaration);
        if (request is not null && request.Value.ValueKind is not (JsonValueKind.Object or JsonValueKind.Null or JsonValueKind.Undefined))
        {
            return (Empty, [new FieldError("params", "params 必须是 JSON 对象。", "PARAM_INVALID")]);
        }
        var posted = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        if (request is { ValueKind: JsonValueKind.Object } body)
        {
            foreach (var property in body.EnumerateObject())
            {
                posted[property.Name] = property.Value;
            }
        }

        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in fields)
        {
            var hasValue = posted.TryGetValue(field.Key, out var raw);
            var text = !hasValue || raw.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
                ? null
                : raw.ValueKind == JsonValueKind.String ? raw.GetString() : raw.GetRawText();
            text = text?.Trim();
            if (string.IsNullOrEmpty(text))
            {
                if (field.Required)
                {
                    errors.Add(new FieldError(field.Key, $"参数“{field.Label}”必填。", "PARAM_REQUIRED"));
                }
                values[field.Key] = null;
                continue;
            }
            if (field.MaxLength is int limit && text.Length > limit)
            {
                errors.Add(new FieldError(field.Key, $"参数“{field.Label}”超过 {limit} 个字符。", "PARAM_TOO_LONG"));
                continue;
            }
            if (!MatchesType(field.Type, text))
            {
                errors.Add(new FieldError(field.Key, $"参数“{field.Label}”不是合法的{TypeLabel(field.Type)}。", "PARAM_INVALID"));
                continue;
            }
            values[field.Key] = text;
        }
        foreach (var key in posted.Keys.Where(key => !fields.Any(field => field.Key.Equals(key, StringComparison.OrdinalIgnoreCase))))
        {
            errors.Add(new FieldError(key, $"参数“{key}”不在该操作的参数声明内。", "PARAM_UNKNOWN"));
        }
        return (values, errors);
    }

    private static readonly IReadOnlyDictionary<string, string?> Empty = new Dictionary<string, string?>();

    private static bool MatchesType(string type, string value) => type.ToLowerInvariant() switch
    {
        "number" => decimal.TryParse(value, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out _),
        "date" => DateTime.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out _),
        "bool" => bool.TryParse(value, out _) || value is "0" or "1" or "是" or "否",
        _ => true,
    };

    private static string TypeLabel(string type) => type.ToLowerInvariant() switch
    {
        "number" => "数字",
        "date" => "日期",
        "bool" => "布尔值",
        _ => "文本",
    };

    private sealed record DocumentActionParamField(string Key, string Label, string Type, bool Required, int? MaxLength);

    private static IReadOnlyList<DocumentActionParamField> ReadFields(JsonElement? declaration)
    {
        var fields = new List<DocumentActionParamField>();
        if (declaration is not { ValueKind: JsonValueKind.Object } root
            || !root.TryGetProperty("fields", out var items)
            || items.ValueKind != JsonValueKind.Array)
        {
            return fields;
        }
        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }
            var key = Text(item, "key");
            if (key.Length == 0)
            {
                continue;
            }
            fields.Add(new DocumentActionParamField(
                key,
                Text(item, "label") is { Length: > 0 } label ? label : key,
                Text(item, "type") is { Length: > 0 } type ? type : "string",
                item.TryGetProperty("required", out var required) && required.ValueKind == JsonValueKind.True,
                item.TryGetProperty("maxLength", out var maxLength) && maxLength.ValueKind == JsonValueKind.Number && maxLength.TryGetInt32(out var length)
                    ? length
                    : null));
        }
        return fields;
    }

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()!.Trim()
            : string.Empty;

    private static bool CSharpIdentifier(string value) =>
        char.IsLetter(value[0]) && value.All(char.IsLetterOrDigit);
}
