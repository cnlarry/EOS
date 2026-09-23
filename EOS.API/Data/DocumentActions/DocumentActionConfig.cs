using System.Text.Json;

namespace EOS.API.Data.DocumentActions;

/// <summary>
/// One configured document action: a MODULE_BUSINESS_ACTION row with EVENT_CODE='MANUAL'. Such a row
/// is never part of the effect chain — it describes a button (key, label, confirm, fail mode,
/// condition, input parameters) that only the user can fire.
/// </summary>
public sealed record DocumentActionConfig(
    string Key,
    string Label,
    bool ConfirmTag,
    string FailMode,
    int Seq,
    JsonElement? Condition,
    JsonElement? Params);

/// <summary>
/// Reads the MANUAL rows out of a published definition's businessActions section. The snapshot is the
/// authority here: a button exists once its configuration has been published, which is the same
/// moment the definition version changes.
/// </summary>
public static class DocumentActionConfigs
{
    /// <summary>Event code reserved for user-triggered actions; no document event fires it.</summary>
    public const string ManualEventCode = "MANUAL";

    /// <summary>Enabled MANUAL rows, ordered by SEQ (the configured button order).</summary>
    public static IReadOnlyList<DocumentActionConfig> Parse(JsonElement? businessActions)
    {
        var result = new List<DocumentActionConfig>();
        if (businessActions is not { ValueKind: JsonValueKind.Array } actions)
        {
            return result;
        }
        foreach (var action in actions.EnumerateArray())
        {
            if (ParseOne(action) is { } config)
            {
                result.Add(config);
            }
        }
        return result.OrderBy(config => config.Seq).ToList();
    }

    /// <summary>The enabled MANUAL row of one action key, or null when the module does not offer it.</summary>
    public static DocumentActionConfig? Find(JsonElement? businessActions, string? actionKey)
    {
        if (string.IsNullOrWhiteSpace(actionKey))
        {
            return null;
        }
        var key = actionKey.Trim();
        return Parse(businessActions).FirstOrDefault(config => config.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
    }

    private static DocumentActionConfig? ParseOne(JsonElement action)
    {
        if (action.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        if (!String(action, "eventCode").Equals(ManualEventCode, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        if (Bool(action, "enabled") == false)
        {
            return null;
        }
        var key = String(action, "effectKey");
        if (key.Length == 0)
        {
            return null;
        }
        // Label precedence: the row's own LABEL, then the effect name carried by the configuration,
        // then the handler's default. The endpoint keeps the last fallback when the row is silent.
        var label = String(action, "label");
        if (label.Length == 0)
        {
            label = String(action, "effectName");
        }
        var failMode = String(action, "failMode");
        return new DocumentActionConfig(
            key,
            label,
            Bool(action, "confirmTag") ?? false,
            failMode.Length == 0 ? "BLOCK" : failMode,
            Int(action, "seq") ?? 0,
            Embedded(action, "condition"),
            Embedded(action, "params"));
    }

    /// <summary>
    /// Reads a structured column: published snapshots carry PARAM_STRUCT/CONDITION_STRUCT as JSON text,
    /// the workspace model may hand them over as inline objects. Both shapes mean the same thing here.
    /// </summary>
    private static JsonElement? Embedded(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }
        if (value.ValueKind != JsonValueKind.String)
        {
            return value.Clone();
        }
        var text = value.GetString()?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }
        try
        {
            return JsonDocument.Parse(text).RootElement.Clone();
        }
        catch (JsonException)
        {
            // A malformed structured column is a save-time/publish-time problem; at runtime it must not
            // take the endpoint down — the row is treated as having no condition/parameters.
            return null;
        }
    }

    private static string String(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()!.Trim()
            : string.Empty;

    private static bool? Bool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True ? true
        : element.TryGetProperty(name, out var falseValue) && falseValue.ValueKind == JsonValueKind.False ? false
        : null;

    private static int? Int(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var parsed)
            ? parsed
            : null;
}
