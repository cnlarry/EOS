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

    /// <summary>
    /// 按**会话**折算金额（会话管理列表的「金额」列）。
    ///
    /// <para>
    /// 一个会话可能跑过多个模型，所以必须**先按模型分别计价、再相加**：拿会话总 token 乘某一个
    /// 模型的单价，得到的数既不是那个模型的钱，也不是这个会话的钱。单价找不回（模型已删 / 改过名）
    /// 时同样退回全局兜底价，理由与按模型折算一致——显示成 0 会把"花过钱"读成"没花钱"。
    /// </para>
    ///
    /// <para>
    /// 这是**估算**：单价取的是当前配置，而不是花钱那一刻的价格（历史价格没有留档）。
    /// 界面上因此以「估算金额」表头呈现，别让它看起来像一张账单。
    /// </para>
    /// </summary>
    public static IReadOnlyDictionary<long, double> PerSessionCost(
        IReadOnlyList<SessionModelUsage> rows,
        IReadOnlyDictionary<string, AssistantModelRow> priceByCode,
        AssistantCostOptions cost)
    {
        var result = new Dictionary<long, double>();
        foreach (var group in rows.GroupBy(row => row.SessionId))
        {
            var total = 0.0;
            foreach (var entry in group)
            {
                priceByCode.TryGetValue(entry.ModelName, out var price);
                total += AssistantCost.Calculate(
                    entry.PromptTokens, entry.CompletionTokens, cost,
                    price?.InputPerMillionYuan, price?.OutputPerMillionYuan);
            }

            result[group.Key] = Math.Round(total, 4);
        }

        return result;
    }
}
