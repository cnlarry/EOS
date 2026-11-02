using EOS.API.Data.Assistant;
using EOS.API.Features.Assistant.Quality;

namespace EOS.API.Features.Assistant.Admin;

/// <summary>一个条款的一致率（只统计"人与判官都给了判断"的那些样本）。</summary>
public sealed record AssistantCalibrationClause(
    string Clause, string Title, int Compared, int Agreed, int Mismatched)
{
    public double? Rate => Compared == 0 ? null : (double)Agreed / Compared;
}

/// <summary>
/// 一条不一致：**两边的话都留着**。
///
/// <para>
/// 只报"不一致 3 条"没有可操作性——要改的是条款文本，而改文本必须看得见"人为什么这么判、判官引的又是哪一句"。
/// <paramref name="Dangerous"/> 特指"人判 FAIL、判官判 PASS"（漏报），它比"人判 PASS、判官判 FAIL"（误报）
/// 危险得多：漏报会把错误的回答留在库里当成没问题。
/// </para>
/// </summary>
public sealed record AssistantCalibrationMismatch(
    string CaseId,
    long MessageId,
    string Clause,
    string HumanVerdict,
    string? HumanEvidence,
    string JudgeVerdict,
    string? JudgeEvidence,
    string JudgeModel,
    bool Dangerous);

/// <summary>判官校准读数（ADR-044 §5 J2）：用户维护的样本 vs 库内判官结论。</summary>
/// <param name="Samples">样本总数（含草稿）。</param>
/// <param name="Reviewed">标记 `reviewed:true` 的样本数——只有这些进一致率。</param>
/// <param name="Compared">真正比过的样本数（有 messageId 且判官判过）。</param>
/// <param name="Uncompared">已审阅但比不了的样本数（缺 messageId / 判官还没判过）。</param>
/// <param name="AgreementRate">总体一致率（条款级汇总）；一条都没比过时为 null，不是 0。</param>
/// <param name="DangerousCount">"人判 FAIL、判官判 PASS"的条数（目标恒 0）。</param>
public sealed record AssistantCalibrationReport(
    int Samples,
    int Reviewed,
    int Compared,
    int Uncompared,
    IReadOnlyList<AssistantCalibrationClause> Clauses,
    double? AgreementRate,
    int DangerousCount,
    IReadOnlyList<AssistantCalibrationMismatch> Mismatches,
    string RubricVersion,
    IReadOnlyList<string> JudgeModels,
    IReadOnlyList<string> Notes);

