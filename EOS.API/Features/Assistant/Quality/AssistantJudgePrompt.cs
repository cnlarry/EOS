using System.Text;
using EOS.API.Features.Assistant.Governance;
using EOS.API.Features.Assistant.Parameters;

namespace EOS.API.Features.Assistant.Quality;

/// <summary>
/// 判分提示词：把条款与转录拼成"一次判定"的输入。
///
/// <para>
/// 三条硬约束写进系统提示词而不是只写在文档里：**证据必须逐字引用**（拿不出引用就是 UNSURE）、
/// **不判数值真值与权限**（各有更准的真源）、**只输出 JSON**（下游按结构解析，散文一律视为无效输出）。
/// </para>
/// </summary>
public static class AssistantJudgePrompt
{
    /// <summary>系统提示词（不含任何会话内容，可缓存复用）。</summary>
    public const string System =
        """
        你是 EOS ERP 工作助手的回答质量判定员。你只回答一个问题：**这条回答有没有如实回答**。

        判定规则：
        1) 逐条判断下面给出的条款，每条给出 PASS / FAIL / NA / UNSURE 之一。
           - PASS：条款要求的都做到了；
           - FAIL：明确违反了条款；
           - NA：该条款对这次回答**不适用**（例如纯机制问答不涉及金额、没有需要转述的缺口）；
           - UNSURE：证据不足以下结论。
        2) **evidence 必须逐字引用回答原文里的一句话**（可截断到一句）。给不出引用时，
           该条只能是 UNSURE——不允许凭印象判 PASS/FAIL。
        3) **不判数值是否正确、不判权限判定是否正确**：这两件事各有确定性真源，不归你判。
           数值有疑问但回答点明了口径与来源时，A 条款仍可 PASS。
        4) 不判文风、长度、措辞、语气。
        5) **遇事不明就 UNSURE**：把"拿不准"判成 PASS 会让这个指标失去意义。

        只输出 JSON，不要解释、不要 Markdown 代码块：
        {"verdicts":[{"clause":"A","verdict":"PASS","evidence":"回答原文里的一句话","note":"可选的一句话"}]}
        """;

    /// <summary>拼 user 消息：条款清单 + 转录（回答超上限时截断并**明确写出被截断**）。</summary>
    public static string BuildUser(
        AssistantJudgeRubric rubric, AssistantJudgeTranscript transcript, AssistantJudgeLimitsOptions limits)
    {
        var builder = new StringBuilder();
        builder.AppendLine("<条款>");
        foreach (var clause in rubric.Clauses)
        {
            builder.Append("- ").Append(clause.Code).Append(' ').Append(clause.Title).Append("：")
                .AppendLine(clause.Question);
        }

        builder.AppendLine("</条款>");
        builder.AppendLine("<转录>");
        builder.Append("用户问句：").AppendLine(Trim(transcript.Question, limits.MaxQuestionChars));
        builder.Append("助手回答：").AppendLine(Trim(transcript.Answer, limits.MaxAnswerChars));
        if (transcript.AnswerTruncated || transcript.Answer.Length > limits.MaxAnswerChars)
        {
            builder.AppendLine("（注意：上面的回答因长度上限被截断，后续内容未提供；无法判断的部分记 UNSURE。）");
        }

        if (transcript.ToolCalls.Count == 0)
        {
            builder.AppendLine("本轮工具调用：无");
        }
        else
        {
            builder.AppendLine("本轮工具调用（名称 / 成败 / 结果摘要）：");
            foreach (var call in transcript.ToolCalls)
            {
                builder.Append("  - ").Append(call.Name)
                    .Append(" / ").Append(call.Ok switch { true => "成功", false => "失败", null => "结果不明" })
                    .Append(" / ")
                    .AppendLine(Trim(call.ResultDigest ?? string.Empty, limits.MaxToolDigestChars));
            }
        }

        builder.AppendLine("</转录>");
        return builder.ToString();
    }

    private static string Trim(string value, int max)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return value.Length <= max ? value : value[..max] + "…";
    }
}
