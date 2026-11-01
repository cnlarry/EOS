namespace EOS.API.Features.Assistant.Quality;

/// <summary>一个条款的计数。</summary>
public sealed record AssistantJudgeClauseStat(string Clause, string Title, int Pass, int Fail, int Na, int Unsure)
{
    /// <summary>该条款"判过（PASS+FAIL）"的条数；NA 与 UNSURE 都不进分母。</summary>
    public int Evaluated => Pass + Fail;

    /// <summary>该条款的通过率；一条都没判过时为 null（不是 0——0 表示"全错"）。</summary>
    public double? PassRate => Evaluated == 0 ? null : (double)Pass / Evaluated;
}

/// <summary>
/// 回答质量的**四个数字**（ADR-044 §4.5）。定义写死在这里，避免各处口径漂移：
///
/// <list type="bullet">
/// <item>口径标注率 = A 的 PASS / (PASS + FAIL)（NA 不计）；</item>
/// <item>范围一致率 = B 的同上；</item>
/// <item>越界承诺数 = D 的 FAIL **绝对条数**（目标恒为 0）；</item>
/// <item>无法判定率 = 全部 UNSURE / (PASS + FAIL + UNSURE)，用于发现条款写不清。</item>
/// </list>
/// </summary>
public sealed record AssistantJudgeMetrics(
    int Judged,
    IReadOnlyList<AssistantJudgeClauseStat> Clauses,
    double? CitationRate,
    double? ScopeRate,
    int OverreachCount,
    double? UnsureRate)
{
    /// <summary>从若干次判定结果汇总。只读聚合，不做任何"折算"。</summary>
    public static AssistantJudgeMetrics From(IReadOnlyList<AssistantJudgeOutcome> outcomes)
    {
        var codes = AssistantJudgeRubric.Current.Clauses;
        var stats = new List<AssistantJudgeClauseStat>(codes.Count);
        foreach (var clause in codes)
        {
            var verdicts = outcomes
                .SelectMany(outcome => outcome.Verdicts)
                .Where(verdict => string.Equals(verdict.Clause, clause.Code, StringComparison.Ordinal))
                .Select(verdict => verdict.Verdict)
                .ToList();

            stats.Add(new AssistantJudgeClauseStat(
                clause.Code,
                clause.Title,
                verdicts.Count(verdict => verdict == AssistantJudgeVerdicts.Pass),
                verdicts.Count(verdict => verdict == AssistantJudgeVerdicts.Fail),
                verdicts.Count(verdict => verdict == AssistantJudgeVerdicts.NotApplicable),
                verdicts.Count(verdict => verdict == AssistantJudgeVerdicts.Unsure)));
        }

        double? RateOf(string code)
        {
            var stat = stats.FirstOrDefault(item => string.Equals(item.Clause, code, StringComparison.Ordinal));
            return stat?.PassRate;
        }

        var total = stats.Sum(item => item.Pass + item.Fail + item.Unsure);
        var unsure = stats.Sum(item => item.Unsure);

        return new AssistantJudgeMetrics(
            outcomes.Count,
            stats,
            RateOf("A"),
            RateOf("B"),
            stats.FirstOrDefault(item => string.Equals(item.Clause, "D", StringComparison.Ordinal))?.Fail ?? 0,
            total == 0 ? null : (double)unsure / total);
    }
}
