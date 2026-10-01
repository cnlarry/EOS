using EOS.API.Features.Assistant.Actions;

namespace EOS.API.Features.Assistant.Parameters;

/// <summary>
/// 记录动作族 ↔ 能力面参数键的**机械映射**（ADR-030 §5.3.4）。
///
/// <para>
/// 与工具开关同一套路数：键名由动作名生成（`ACTION_` + 动作名转大写），动作名本身引用
/// <see cref="AssistantRecordActionNames"/> 的常量——**不写第二遍字面量**。
/// 动作名改了，键名跟着改，不会留下"键还在、动作已经改名"的悬空参数。
/// </para>
///
/// <para>
/// 只有新增 / 修改 / 删除三个。批核族与权限授予类**没有成员**，因此也不可能有开关
/// （结构断言见 <c>NoApprovalEndpointCallTests</c>）——"不可配"在这里是编译期事实，不是约定。
/// </para>
/// </summary>
public static class AssistantActionKeys
{
    /// <summary>动作名（引用实现侧常量，不另抄一份）。</summary>
    public static IReadOnlyList<string> ActionNames { get; } =
    [
        AssistantRecordActionNames.Insert,
        AssistantRecordActionNames.Update,
        AssistantRecordActionNames.Delete,
    ];

    /// <summary>动作族的默认状态：可用（关掉才落非默认值，与工具开关同口径）。</summary>
    public const bool EnabledByDefault = true;

    /// <summary>按动作名生成参数键。</summary>
    public static string ParameterKeyOf(string actionName) =>
        "ACTION_" + actionName.Trim().ToUpperInvariant();

    /// <summary>反向：参数键 → 动作名。不是动作族开关的键返回 false。</summary>
    public static bool TryGetActionName(string? parameterKey, out string actionName)
    {
        actionName = string.Empty;
        var key = parameterKey?.Trim();
        if (string.IsNullOrEmpty(key) || !key.StartsWith("ACTION_", StringComparison.OrdinalIgnoreCase)) return false;

        var wanted = key["ACTION_".Length..];
        foreach (var name in ActionNames)
        {
            // CONFIG_WRITE_* 也以 ACTION_ 开头？不——它前缀不同，这里只认 ACTION_ 之后**恰为动作名**的键，
            // 所以 "ACTION_MAX_ROWS" 这类阈值键不会被误认成动作族开关
            if (string.Equals(name, wanted, StringComparison.OrdinalIgnoreCase))
            {
                actionName = name;
                return true;
            }
        }

        return false;
    }
}
