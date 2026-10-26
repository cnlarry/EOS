using System.Text.Json;
using EOS.API.Features.Assistant.ModelAccess;
using EOS.API.Features.Assistant.Tools;
using Microsoft.Data.SqlClient;

namespace EOS.API.Tests.Parity;

/// <summary>
/// 从库内真实会话重建 parity 用例（**只读查询**，不写任何业务表）。
///
/// <para>
/// 为什么从库里取而不是在进程内录制：真实问答、真实工具参数与真实结果摘要都已经落在
/// <c>ASSISTANT_MESSAGE</c>（<c>CONTENT</c> / <c>TOOL_CALLS_JSON</c> / <c>PROMPT_TOKENS</c> 等），
/// 重建一次就能得到"模型真见过的形状"，且**不需要改生产代码、不需要重启服务**。
/// </para>
///
/// <para>
/// 三条已知损失，都写在用例的 <c>note</c> 里：
/// </para>
/// <list type="number">
/// <item>**系统提示词未落库**：按占位文本给，不参与对比（比较器只比角色序列，不比正文）。</item>
/// <item>**流式分片丢失**：按"一次调用一帧正文"合成，回放不依赖分片粒度。</item>
/// <item>**轮次边界丢失**：<c>TOOL_CALLS_JSON</c> 是跨轮扁平列表，故按"一轮工具 + 一轮回答"重建；
/// 真实轮数大于 1 的会话会被压缩（用例里标出 <c>approximate</c>）。</item>
/// </list>
/// </summary>
internal static class SessionParityCaseGenerator
{
    /// <summary>系统提示词未落库时的占位（不参与对比，只保证"首条是 System"这一形状成立）。</summary>
    private const string SystemPromptPlaceholder =
        "（该用例由库内会话重建：系统提示词未随消息落库，此处为占位；比较器只比角色序列，不比正文。）";

    private static readonly JsonSerializerOptions ToolJsonOptions =
        new() { PropertyNameCaseInsensitive = true };

    /// <summary>读取最近的真实会话并重建用例；没有可用会话时返回空集合。</summary>
    public static async Task<IReadOnlyList<ParityCase>> ReadAsync(
        string connectionString,
        int maxCases = 10,
        CancellationToken token = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        var rows = new List<Row>();
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(token);

        await using var command = new SqlCommand(
            """
            SELECT TOP (@MaxCases)
                m.ID, m.SESSION_ID, m.CONTENT, m.MODEL_NAME,
                m.PROMPT_TOKENS, m.COMPLETION_TOKENS, m.TOOL_CALLS_JSON, m.FINISH_REASON,
                (SELECT TOP 1 u.CONTENT
                 FROM dbo.ASSISTANT_MESSAGE u
                 WHERE u.SESSION_ID = m.SESSION_ID AND u.ROLE = 1 AND u.ID < m.ID
                 ORDER BY u.ID DESC) AS USER_CONTENT
            FROM dbo.ASSISTANT_MESSAGE m
            WHERE m.ROLE = 2 AND DATALENGTH(m.CONTENT) > 0
            ORDER BY m.ID DESC;
            """,
            connection);
        command.Parameters.AddWithValue("@MaxCases", maxCases);

        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            rows.Add(new Row(
                Id: reader.GetInt64(0),
                SessionId: reader.GetInt64(1),
                Content: reader.GetString(2),
                ModelName: reader.IsDBNull(3) ? null : reader.GetString(3),
                PromptTokens: reader.IsDBNull(4) ? null : reader.GetInt32(4),
                CompletionTokens: reader.IsDBNull(5) ? null : reader.GetInt32(5),
                ToolCallsJson: reader.IsDBNull(6) ? null : reader.GetString(6),
                FinishReason: reader.IsDBNull(7) ? null : reader.GetString(7),
                UserContent: reader.IsDBNull(8) ? null : reader.GetString(8)));
        }