/// <summary>
/// 把"样本标签"与"判官结论"对起来算一致率。**纯计算，不碰数据库、不调模型**。
///
/// <para>
/// 三条口径写死在这里，避免各处各算一遍：
/// <list type="bullet">
/// <item>只算 `reviewed:true` 的样本——草稿的标签还没定稿，拿它算等于把人的犹豫记成判官的错；</item>
/// <item>只算**当前条款版本**的判官结论（版本不同，比的不是同一把尺子）；同一回答同一条款有多份结论时
/// 取**最近**一份（换过模型时以最后一次为准，且把模型名列出来）；</item>
/// <item>条款一致 = 四态**完全相同**。`NA` 与 `UNSURE` 也算一种判断：人判 NA、判官判 PASS 就是不一致，
/// 不能因为"差不多"就放过——那正是条款没写清的地方。</item>
/// </list>
/// </para>
/// </summary>
public static class AssistantCalibrationView
{
    public static AssistantCalibrationReport Build(
        IReadOnlyList<AssistantCalibrationSample> samples,
        IReadOnlyList<AssistantCalibrationVerdict> verdicts)
    {
        var rubric = AssistantJudgeRubric.Current;

        // 同一 (回答, 条款) 只留**最近**一份结论：换过判官模型时以最后一次为准。
        // 不依赖调用方给的顺序——仓储的 ORDER BY 只是省事，口径不能建立在"顺序要对"上。
        var latest = new Dictionary<(long MessageId, string Clause), AssistantCalibrationVerdict>();
        foreach (var verdict in verdicts)
        {
            var key = (verdict.MessageId, verdict.Clause);
            if (!latest.TryGetValue(key, out var current) || verdict.JudgedAt >= current.JudgedAt)
            {
                latest[key] = verdict;
            }
        }

        var reviewed = samples.Where(sample => sample.Reviewed).ToList();
        var comparedSamples = 0;
        var uncomparedSamples = 0;
        var notes = new List<string>();

        // 一条都没审阅时说清"为什么是 0"：界面上的 0 与"判官全对"长得一样，必须区分。
        if (reviewed.Count == 0 && samples.Count > 0)
        {
            notes.Add($"库里 {samples.Count} 条校准样本都还没审阅（reviewed:false）："
                + "在 3106 复核后把 reviewed 改为 true 并补 reviewedBy/reviewedAt，它们才会进入一致率。");
        }

        var matches = new List<(AssistantCalibrationVerdict Judge, AssistantCalibrationLabel Human)>();
        var mismatches = new List<AssistantCalibrationMismatch>();

        foreach (var sample in reviewed)
        {
            if (sample.Problem is not null)
            {
                uncomparedSamples++;
                notes.Add($"{sample.CaseId}：{sample.Problem}，无法比对。");
                continue;
            }

            if (sample.MessageId is not { } messageId)
            {
                uncomparedSamples++;
                notes.Add($"{sample.CaseId}：没写 messageId（要对比的是库内哪一条回答），无法比对。");
                continue;
            }

            if (sample.Labels.Count == 0)
            {
                uncomparedSamples++;
                notes.Add($"{sample.CaseId}：没有解析到任何条款标签（labels 为空或格式不对），无法比对。");
                continue;
            }

            var judged = sample.Labels
                .Select(label => (label, verdict: latest.GetValueOrDefault((messageId, label.Clause))))
                .Where(pair => pair.verdict is not null)
                .ToList();

            if (judged.Count == 0)
            {
                uncomparedSamples++;
                notes.Add($"{sample.CaseId}：判官还没有对回答 {messageId} 给出当前条款版本的结论"
                    + "（若结论已存在，检查样本的条款编号是否属于当前版本），无法比对。");
                continue;
            }

            comparedSamples++;

            foreach (var (label, verdict) in judged)
            {
                if (string.Equals(label.Verdict, verdict!.Verdict, StringComparison.Ordinal))
                {
                    matches.Add((verdict, label));
                    continue;
                }

                mismatches.Add(new AssistantCalibrationMismatch(
                    sample.CaseId, messageId, label.Clause,
                    label.Verdict, label.Evidence,
                    verdict.Verdict, verdict.Evidence,
                    verdict.JudgeModel,
                    Dangerous: label.Verdict == AssistantJudgeVerdicts.Fail
                               && verdict.Verdict == AssistantJudgeVerdicts.Pass));
            }
        }

        var clauses = new List<AssistantCalibrationClause>(rubric.Clauses.Count);
        foreach (var clause in rubric.Clauses)
        {
            var agreed = matches.Count(pair => pair.Human.Clause == clause.Code);
            var mismatched = mismatches.Count(item => item.Clause == clause.Code);
            clauses.Add(new AssistantCalibrationClause(clause.Code, clause.Title, agreed + mismatched, agreed, mismatched));
        }

        var totalCompared = clauses.Sum(clause => clause.Compared);
        var totalAgreed = clauses.Sum(clause => clause.Agreed);

        return new AssistantCalibrationReport(
            samples.Count,
            reviewed.Count,
            comparedSamples,
            uncomparedSamples,
            clauses,
            totalCompared == 0 ? null : (double)totalAgreed / totalCompared,
            mismatches.Count(item => item.Dangerous),
            [.. mismatches
                .OrderByDescending(item => item.Dangerous)
                .ThenBy(item => item.CaseId, StringComparer.Ordinal)
                .ThenBy(item => item.Clause, StringComparer.Ordinal)],
            rubric.Version,
            [.. verdicts.Select(verdict => verdict.JudgeModel).Distinct(StringComparer.Ordinal).OrderBy(item => item, StringComparer.Ordinal)],
            notes);
    }
}
