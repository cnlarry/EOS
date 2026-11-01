using EOS.API.Features.Assistant.Governance;

namespace EOS.API.Features.Assistant.Quality;

/// <summary>一次回答里出现过的工具调用摘要（只带名字与结果摘要，不带完整返回）。</summary>
public sealed record AssistantJudgeToolCall(string Name, string? ResultDigest, bool? Ok);

/// <summary>
/// 判分输入：一条**已落库**的问答对（ADR-044 §4.1 的"最小必要"）。
///
/// <para>
/// 只带问句、回答正文、该回答的工具调用摘要与回答时间；**不带**明细行值、附件内容、
/// 其他会话与用户隐私字段。判官判的是"有没有如实回答"，不是"数据对不对"——
/// 后者由确定性对拍负责（ADR-044 §2.2）。
/// </para>
/// </summary>
public sealed record AssistantJudgeTranscript(
    long MessageId,
    long SessionId,
    string UserId,
    string Question,
    string Answer,
    IReadOnlyList<AssistantJudgeToolCall> ToolCalls,
    DateTimeOffset AnsweredAt)
{
    /// <summary>回答是否因为超过字符上限被截断（截断必须在 prompt 里说，否则判官会按片段下结论）。</summary>
    public bool AnswerTruncated { get; init; }
}