        return [.. rows.Where(row => !string.IsNullOrWhiteSpace(row.UserContent)).Select(Build)];
    }

    private static ParityCase Build(Row row)
    {
        var recorded = ParseToolCalls(row.ToolCallsJson);

        // 同名工具只留第一条结果：用例格式是一个工具一个桩，同一轮里重复调用的细节由 note 说明。
        var stubs = recorded
            .GroupBy(call => call.Name, StringComparer.Ordinal)
            .Select(group => ToStub(group.First()))
            .ToArray();

        var calls = new List<ParityModelCall>();
        if (recorded.Count > 0)
        {
            calls.Add(new ParityModelCall(
            [
                new ChatDelta(
                    ContentDelta: null,
                    Usage: null,
                    ToolCallDeltas:
                    [
                        .. recorded.Select((call, index) => new ProposedToolCallFragment(
                            Index: index,
                            Id: $"call_{index + 1}",
                            Name: call.Name,
                            ArgumentsFragment: call.ArgumentsJson)),
                    ],
                    FinishReason: "tool_calls"),
            ]));
        }

        var finalOutput = new List<ChatDelta>
        {
            new(ContentDelta: row.Content, Usage: null),
        };
        if (row.PromptTokens is not null || row.CompletionTokens is not null)
        {
            finalOutput.Add(new ChatDelta(
                ContentDelta: null,
                Usage: new ChatUsage(row.PromptTokens ?? 0, row.CompletionTokens ?? 0, 0)));
        }

        finalOutput.Add(new ChatDelta(
            ContentDelta: null,
            Usage: null,
            FinishReason: string.IsNullOrWhiteSpace(row.FinishReason) ? "stop" : row.FinishReason));
        calls.Add(new ParityModelCall(finalOutput));

        var approximate = recorded.Count > 0 && recorded
            .GroupBy(call => call.Name, StringComparer.Ordinal)
            .Any(group => group.Count() > 1);

        return new ParityCase(
            Name: $"real-session-{row.SessionId}-msg-{row.Id}",
            ModelName: row.ModelName ?? "unknown",
            SystemPrompt: SystemPromptPlaceholder,
            UserInput: row.UserContent!,
            MaxToolRounds: 4,
            Tools: stubs,
            Calls: calls,
            Note: BuildNote(recorded.Count, approximate));
    }

    private static string BuildNote(int toolCallCount, bool approximate) =>
        "由库内真实会话重建（只读查询 ASSISTANT_MESSAGE）。"
        + "系统提示词未落库 → 用占位文本，不参与对比；流式分片丢失 → 每次调用合成一帧正文；"
        + "工具 Schema 未落库 → 用占位 JSON（只影响声明文本，不影响工具数与结果）；"
        + (toolCallCount == 0
            ? "本轮没有工具调用。"
            : $"本轮有 {toolCallCount} 次工具调用，按“一轮工具 + 一轮回答”重建"
              + (approximate ? "；**存在同名工具重复调用，真实轮数可能大于 1，故为近似重建**。" : "。"));

    private static ParityToolStub ToStub(RecordedToolCall call) => new(
        Name: call.Name,
        Description: $"[READ] 由库内会话重建：{call.Name}",
        ParametersJson: """{"type":"object","properties":{}}""",
        Risk: AssistantToolRisk.Read,
        ReadOnly: true,
        // 结果摘要就是当初回喂给模型的内容（旧引擎落库时截断到 CHAT_TOOL_DIGEST_LENGTH）。
        ResultContent: call.ResultDigest,
        // 拒绝类摘要说明工具没成功：成功标志必须与当初一致，否则对比会把它当成回归。
        Ok: !call.ResultDigest.StartsWith("rejected:", StringComparison.Ordinal)
            && !call.ResultDigest.StartsWith("error:", StringComparison.Ordinal));

    private static IReadOnlyList<RecordedToolCall> ParseToolCalls(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<RecordedToolCall>>(json, ToolJsonOptions) ?? [];
        }
        catch (JsonException)
        {
            // 历史行可能是别的形状：宁可不生成工具桩，也不让一次坏数据挡住整批重建。
            return [];
        }
    }

    private sealed record Row(
        long Id,
        long SessionId,
        string Content,
        string? ModelName,
        int? PromptTokens,
        int? CompletionTokens,
        string? ToolCallsJson,
        string? FinishReason,
        string? UserContent);

    /// <summary>落库的工具摘要形状：<c>ChatService.ToolCallSummary</c> 的序列化结果。</summary>
    private sealed record RecordedToolCall(string Name, string ArgumentsJson, string ResultDigest);
}
