using EOS.API.Data;

namespace EOS.API.Features.Assistant.ModelAccess;

/// <summary>
/// 助手**全局策略**参数的键与元数据，以及"把库里的覆盖值应用到代码默认值上"的唯一入口。
///
/// <para>
/// 为什么要有这么一个地方：界面上要展示"默认值是多少"、要按类型校验输入、要把值解析回来——
/// 这三件事如果各写一份，默认值就会漂移（界面显示 5 元、代码里其实已经改成 8 元）。
/// 所以默认值直接取 <c>new AssistantSettings()</c>，元数据只有这一份。
/// </para>
///
/// <para>
/// **缺行 = 用代码默认值**（不是"用 0"）。这样"恢复默认"就是一个删除动作，而且升级调整默认值时，
/// 没被管理员改过的参数会自动跟着走——改过的那些必须留住，没改过的不该被旧值钉死。
/// </para>
/// </summary>
public static class AssistantSettingKeys
{
    public const string SystemPrompt = "SystemPrompt";
    public const string EnableAutoDistill = "EnableAutoDistill";
    public const string GlobalDailyCapYuan = "GlobalDailyCapYuan";
    public const string UserDailyCapYuan = "UserDailyCapYuan";
    public const string MaxConsecutiveFailures = "MaxConsecutiveFailures";
    public const string CooldownSeconds = "CooldownSeconds";
    public const string ReserveMicroYuanPerRequest = "ReserveMicroYuanPerRequest";
    public const string InputPerMillionYuan = "InputPerMillionYuan";
    public const string OutputPerMillionYuan = "OutputPerMillionYuan";

    private static readonly AssistantSettings Defaults = new();

    /// <summary>参数元数据（顺序即界面顺序）。<c>DefaultText</c> 直接来自代码默认值，不会漂移。</summary>
    public static IReadOnlyList<AssistantSettingDescriptor> All { get; } =
    [
        new(SystemPrompt, "系统提示词", "string", null,
            "每轮对话注入的指令。**输出格式那一段是与前端渲染面的契约**（回答按 Markdown 渲染），改之前先看手册 60 篇。",
            Defaults.SystemPrompt),
        new(EnableAutoDistill, "会话结束自动提炼记忆", "bool", null,
            "done 之后异步提炼候选记忆（待用户确认）。关掉可以省一次模型调用。",
            Defaults.EnableAutoDistill ? "true" : "false"),
        new(GlobalDailyCapYuan, "全局日上限（元）", "decimal", "元",
            "全体用户当日合计上限，超过即拒绝新请求。必须大于 0——设成 0 会让所有人立刻被拒。",
            Defaults.Cost.GlobalDailyCapYuan.ToString("0.####")),
        new(UserDailyCapYuan, "每人日上限（元）", "decimal", "元",
            "单个用户当日上限。必须大于 0。", Defaults.Cost.UserDailyCapYuan.ToString("0.####")),
        new(MaxConsecutiveFailures, "连续失败熔断阈值（次）", "int", "次",
            "同一用户连续技术失败达到这个次数即冷却；成功一次即清零。权限拒绝与用户取消不计入。",
            Defaults.Cost.MaxConsecutiveFailures.ToString()),
        new(CooldownSeconds, "熔断冷却（秒）", "int", "秒",
            "触发熔断后的冷却时长。", Defaults.Cost.CooldownSeconds.ToString()),
        new(ReserveMicroYuanPerRequest, "每轮预留额（微元）", "long", "微元",
            "单轮模型调用的预留额，实际预留 = 本值 ×（工具轮上限 + 1）。1 元 = 1000000 微元。",
            Defaults.Cost.ReserveMicroYuanPerRequest.ToString()),
        new(InputPerMillionYuan, "输入单价兜底（元/百万 token）", "decimal", "元",
            "**模型行没填单价时**用它。必须大于 0——0 元会让日上限永远不触发。",
            Defaults.Cost.InputPerMillionYuan.ToString("0.####")),
        new(OutputPerMillionYuan, "输出单价兜底（元/百万 token）", "decimal", "元",
            "同上，作用于输出 token。", Defaults.Cost.OutputPerMillionYuan.ToString("0.####")),
    ];

