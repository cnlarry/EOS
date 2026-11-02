namespace EOS.API.Features.Assistant.Quality;

/// <summary>一个条款的计数。</summary>
public sealed record AssistantJudgeClauseStat(string Clause, string Title, int Pass, int Fail, int Na, int Unsure)
{
    /// <summary>该条款"判过（PASS+FAIL）"的条数；NA 与 UNSURE 都不进分母。</summary>
    public int Evaluated => Pass + Fail;

    /// <summary>该条款的通过率；一条都没判过时为 null（不是 0——0 表示"全错"）。</summary>
    public double? PassRate => Evaluated == 0 ? null : (double)Pass / Evaluated;
}

/// <summary>某个条款的原始计数（来自库里的 verdict 行，或内存中的判定结果）。</summary>
public sealed record AssistantJudgeClauseTally(string Clause, int Pass, int Fail, int Na, int Unsure);

/// <summary>
/// 回答质量的**四个数字**（ADR-044 §4.5）。定义写死在这里，避免各处口径漂移：
///
/// <list type="bullet">
/// <item>口径标注率 = A 的 PASS / (PASS + FAIL)（NA 不计）；</item>
/// <item>范围一致率 = B 的同上；</item>
/// <item>越界承诺数 = D 的 FAIL **绝对条数**（目标恒为 0）；</item>
/// <item>无法判定率 = 全部 UNSURE / (PASS + FAIL + UNSURE)，用于发现条款写不清。</item>
/// </list>
///
/// <para>
/// **两个入口、一份定义**：离线回放传内存里的判定结果（<see cref="From"/>），看板传库里的
/// 聚合计数（<see cref="FromTallies"/>）；两者都落到同一个核心，所以"回放说口径标注率 100%、
/// 看板说 90%"这种分叉不可能发生。
/// </para>
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
        var tallies = AssistantJudgeRubric.Current.Clauses
            .Select(clause =>
            {
                var verdicts = outcomes
                    .SelectMany(outcome => outcome.Verdicts)
                    .Where(verdict => string.Equals(verdict.Clause, clause.Code, StringComparison.Ordinal))
                    .Select(verdict => verdict.Verdict)
                    .ToList();

                return new AssistantJudgeClauseTally(
                    clause.Code,
                    verdicts.Count(verdict => verdict == AssistantJudgeVerdicts.Pass),
                    verdicts.Count(verdict => verdict == AssistantJudgeVerdicts.Fail),
                    verdicts.Count(verdict => verdict == AssistantJudgeVerdicts.NotApplicable),
                    verdicts.Count(verdict => verdict == AssistantJudgeVerdicts.Unsure));
            })
            .ToList();

        return FromTallies(outcomes.Count, tallies);
    }

    /// <summary>
    /// 从**已汇总的条款计数**算四个数字。
    ///
    /// <para>
    /// 条款清单以 rubric 为准：库里出现了当前 rubric 之外的条款编号时不进这四个数字
    /// （那是历史版本的判定，混进来会让"这一版条款准不准"说不清）；当前 rubric 里一条都没有的
    /// 条款按 0 计并在看板上显示为"未判"。
    /// </para>
    /// </summary>
    public static AssistantJudgeMetrics FromTallies(
        int judged, IReadOnlyList<AssistantJudgeClauseTally> tallies)
    {
        var byClause = new Dictionary<string, AssistantJudgeClauseTally>(StringComparer.Ordinal);
        foreach (var tally in tallies) byClause[tally.Clause] = tally;

        var stats = new List<AssistantJudgeClauseStat>(AssistantJudgeRubric.Current.Clauses.Count);
        foreach (var clause in AssistantJudgeRubric.Current.Clauses)
        {
            var tally = byClause.TryGetValue(clause.Code, out var found)
                ? found
                : new AssistantJudgeClauseTally(clause.Code, 0, 0, 0, 0);

            stats.Add(new AssistantJudgeClauseStat(
                clause.Code, clause.Title, tally.Pass, tally.Fail, tally.Na, tally.Unsure));
        }

        double? RateOf(string code)
        {
            var stat = stats.FirstOrDefault(item => string.Equals(item.Clause, code, StringComparison.Ordinal));
            return stat?.PassRate;
        }

        var total = stats.Sum(item => item.Pass + item.Fail + item.Unsure);
        var unsure = stats.Sum(item => item.Unsure);

        return new AssistantJudgeMetrics(
            judged,
            stats,
            RateOf("A"),
            RateOf("B"),
            stats.FirstOrDefault(item => string.Equals(item.Clause, "D", StringComparison.Ordinal))?.Fail ?? 0,
            total == 0 ? null : (double)unsure / total);
    }
}
