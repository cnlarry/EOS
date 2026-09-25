using System.Text.Json;
using System.Text.Json.Nodes;

namespace EOS.API.Data;

/// <summary>
/// Decides whether two module definitions say the same thing. Publishing ("content unchanged,
/// reuse the current version") and staleness detection must answer that question identically,
/// so both call here instead of each comparing the raw text.
///
/// The comparison is semantic, not byte-for-byte: differences the runtime provably ignores are
/// normalized away first, so tidying a configuration no longer costs the evidence recorded
/// against the previous version. Two kinds of difference are ignored today:
///
/// · placeholder formula rows (empty op code, target table and target field) — the loader drops
///   them before anything else looks at them, so a row that exists only to carry a service
///   effect's parameters never takes part in execution;
/// · an empty or whitespace-only string where the field is absent — the readers fold both into
///   "not set" for every field the runtime reads that way.
///
/// Everything else is a real difference. Normalizing more than the runtime ignores would let a
/// changed configuration reuse the old version: the evidence would still look fresh while it no
/// longer describes what the configuration does, which is worse than the current over-strictness.
/// </summary>
public static class WorkbenchDefinitionContentComparer
{
    /// <summary>Action fields whose empty spelling means the same as an absent one.</summary>
    private static readonly string[] EmptyMeansUnsetActionFields =
        ["condition", "params", "reverse", "effectName", "failMode"];

    /// <summary>Formula-row fields whose empty spelling means the same as an absent one.</summary>
    private static readonly string[] EmptyMeansUnsetOpFields =
        ["condition", "match", "sourceTerms", "sourceTable", "sourceField", "sourceAgg", "remark", "sourceScope"];

    /// <summary>Validation-rule fields whose empty spelling means the same as an absent one.</summary>
    private static readonly string[] EmptyMeansUnsetRuleFields = ["params", "message"];

    /// <summary>
    /// True when the two definitions are the same configuration. Unparsable content falls back to
    /// a text comparison and is treated as different unless it matches exactly: a comparison that
    /// cannot be made must never be reported as equivalent.
    /// </summary>
    public static bool AreEquivalent(string? stored, string? rebuilt)
    {
        if (string.Equals(stored, rebuilt, StringComparison.Ordinal))
        {
            return true;
        }
        if (stored is null || rebuilt is null)
        {
            return false;
        }
        return TryNormalize(stored, out var normalizedStored)
            && TryNormalize(rebuilt, out var normalizedRebuilt)
            && string.Equals(normalizedStored, normalizedRebuilt, StringComparison.Ordinal);
    }

    /// <summary>Canonical text used by <see cref="AreEquivalent"/>; false when the text is not a JSON object.</summary>
    public static bool TryNormalize(string definitionJson, out string normalized)
    {
        normalized = definitionJson;
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(definitionJson);
        }
        catch (JsonException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
        if (root is not JsonObject definition)
        {
            return false;
        }

        NormalizeActions(definition);
        NormalizeValidationRules(definition);
        normalized = definition.ToJsonString();
        return true;
    }

    private static void NormalizeActions(JsonObject definition)
    {
        if (definition["businessActions"] is not JsonArray actions)
        {
            return;
        }
        foreach (var element in actions)
        {
            if (element is not JsonObject action)
            {
                continue;
            }
            ApplyEmptyMeansUnset(action, EmptyMeansUnsetActionFields);
            NormalizeOps(action);
        }
    }

    private static void NormalizeOps(JsonObject action)
    {
        if (action["ops"] is not JsonArray ops)
        {
            return;
        }
        for (var index = ops.Count - 1; index >= 0; index--)
        {
            if (ops[index] is not JsonObject op)
            {
                continue;
            }
            if (ModuleBusinessConfigValidator.IsPlaceholderOp(
                    ReadString(op, "opCode"), ReadString(op, "targetTable"), ReadString(op, "targetField")))
            {
                ops.RemoveAt(index);
                continue;
            }
            ApplyEmptyMeansUnset(op, EmptyMeansUnsetOpFields);
            NormalizeConstant(op);
        }
    }

    /// <summary>
    /// The empty string is a real value for a CONSTANT source ("clear this column"), and the
    /// loader reads that field without folding empty into unset, so the two spellings are only
    /// interchangeable for the other source scopes.
    /// </summary>
    private static void NormalizeConstant(JsonObject op)
    {
        if (string.Equals(ReadString(op, "sourceScope")?.Trim(), "CONSTANT", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        if (op["sourceConstant"] is JsonValue constant
            && constant.GetValueKind() == JsonValueKind.String
            && string.IsNullOrWhiteSpace(constant.GetValue<string>()))
        {
            op["sourceConstant"] = null;
        }
    }

    private static void NormalizeValidationRules(JsonObject definition)
    {
        if (definition["validationRules"] is not JsonArray rules)
        {
            return;
        }
        foreach (var element in rules)
        {
            if (element is JsonObject rule)
            {
                ApplyEmptyMeansUnset(rule, EmptyMeansUnsetRuleFields);
            }
        }
    }

    private static void ApplyEmptyMeansUnset(JsonObject target, IReadOnlyList<string> names)
    {
        foreach (var name in names)
        {
            if (target[name] is JsonValue value
                && value.GetValueKind() == JsonValueKind.String
                && string.IsNullOrWhiteSpace(value.GetValue<string>()))
            {
                target[name] = null;
            }
        }
    }

    /// <summary>String value of a property, or null when it is absent or not a string.</summary>
    private static string? ReadString(JsonObject target, string name) =>
        target[name] is JsonValue value && value.GetValueKind() == JsonValueKind.String
            ? value.GetValue<string>()
            : null;
}
