using System.Text.Json;
using EOS.API.Data;
using EOS.API.Data.Effects;
using EOS.API.Models;

namespace EOS.API.Features.Assistant.Config;

/// <summary>一条可核对的不一致项：同一输入两次结论一致（纯函数产出，不含时间与随机）。</summary>
public sealed record ConfigFinding(
    string Code,
    string Surface,
    string Target,
    string Message,
    string Source,
    string NextStep);

/// <summary>动作行的只读事实（来自 MODULE_BUSINESS_ACTION 与 _OP 的既有读取入口，不另建事实源）。</summary>
public sealed record ConfigActionFact(
    int Seq,
    string EventCode,
    string EffectKey,
    bool Enabled,
    string? Reverse,
    IReadOnlyList<BusinessActionOpDto> Ops);

/// <summary>效果键在全库的使用次数（分布口径与效果说明门禁一致：只算非用户点击行）。</summary>
public sealed record ConfigEffectKeyUsage(string EffectKey, int Count);

public sealed record ConfigConsistencyFacts(
    int ModuleId,
    IReadOnlyList<ConfigActionFact> Actions,
    IReadOnlyList<ConfigEffectKeyUsage> EffectKeyUsage);

/// <summary>
/// 配置面**可自动检出**的三类不一致（判据来自既有真源，本类不新增第二份目录）：
/// <list type="number">
/// <item>反向 <c>note</c> 里写了自由文本——引擎只读 <c>kind</c>，那段说明不会被读取；</item>
/// <item>反向 <c>kind</c> 名义闭集 ≠ 执行闭集——配错会在真单据解批那一刻抛错（发布门同一口径）；</item>
/// <item>效果键单例（全库只用过一次）——先看能否复用同类键，而不是新配一套。</item>
/// </list>
/// </summary>
public static class ConfigConsistencyRules
{
    public const string ReverseNoteNotRead = "reverse-note-not-read";
    public const string ReverseKindUnsupported = "reverse-kind-unsupported";
    public const string EffectKeySingleton = "effect-key-singleton";

    /// <summary>效果面的事实来源标注（与 2301 编辑器同一批读取入口）。</summary>
    private const string Source = "MODULE_BUSINESS_ACTION(_OP) + EffectStructSchemas/EffectReverseCompatibility";

    public static IReadOnlyList<ConfigFinding> Check(ConfigConsistencyFacts facts)
    {
        var findings = new List<ConfigFinding>();
        foreach (var action in facts.Actions)
        {
            var location = $"SEQ={action.Seq} {action.EffectKey}";
            var note = ReadReverseNote(action.Reverse);
            if (note is not null)
            {
                findings.Add(new ConfigFinding(
                    ReverseNoteNotRead,
                    "effect",
                    location,
                    $"反向说明里写了「{note}」：引擎只读 kind，这段说明不会被任何代码读取。",
                    Source,
                    "把真实意图写进受支持的 kind（见反向 kind 说明）；说明性文字请放到备注（REMARK），那里是给人看的。"));
            }

            var kind = ReadReverseKind(action.Reverse);
            if (kind is null || BusinessActionCatalog.IsManualEvent(action.EventCode)) continue;
            var hasFormulaRows = ModuleBusinessConfigValidator.HasFormulaRows(action.Ops);
            if (EffectReverseCompatibility.IsSupported(action.EffectKey, kind, hasFormulaRows)) continue;
            findings.Add(new ConfigFinding(
                ReverseKindUnsupported,
                "effect",
                location,
                $"反向 kind={kind} 不受该效果键支持：真单据解批时会抛错"
                + $"（该键可用：{string.Join("/", EffectReverseCompatibility.AllowedKinds(action.EffectKey, hasFormulaRows))}）。",
                Source + "（发布门 effect_reverse_kind_supported 同一口径）",
                "改用该键支持的反向 kind，或换一个语义匹配的效果键。"));
        }

        findings.AddRange(SingletonFindings(facts));
        return findings;
    }

    /// <summary>单例键只在"本模块真的用了它"时才报——全库统计要落到具体模块才可操作。</summary>
    private static IEnumerable<ConfigFinding> SingletonFindings(ConfigConsistencyFacts facts)
    {
        var usedByModule = facts.Actions
            .Select(action => action.EffectKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var inUse = facts.EffectKeyUsage.Count;
        foreach (var usage in facts.EffectKeyUsage)
        {
            if (usage.Count != 1 || !usedByModule.Contains(usage.EffectKey)) continue;
            yield return new ConfigFinding(
                EffectKeySingleton,
                "effect",
                usage.EffectKey,
                $"效果键「{usage.EffectKey}」全库只用过 1 次（在用键 {inUse} 个）；"
                + "同一件事配出多个只用一次的效果键，是最难维护的一类配置。",
                "MODULE_BUSINESS_ACTION.EFFECT_KEY 全库分布",
                "先用 /schemas 的效果键目录核对既有键能否表达同一件事；确需新键时按目录评审登记。");
        }
    }

    /// <summary>读反向结构里的 <c>note</c>：写在这里的自由文本没有任何代码读取（见判决说明）。</summary>
    internal static string? ReadReverseNote(string? reverse)
    {
        if (ReadReverse(reverse) is not { } element) return null;
        return element.TryGetProperty("note", out var note) && note.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(note.GetString())
                ? note.GetString()!.Trim()
                : null;
    }

    internal static string? ReadReverseKind(string? reverse)
    {
        if (ReadReverse(reverse) is not { } element) return null;
        return element.TryGetProperty("kind", out var kind) && kind.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(kind.GetString())
                ? kind.GetString()!.Trim()
                : null;
    }

    /// <summary>REVERSE_STRUCT 的形态容错：对象直接用；字符串按 JSON 解析（快照与库内都可能给字符串）。</summary>
    private static JsonElement? ReadReverse(string? reverse)
    {
        if (string.IsNullOrWhiteSpace(reverse)) return null;
        try
        {
            var element = JsonDocument.Parse(reverse).RootElement;
            return element.ValueKind == JsonValueKind.Object ? element.Clone() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
