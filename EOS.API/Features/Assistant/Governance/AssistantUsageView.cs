using EOS.API.Data;
using EOS.API.Data.Assistant;

namespace EOS.API.Features.Assistant.Governance;

/// <summary>按模型的一行用量（管理面「模型与用量」与个人面「我的用量」共用同一形状）。</summary>
public sealed record AssistantUsageModelRow(
    string ModelName,
    int Requests,
    long PromptTokens,
    long CompletionTokens,
    double EstimatedCostYuan,
    DateTimeOffset LastUsedAt);

/// <summary>按 UTC 自然日的一行用量。</summary>
public sealed record AssistantUsageTrendRow(
    DateTime Day,
    int Requests,
    long PromptTokens,
    long CompletionTokens,
    double EstimatedCostYuan);

/// <summary>
/// 用量看板的两个投影：把仓库层的聚合结果折算成界面要的形状（含**成本**）。
///
/// <para>
/// 为什么要单独一处：管理面（全体）与个人面（只看自己）取数范围不同，但**折算口径必须同一份**——
/// 按模型的单价找不回时退全局兜底价、趋势按天只能按兜底价（一天里可能跑过好几个模型）。
/// 分成两处写，迟早出现"我的页面说 3 块、管理页说 5 块"，而两边看上去都对，
/// 于是没人说得清该信哪个。成本口径还要与台账实际扣的钱一致（ADR-030 §6）。
/// </para>
/// </summary>
public static class AssistantUsageView
{
    /// <summary>
    /// 按模型折算。落库的 <c>MODEL_NAME</c> 就是模型标识，所以能按它找回那一行的单价；
    /// 找不回（模型已被删 / 改过名）就用全局兜底价——不能让成本显示成 0，否则"限额到底还有多少"
    /// 会被读成"一分钱没花"。
    /// </summary>
    public static IReadOnlyList<AssistantUsageModelRow> ByModel(
        IReadOnlyList<ModelUsage> rows,
        IReadOnlyDictionary<string, AssistantModelRow> priceByCode,
        AssistantCostOptions cost)
    {
        var result = new List<AssistantUsageModelRow>(rows.Count);
        foreach (var entry in rows)
        {
            priceByCode.TryGetValue(entry.ModelName, out var price);
            result.Add(new AssistantUsageModelRow(
                entry.ModelName,
                entry.Usage.Requests,
                entry.Usage.PromptTokens,
                entry.Usage.CompletionTokens,
                Math.Round(AssistantCost.Calculate(
                    entry.Usage.PromptTokens, entry.Usage.CompletionTokens, cost,
                    price?.InputPerMillionYuan, price?.OutputPerMillionYuan), 4),
                entry.LastUsedAt));
        }

        return result;
    }

    /// <summary>
    /// 按天折算。**只能**用全局兜底价：一行是一整天，而一天里可能跑过好几个不同单价的模型，
    /// 按某一行的模型单价算会把它算成那个模型的价格。
    /// </summary>
    public static IReadOnlyList<AssistantUsageTrendRow> ByDay(
        IReadOnlyList<DayUsage> rows, AssistantCostOptions cost)
    {
        var result = new List<AssistantUsageTrendRow>(rows.Count);
        foreach (var entry in rows)
        {
            result.Add(new AssistantUsageTrendRow(
                entry.Day,
                entry.Usage.Requests,
                entry.Usage.PromptTokens,
                entry.Usage.CompletionTokens,
                Math.Round(AssistantCost.Calculate(
                    entry.Usage.PromptTokens, entry.Usage.CompletionTokens, cost), 4)));
        }

        return result;
    }
}