    /// <summary>按 Key 找元数据；找不到返回 null（库里出现了界面不认识的键）。</summary>
    public static AssistantSettingDescriptor? Find(string? key) =>
        string.IsNullOrWhiteSpace(key)
            ? null
            : All.FirstOrDefault(item => string.Equals(item.Key, key.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 把库里的覆盖值应用到**代码默认值**上，返回结果与"解析不了的值"清单。
    ///
    /// <para>
    /// 解析失败（或值越界）时**跳过该键并记进 <c>Problems</c>**，绝不静默用默认值冒充成功：
    /// 一个写坏的日上限如果被悄悄忽略，表现是"界面显示 5 元、实际按默认值跑"，而没有任何线索。
    /// 调用方（注册表）会把 Problems 记进日志。
    /// </para>
    /// </summary>
    public static (AssistantSettings Settings, IReadOnlyList<string> Problems) Apply(
        IReadOnlyList<AssistantSettingRow> rows)
    {
        var settings = new AssistantSettings();
        var problems = new List<string>();

        foreach (var row in rows)
        {
            var descriptor = Find(row.ParamKey);
            if (descriptor is null)
            {
                // 库里留着界面已不认识的键（参数下线了）——提示但不报错，删掉即可
                problems.Add($"参数 {row.ParamKey} 已不在参数表里，被忽略（可在设置页删除）。");
                continue;
            }

            var text = row.ParamValue?.Trim() ?? string.Empty;
            if (text.Length == 0)
            {
                // 空值按"未设置"处理，等于用默认值；比写空串报错更实用（界面上清空就是恢复默认）
                continue;
            }

            if (!TryApply(settings, descriptor.Key, text, out var problem))
            {
                problems.Add($"{descriptor.DisplayName}：{problem}（已忽略，仍用默认值 {descriptor.DefaultText}）");
            }
        }

        return (settings, problems);
    }

    private static bool TryApply(AssistantSettings settings, string key, string text, out string problem)
    {
        problem = string.Empty;
        switch (key)
        {
            case SystemPrompt:
                settings.SystemPrompt = text;
                return true;

            case EnableAutoDistill:
                if (!bool.TryParse(text, out var enabled))
                {
                    problem = $"“{text}”不是合法布尔值";
                    return false;
                }

                settings.EnableAutoDistill = enabled;
                return true;

            case GlobalDailyCapYuan:
                return TryPositiveDouble(text, v => settings.Cost.GlobalDailyCapYuan = v, "日上限必须大于 0", out problem);

            case UserDailyCapYuan:
                return TryPositiveDouble(text, v => settings.Cost.UserDailyCapYuan = v, "每人日上限必须大于 0", out problem);

            case InputPerMillionYuan:
                return TryPositiveDouble(text, v => settings.Cost.InputPerMillionYuan = v, "单价必须大于 0", out problem);

            case OutputPerMillionYuan:
                return TryPositiveDouble(text, v => settings.Cost.OutputPerMillionYuan = v, "单价必须大于 0", out problem);

            case MaxConsecutiveFailures:
                if (!int.TryParse(text, out var failures) || failures < 1 || failures > 100)
                {
                    problem = $"“{text}”不是 1–100 之间的整数";
                    return false;
                }

                settings.Cost.MaxConsecutiveFailures = failures;
                return true;

            case CooldownSeconds:
                if (!int.TryParse(text, out var cooldown) || cooldown < 0 || cooldown > 86400)
                {
                    problem = $"“{text}”不是 0–86400 之间的整数";
                    return false;
                }

                settings.Cost.CooldownSeconds = cooldown;
                return true;

            case ReserveMicroYuanPerRequest:
                if (!long.TryParse(text, out var reserve) || reserve < 0)
                {
                    problem = $"“{text}”不是非负整数";
                    return false;
                }

                settings.Cost.ReserveMicroYuanPerRequest = reserve;
                return true;

            default:
                problem = "没有对应的应用逻辑";
                return false;
        }
    }

    private static bool TryPositiveDouble(string text, Action<double> assign, string requirement, out string problem)
    {
        problem = string.Empty;
        if (!double.TryParse(text, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var value) || value <= 0)
        {
            problem = $"“{text}”不是大于 0 的数字（{requirement}）";
            return false;
        }

        assign(value);
        return true;
    }
}

/// <summary>一个可配置参数的元数据。默认值来自代码，界面据此提示"留空/删除即回到这个值"。</summary>
public sealed record AssistantSettingDescriptor(
    string Key,
    string DisplayName,
    string ValueType,
    string? Unit,
    string Description,
    string DefaultText);

/// <summary>可被"从设置页写回"的参数键集合（控制器据此拒绝不认识的键）。</summary>
public static class AssistantSettingKeysExtensions
{
    public static bool IsWritable(this string key) => AssistantSettingKeys.Find(key) is not null;
}
