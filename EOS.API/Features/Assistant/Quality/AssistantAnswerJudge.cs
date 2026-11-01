using System.Text.Json;
using System.Text.RegularExpressions;
using EOS.API.Features.Assistant.ModelAccess;

namespace EOS.API.Features.Assistant.Quality;

/// <summary>判定结论的四态（与 ADR-044 §2.3 逐字一致）。</summary>
public static class AssistantJudgeVerdicts
{
    public const string Pass = "PASS";
    public const string Fail = "FAIL";
    public const string NotApplicable = "NA";
    public const string Unsure = "UNSURE";

    public static bool IsKnown(string? value) =>
        value is Pass or Fail or NotApplicable or Unsure;
}

/// <summary>一条条款的判定结果。</summary>
public sealed record AssistantJudgeVerdict(
    string Clause,
    string Verdict,
    string? Evidence,
    string? Note);

/// <summary>一次判定的结果：条款版本、判官模型、是否回落，以及逐条结论。</summary>
public sealed record AssistantJudgeOutcome(
    long MessageId,
    string RubricVersion,
    string JudgeModel,
    bool FellBackToChat,
    IReadOnlyList<AssistantJudgeVerdict> Verdicts);

/// <summary>
/// 判官：把"问句 + 回答 + 工具摘要"交给模型，按条款逐条取四态结论。
///
/// <para>
/// **不碰数据库、不做权限判定、不算数值**（ADR-044 §3）：输入是已经取好的转录，输出是结构化结论，
/// 其余由调用方负责。这样将来若把"打分运行"上移到 Harness，搬的是这个类，不是重写。
/// </para>
/// </summary>
public sealed class AssistantAnswerJudge(IAssistantJudgeModel model)
{
    public async Task<AssistantJudgeOutcome> JudgeAsync(
        AssistantJudgeTranscript transcript, CancellationToken token)
    {
        var rubric = AssistantJudgeRubric.Current;
        var user = AssistantJudgePrompt.BuildUser(rubric, transcript);
        var raw = await model.CompleteAsync(AssistantJudgePrompt.System, user, token);

        return new AssistantJudgeOutcome(
            transcript.MessageId,
            rubric.Version,
            model.Descriptor,
            model.FellBackToChat,
            AssistantJudgeVerdictParser.Parse(raw, rubric));
    }
}

/// <summary>
/// 解析模型输出。**宽容解析、严格兜底**：模型可能加代码块、漏条款、给未知编号或空证据——
/// 这些都按 UNSURE 处理并写明原因，绝不放宽成 PASS（"拿不准"必须与"通过"分开统计）。
/// </summary>
public static class AssistantJudgeVerdictParser
{
    private static readonly Regex CodeFence = new(@"```(?:json)?\s*(?<body>[\s\S]*?)```", RegexOptions.Compiled);

    public static IReadOnlyList<AssistantJudgeVerdict> Parse(string? raw, AssistantJudgeRubric rubric)
    {
        var verdicts = new Dictionary<string, AssistantJudgeVerdict>(StringComparer.Ordinal);
        var json = ExtractJson(raw);
        if (json is not null)
        {
            try
            {
                using var document = JsonDocument.Parse(json);
                if (document.RootElement.TryGetProperty("verdicts", out var array)
                    && array.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in array.EnumerateArray())
                    {
                        var verdict = ReadVerdict(item);
                        if (verdict is not null) verdicts[verdict.Clause] = verdict;
                    }
                }
            }
            catch (JsonException)
            {
                // 落到下面的"全部 UNSURE"分支：解析失败不是"没问题"，而是"没判成"。
            }
        }

        var results = new List<AssistantJudgeVerdict>(rubric.Clauses.Count);
        foreach (var clause in rubric.Clauses)
        {
            if (!verdicts.TryGetValue(clause.Code, out var verdict))
            {
                results.Add(new AssistantJudgeVerdict(
                    clause.Code, AssistantJudgeVerdicts.Unsure, null, "模型未返回该条款的判定。"));
                continue;
            }

            // 证据规则是硬的：判 PASS/FAIL 却拿不出逐字引用 ⇒ 降为 UNSURE。
            // 这条把"凭印象判定"挡在统计之外，也让"无法判定率"成为一个真实信号。
            if (verdict.Verdict is AssistantJudgeVerdicts.Pass or AssistantJudgeVerdicts.Fail
                && string.IsNullOrWhiteSpace(verdict.Evidence))
            {
                results.Add(verdict with
                {
                    Verdict = AssistantJudgeVerdicts.Unsure,
                    Note = "缺少逐字证据，按 UNSURE 记。",
                });
                continue;
            }

            results.Add(verdict);
        }

        return results;
    }

    private static AssistantJudgeVerdict? ReadVerdict(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object) return null;

        var clause = Text(item, "clause");
        if (string.IsNullOrWhiteSpace(clause)) return null;

        var rawVerdict = Text(item, "verdict")?.Trim().ToUpperInvariant();
        var verdict = AssistantJudgeVerdicts.IsKnown(rawVerdict)
            ? rawVerdict!
            : AssistantJudgeVerdicts.Unsure;

        return new AssistantJudgeVerdict(
            clause.Trim().ToUpperInvariant(),
            verdict,
            Text(item, "evidence"),
            Text(item, "note"));
    }

    private static string? Text(JsonElement item, string property) =>
        item.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>容忍代码块与前后寒暄：取第一个 <c>{</c> 到最后一个 <c>}</c> 之间的内容。</summary>
    private static string? ExtractJson(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var fence = CodeFence.Match(raw);
        var body = fence.Success ? fence.Groups["body"].Value : raw;

        var start = body.IndexOf('{');
        var end = body.LastIndexOf('}');
        return start >= 0 && end > start ? body[start..(end + 1)] : null;
    }
}
