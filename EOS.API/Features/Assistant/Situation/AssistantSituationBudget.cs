using Microsoft.Extensions.Options;

namespace EOS.API.Features.Assistant.Situation;

/// <summary>
/// 处境上下文的预算与上限：超限一律按这里的上限硬截断。
///
/// <para>
/// 取值来自 3105 的参数目录（<c>SIT_*</c> 键），**不再从配置节绑定**（ADR-030 §8）；
/// 这里的属性初始值就是"代码默认值"，也是目录里 <c>DEFAULT_VALUE</c> 的依据——默认值只有这一份。
/// </para>
/// </summary>
public sealed class AssistantSituationBudgetOptions
{
    /// <summary>常驻处境合计预算（token）。</summary>
    public int ResidentTokenLimit { get; set; } = 300;

    /// <summary>身份段预算（token）。</summary>
    public int IdentityTokenLimit { get; set; } = 200;

    /// <summary>待办段预算（token）。</summary>
    public int PendingTokenLimit { get; set; } = 80;

    /// <summary>上报筛选条件条数上限。</summary>
    public int MaxFilters { get; set; } = 10;

    /// <summary>上报选中行主键条数上限。</summary>
    public int MaxSelection { get; set; } = 20;

    /// <summary>上报脏字段条数上限。</summary>
    public int MaxDirtyFields { get; set; } = 20;

    /// <summary>上报单个值/主键的字符上限。</summary>
    public int MaxValueLength { get; set; } = 120;

    /// <summary>上报拒绝摘要的字符上限。</summary>
    public int MaxNoticeSummaryLength { get; set; } = 160;

    /// <summary>单据滞留判定天数（建立日期早于该天数仍未批核即视为滞留）。</summary>
    public int OverdueDays { get; set; } = 7;

    /// <summary>
    /// 滞留取数的**年龄上界**（天）：超过该年龄的历史单据不进摘要。
    /// 没有上界时，按模块默认排序取到的会是多年前的遗留单（实测取到 3120 天前、同类 43 条），
    /// 那类条目对"我手上压着什么"没有帮助。
    /// </summary>
    public int OverdueMaxAgeDays { get; set; } = 365;

    /// <summary>打开即见摘要的最大条目数。</summary>
    public int DigestMaxItems { get; set; } = 5;

    /// <summary>
    /// "此刻办不下去"逐单扫描的**每模块记录数上限**（按建立日期倒序取最近的）。
    /// 与 <see cref="BlockedNowMaxAgeDays"/> 一起把这条链路的总代价限住。
    /// </summary>
    public int BlockedNowScanRecords { get; set; } = 10;

    /// <summary>"此刻办不下去"扫描的年龄上界（天）：只看近期单据——既是"此刻"的语义，也把代价限住。</summary>
    public int BlockedNowMaxAgeDays { get; set; } = 30;

    /// <summary>"此刻办不下去"每模块最多判定的校验判据条数（逐单求值的成本上界）。</summary>
    public int BlockedNowProbeRules { get; set; } = 4;

    /// <summary>摘要扫描的候选模块数上限（按本人最近活动取候选）。</summary>
    public int DigestModuleScanLimit { get; set; } = 8;

    /// <summary>取"本人最近活动模块"的时间窗（天）。</summary>
    public int ActivityWindowDays { get; set; } = 30;

    /// <summary>取"最近被拒事件"的时间窗（天）与条数上限。</summary>
    public int RecentFailureDays { get; set; } = 7;

    /// <summary>最近被拒事件条数上限。</summary>
    public int RecentFailureLimit { get; set; } = 3;

    /// <summary>摘要条目文案的字符上限。</summary>
    public int DigestTextLength { get; set; } = 120;
}

/// <summary>
/// 处境上下文的预算执行器：统一"超限硬截断 + Warning 日志"的口径，避免静默丢内容。
/// token 估算沿用模型接入层的保守口径（1 字符 ≈ 1 token，只多不少）。
/// </summary>
public sealed class AssistantSituationBudget(
    IOptions<AssistantSituationBudgetOptions> options,
    ILogger<AssistantSituationBudget> logger)
{
    public AssistantSituationBudgetOptions Limits => options.Value;

    /// <summary>保守估算：中日韩字符约 1 token/字，按 1 字 = 1 token 计。</summary>
    public static int EstimateTokens(string? text) => text?.Length ?? 0;

    /// <summary>按条数上限截断，超限记 Warning。</summary>
    public IReadOnlyList<T> Take<T>(string section, IReadOnlyList<T>? items, int max)
    {
        if (items is null || items.Count == 0) return [];
        if (items.Count <= max) return items;
        logger.LogWarning("助手处境上报超出上限，已截断 section={Section} limit={Limit} actual={Actual}",
            section, max, items.Count);
        return [.. items.Take(max)];
    }

    /// <summary>按 token 预算硬截断文本，超限记 Warning。</summary>
    public string Truncate(string section, string? text, int tokenLimit)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        if (text.Length <= tokenLimit) return text;
        logger.LogWarning("助手处境文本超出预算，已硬截断 section={Section} limit={Limit} actual={Actual}",
            section, tokenLimit, text.Length);
        return text[..tokenLimit];
    }

    /// <summary>把多行合并到 token 预算内；放不下的行整行丢弃并记 Warning（不切半行）。</summary>
    public string JoinLines(string section, IReadOnlyList<string> lines, int tokenLimit)
    {
        var kept = new List<string>();
        var used = 0;
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var cost = line.Length + 1;
            if (used + cost > tokenLimit) break;
            kept.Add(line);
            used += cost;
        }

        if (kept.Count < lines.Count)
        {
            logger.LogWarning("助手处境文本超出预算，已丢弃尾部内容 section={Section} limit={Limit} kept={Kept} total={Total}",
                section, tokenLimit, kept.Count, lines.Count);
        }

        return string.Join(Environment.NewLine, kept);
    }

    /// <summary>按字符上限裁剪单行文本（用于摘要条目）。</summary>
    public string Clip(string? text, int maxLength)
    {
        var value = text?.Trim() ?? string.Empty;
        return value.Length <= maxLength ? value : value[..maxLength];
    }
}
