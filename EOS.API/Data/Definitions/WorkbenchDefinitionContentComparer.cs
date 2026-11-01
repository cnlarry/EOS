using System.Text.Json;
using System.Text.Json.Nodes;
using EOS.API.Data;

namespace EOS.API.Data.Definitions;

/// <summary>
/// Decides whether two module definitions say the same thing. Publishing ("content unchanged,
/// reuse the current version") and staleness detection must answer that question identically,
/// so both call here instead of each comparing the raw text.
///
/// The comparison is semantic, not byte-for-byte: differences the runtime provably ignores are
/// normalized away first, so tidying a configuration no longer costs the evidence recorded
/// against the previous version. Three kinds of difference are ignored today:
///
/// · placeholder formula rows (empty op code, target table and target field) — the loader drops
///   them before anything else looks at them, so a row that exists only to carry a service
///   effect's parameters never takes part in execution;
/// · an empty or whitespace-only string where the field is absent — the readers fold both into
///   "not set" for every field the runtime reads that way;
/// · the `effectEngine` section — ADR-035 retired the per-module engine switch, and nothing in the
///   runtime reads that section any more (takeover is derived from the actions themselves).
///   Leaving it in the comparison would make deleting a dead section look like a configuration
///   change: republishing would bump the version and invalidate the evidence recorded against it,
///   for no gain.
///
/// · the `width` of a field — column width is a per-user preference (SYS_USER_COLUMN.F_WIDTH) resolved on every
///   read, with the field metadata value acting only as the default, so it is not a fact about the
///   published definition (see <see cref="NormalizeFieldViews"/>). The same goes for the **order** of
///   the field arrays (that follows SYS_USER_COLUMN.F_IDX) and the `UserId` that built the content.
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
        NormalizeFieldViews(definition);
        // 谁点的发布不是配置事实：`UserId` 只是装配时的身份（发布人），运行期每次按当前
        // 用户重建字段视图。留着它，同一份配置换个账号发布就会被判成"内容变了"。
        definition.Remove("UserId");
        definition.Remove("userId");
        // `effectEngine` 段不再参与执行（ADR-035）：新发布的定义不再写出它，历史快照里可能还留着，
        // 两者的"内容"对运行期而言相同——故比较前直接摘掉，避免删段被当成配置变更而升版本。
        definition.Remove("effectEngine");
        // 同一个退役动作还带走了一个**漏进快照的计算属性** `EffectEngineEnabled`（只读 getter，没有
        // [JsonIgnore]，默认选项会把它序列化出来）。判据改推导后该属性被删除，历史快照里却仍有它——
        // 不摘掉的话，"全库快照集体落后"这个假警报会盖住真正的静默落后（实测 261/262 条）。
        definition.Remove("EffectEngineEnabled");
        definition.Remove("effectEngineEnabled");
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

    /// <summary>
    /// 字段视图段（`MasterFields` / `DetailFields`）里有两类"不是模块定义事实"的差异，必须归一掉：
    ///
    /// · **列宽**（`Width`）：它是个人层偏好（`SYS_USER_COLUMN.F_WIDTH`，字段元数据的
    ///   `DISPLAY_LENGTH` 只是默认初值），运行期每次按当前用户现算。历史实现写的是小写
    ///   `"width"`，而序列化出来的是 `"Width"`，等于**这条归一从来没生效过**——于是谁拖一下列宽，
    ///   已发布快照就被判落后（LESSONS L83 记的就是这个形状，但修的键名不匹配）。
    /// · **顺序**：它来自个人列序（`SYS_USER_COLUMN.F_IDX`），运行期按当前用户重排；
    ///   发布所得的定义里那一份顺序只反映"发布人当时怎么排的"，换个账号发布就会变。
    ///   不归一的话，发布与落后检测会因发布人不同而给出相反结论。
    ///
    /// 两者都不是配置事实，故按 `Key` 归一后比较。真正的事实（字段集、类型、只读、默认值、
    /// 选择器、必填）一个都不动——少归一一个，就会让"改了配置却复用旧版本"从另一头漏回来。
    /// </summary>
    private static void NormalizeFieldViews(JsonObject definition)
    {
        foreach (var section in new[] { "masterFields", "detailFields", "MasterFields", "DetailFields" })
        {
            if (definition[section] is not JsonArray fields)
            {
                continue;
            }
            var ordered = fields.OfType<JsonObject>()
                .OrderBy(field => ReadString(field, "Key") ?? ReadString(field, "key") ?? string.Empty,
                    StringComparer.Ordinal)
                .ToList();
            foreach (var field in ordered)
            {
                field.Remove("width");
                field.Remove("Width");
            }
            var canonical = ordered.Select(field => field.DeepClone()).ToList();
            fields.Clear();
            foreach (var field in canonical)
            {
                fields.Add(field);
            }
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
