using System.Text.Json;

namespace EOS.API.Features.Assistant.Admin;

public sealed record AssistantQualityOverview(
    int Sessions,
    int UserMessages,
    int AssistantMessages,
    int ToolCalls,
    int SucceededCalls,
    int FailedCalls,
    int UnknownOutcomeCalls,
    int RejectedCalls,
    int UnknownToolCalls,
    long PromptTokens,
    long CompletionTokens,
    int AverageElapsedMs,
    int PositiveFeedback,
    int NegativeFeedback);

public sealed record AssistantQualityToolStat(
    string Name, int Calls, int Succeeded, int Failed, int UnknownOutcome, int Rejected, int Unknown);

public sealed record AssistantQualityDay(
    string Day, int Messages, int ToolCalls, int Failed, long PromptTokens, long CompletionTokens);

public sealed record AssistantQualityCallBucket(int CallsPerReply, int Replies);

public sealed record AssistantQualityReport(
    int Days,
    DateTimeOffset Since,
    AssistantQualityOverview Overview,
    IReadOnlyList<AssistantQualityToolStat> Tools,
    IReadOnlyList<AssistantQualityDay> Daily,
    IReadOnlyList<AssistantQualityCallBucket> CallBuckets,
    decimal SpentYuan,
    int Requests)
{
    /// <summary>
    /// 「回答质量」区块（ADR-044 §4.5）。单独作为可选属性而不是构造函数参数：
    /// 它来自另一张表（判定结论），取数失败时整页质量报表仍应可用——但那时它会明确为空，
    /// 而不是显示一组 0（0 表示"判了，全都错"）。
    /// </summary>
    public AssistantAnswerQualityBlock? AnswerQuality { get; init; }

    /// <summary>质量告警（J3，只告警不阻断）：口径标注率偏低 / 越界承诺 / 无法判定率偏高 / 停判。</summary>
    public IReadOnlyList<AssistantQualityAlert> Alerts { get; init; } = [];
}

/// <summary>
/// 质量看板的纯聚合：输入消息样本与用量，输出结构化统计。
///
/// <para>
/// 工具失败只按**稳定标记**计：`rejected:`（含 `unknown_tool` / `disabled_by_admin`）。
/// 旧数据没有 Ok 列，工具返回原文（如“没查到”）不据此判失败——宁可少报，不把
/// “查到了但为空”算成失败。旧数据里没有 `ok` 的事实，也在返回的「口径」里说明。
/// </para>
/// </summary>
public static class AssistantQualityView
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public static AssistantQualityReport Build(
        int days, DateTimeOffset since,
        IReadOnlyList<AssistantQualityMessage> messages,
        AssistantQualityUsage usage)
    {
        var sessions = new HashSet<long>();
        var toolStats = new Dictionary<string, (int Calls, int Succeeded, int Failed, int UnknownOutcome, int Rejected, int Unknown)>(StringComparer.Ordinal);
        var daily = new Dictionary<DateOnly, (int Messages, int ToolCalls, int Failed, long Prompt, long Completion)>();
        var callBuckets = new Dictionary<int, int>();        int userMessages = 0, assistantMessages = 0, toolCalls = 0;
        int succeeded = 0, failed = 0, unknownOutcome = 0, rejected = 0, unknown = 0;
        long promptTokens = 0, completionTokens = 0;
        long elapsedTotal = 0;
        var elapsedSamples = 0;
        int positive = 0, negative = 0;

        foreach (var message in messages)
        {
            sessions.Add(message.SessionId);
            if (message.Role == 1) userMessages++;
            if (message.Role == 2) assistantMessages++;
            promptTokens += message.PromptTokens;
            completionTokens += message.CompletionTokens;
            if (message.ElapsedMs > 0)
            {
                elapsedTotal += message.ElapsedMs;
                elapsedSamples++;
            }

            if (message.Feedback == 1) positive++;
            if (message.Feedback == -1) negative++;

            var calls = ParseToolCalls(message.ToolCallsJson);
            var callCount = calls.Count;
            if (message.Role == 2)
            {
                var bucket = Math.Min(callCount, 5);
                callBuckets[bucket] = callBuckets.GetValueOrDefault(bucket) + 1;
            }

            var day = DateOnly.FromDateTime(message.CreatedAt.UtcDateTime);
            var currentDay = daily.GetValueOrDefault(day);
            currentDay.Messages++;
            daily[day] = currentDay;

            foreach (var call in calls)
            {
                toolCalls++;
                var name = string.IsNullOrWhiteSpace(call.Name) ? "(unknown)" : call.Name.Trim();
                var digest = call.ResultDigest ?? string.Empty;
                var isRejected = digest.StartsWith("rejected:", StringComparison.OrdinalIgnoreCase);
                var isUnknown = digest.Equals("rejected:unknown_tool", StringComparison.OrdinalIgnoreCase);
                if (call.Ok == true) succeeded++;
                else if (call.Ok == false) failed++;
                else unknownOutcome++;
                if (isRejected) rejected++;
                if (isUnknown) unknown++;

                var stat = toolStats.GetValueOrDefault(name);
                stat.Calls++;
                if (call.Ok == true) stat.Succeeded++;
                else if (call.Ok == false) stat.Failed++;
                else stat.UnknownOutcome++;
                if (isRejected) stat.Rejected++;
                if (isUnknown) stat.Unknown++;
                toolStats[name] = stat;

                currentDay.ToolCalls++;
                if (call.Ok == false) currentDay.Failed++;
                daily[day] = currentDay;
            }

            currentDay = daily.GetValueOrDefault(day);
            currentDay.Prompt += message.PromptTokens;
            currentDay.Completion += message.CompletionTokens;
            daily[day] = currentDay;
        }

        return new AssistantQualityReport(
            days,
            since,
            new AssistantQualityOverview(
                sessions.Count, userMessages, assistantMessages, toolCalls,
                succeeded, failed, unknownOutcome, rejected, unknown,
                promptTokens, completionTokens,
                elapsedSamples == 0 ? 0 : (int)(elapsedTotal / elapsedSamples),
                positive, negative),
            [.. toolStats
                .OrderByDescending(pair => pair.Value.Calls)
                .ThenBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => new AssistantQualityToolStat(
                    pair.Key, pair.Value.Calls, pair.Value.Succeeded, pair.Value.Failed,
                    pair.Value.UnknownOutcome, pair.Value.Rejected, pair.Value.Unknown))],
            [.. daily
                .OrderBy(pair => pair.Key)
                .Select(pair => new AssistantQualityDay(
                    pair.Key.ToString("yyyy-MM-dd"), pair.Value.Messages, pair.Value.ToolCalls,
                    pair.Value.Failed, pair.Value.Prompt, pair.Value.Completion))],
            [.. callBuckets.OrderBy(pair => pair.Key)
                .Select(pair => new AssistantQualityCallBucket(pair.Key, pair.Value))],
            usage.SpentMicroYuan / 1_000_000m,
            usage.Requests);
    }

    private sealed record QualityToolCall(string? Name, string? ResultDigest, bool? Ok = null);

    private static IReadOnlyList<QualityToolCall> ParseToolCalls(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<QualityToolCall>>(json, JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            // 历史数据里可能有非结构化内容：看板不能因此 500，按"没有工具调用"计。
            return [];
        }
    }
}
