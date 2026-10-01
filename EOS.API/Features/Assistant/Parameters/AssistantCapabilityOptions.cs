namespace EOS.API.Features.Assistant.Parameters;

/// <summary>
/// 能力面参数（域 `CAPABILITY`）：**哪些工具被关掉了**。
///
/// <para>
/// 存"关掉了哪些"而不是"开着哪些"：工具默认全开，参数只在**关**的时候落一条非默认值，
/// 于是"没配过的工具"天然是开的，每加一个工具也不必补一行——这与"目录按批生长"是同一件事的两面。
/// </para>
///
/// <para>
/// 关掉的语义是**双向**的：既不发工具声明给模型（模型看不到它），也不接受模型对它的调用
/// （模型幻觉出一个已关闭的工具名时，明确拒绝而不是当成"工具不存在"）。
/// </para>
/// </summary>
public sealed class AssistantCapabilityOptions
{
    /// <summary>被关掉的工具名（与 <see cref="AssistantToolKeys.ToolNames"/> 同名）。</summary>
    public ISet<string> DisabledTools { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>被关掉的记录动作族（与 <see cref="AssistantActionKeys.ActionNames"/> 同名）。</summary>
    public ISet<string> DisabledActions { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>这个工具此刻可用吗（没被关 = 可用）。</summary>
    public bool IsToolEnabled(string toolName) => !DisabledTools.Contains(toolName);

    /// <summary>这个记录动作族此刻可用吗。</summary>
    public bool IsActionEnabled(string actionName) => !DisabledActions.Contains(actionName);

    /// <summary>
    /// 这段行为规则该不该注入：**它点名的工具必须全都在**（ADR-030 §7.4）。
    ///
    /// <para>
    /// 少一个就整条不注入，而不是把规则改成"半条"——规则是一段话，删半句往往自相矛盾。
    /// 判定放在这里而不是散在提示词组装处，是为了让它可被单独断言：
    /// "关掉 enum_metrics 之后规则不再注入"这件事的判据只有一句，改不了口径。
    /// </para>
    /// </summary>
    public bool AllowsRule(params string[] requiredTools) => requiredTools.All(IsToolEnabled);
}
