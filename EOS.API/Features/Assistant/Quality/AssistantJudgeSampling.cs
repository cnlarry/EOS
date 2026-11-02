using System.Text.RegularExpressions;
using EOS.API.Features.Assistant.Parameters;

namespace EOS.API.Features.Assistant.Quality;

/// <summary>
/// 抽样决策：**哪些回答值得判**（ADR-044 §4.4）。
///
/// <para>
/// 金额/数量/比率类回答全判——口径标注（A）与范围一致（B）只在这类回答上才有意义，
/// "9 月营业额答成全部时间"那个错也只会出现在这里；其余回答按比例抽。
/// </para>
///
/// <para>
/// 抽样是**确定性的**：按消息编号取模落桶，不用随机数。理由是可比性——随机抽样会让
/// "再跑一轮看看"的输出与上一轮不同，看板上的比率因此无法判断是产品变了还是抽样变了。
/// </para>
/// </summary>
public static class AssistantJudgeSampling
{
    /// <summary>取数类工具：它们的回答几乎必然带数字，按"金额/数量/比率类"处理。</summary>
    private static readonly string[] AmountTools = ["resolve_metric", "run_report"];

    private static readonly Regex AmountPattern = new(
        "金额|总额|合计|小计|营业额|销售额|应收|应付|毛利|税额|含税|未税|"
        + "数量|总数|库存|单价|成本|价格|占比|比例|百分比|百分之|\\d\\s*%",
        RegexOptions.Compiled);

    /// <summary>这条回答这次是否要判。</summary>
    public static bool ShouldJudge(AssistantJudgeTranscript transcript, AssistantJudgeLimitsOptions limits)
    {
        var rate = IsAmountRelated(transcript) ? limits.AmountSampleRatePct : limits.SampleRatePct;
        if (rate <= 0) return false;
        if (rate >= 100) return true;

        // 落桶用消息编号：同一条回答每次扫描都落在同一桶里，重复扫描不会改变样本集。
        return transcript.MessageId % 100 < rate;
    }

    /// <summary>
    /// 是否属于"金额/数量/比率类"：看工具（取数类一律算）与问句关键词。
    ///
    /// <para>
    /// 判定偏宽是有意的：多判一条只是多花一点钱，漏判却会让最该被盯住的回答落进 20% 的抽样外。
    /// </para>
    /// </summary>
    public static bool IsAmountRelated(AssistantJudgeTranscript transcript)
    {
        foreach (var call in transcript.ToolCalls)
        {
            if (AmountTools.Contains(call.Name, StringComparer.OrdinalIgnoreCase)) return true;
        }

        return AmountPattern.IsMatch(transcript.Question);
    }
}
