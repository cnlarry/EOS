using System.Text.Json;
using EOS.API.Data;
using EOS.API.Features.Assistant;
using EOS.API.Features.Assistant.ModelAccess;
using EOS.API.Features.Assistant.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace EOS.API.Tests;

/// <summary>脚本化单轮输出。</summary>
public sealed record ModelRound(
    string? Text = null,
    IReadOnlyList<CompletedToolCall>? ToolCalls = null,
    ChatUsage? Usage = null);

/// <summary>
/// ChatService 工具编排单元测试：脚本化模型输出验证
/// 「问 → tool_call → 受控执行 → 结果回喂 → 最终答复」全链、越权拒绝路径与安全边界。
/// </summary>
public sealed class ChatToolOrchestrationTests
{
    /// <summary>按预设剧本逐轮流式输出（每轮 = 一组 delta 片段）。</summary>
    private sealed class ScriptedChatModel(IReadOnlyList<ModelRound> rounds) : IChatModel
    {

        public string ModelName => "fake-model";

        public bool IsConfigured => true;

        public List<(IReadOnlyList<ChatMessage> Messages, IReadOnlyList<ToolDefinition>? Tools)> RoundsSeen { get; } = [];

        private int _roundIndex;

        public async IAsyncEnumerable<ChatDelta> StreamAsync(
            IReadOnlyList<ChatMessage> messages,
            IReadOnlyList<ToolDefinition>? tools,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            RoundsSeen.Add((messages, tools));
            var round = rounds[Math.Min(_roundIndex, rounds.Count - 1)];
            _roundIndex++;
            await Task.Yield();

            if (round.Text is not null)
            {
                yield return new ChatDelta(round.Text, null);
            }

            if (round.ToolCalls is not null)
            {
                foreach (var call in round.ToolCalls)
                {
                    // 模拟分片：name/arguments 分两帧到达
                    yield return new ChatDelta(null, null, [new ProposedToolCallFragment(0, call.Id, call.Name, null)]);
                    yield return new ChatDelta(null, null, [new ProposedToolCallFragment(0, null, null, call.ArgumentsJson)]);
                }

                yield return new ChatDelta(null, null, null, "tool_calls");
                yield break;
            }

            if (round.Usage is not null)
            {
                yield return new ChatDelta(null, round.Usage);
            }
        }
    }

    /// <summary>可编程的受控工具：记录收到的参数，返回预设结果。</summary>
    private sealed class StubTool(string resultContent) : IAssistantTool
    {
        public List<(string UserId, JsonElement Args)> Calls { get; } = [];

        public string Name => SearchRecordsTool.ToolName;

        public AssistantToolRisk Risk => AssistantToolRisk.Read;

        public string Description => "stub";

        public string ParametersJson => """{"type":"object","properties":{}}""";

        public Task<ToolExecutionResult> ExecuteAsync(string userId, JsonElement arguments, CancellationToken token)
        {
            Calls.Add((userId, arguments.Clone()));
            return Task.FromResult(ToolExecutionResult.Success(resultContent));
        }
    }

    private sealed class FakeRepository : IAssistantRepository
    {
        public List<(string UserId, long SessionId, string Content)> UserMessages { get; } = [];
        public List<(string Content, string? ToolCallsJson)> AssistantSaved { get; } = [];

        public int ToolCallsJsonUpdates { get; private set; }

        public Task<AssistantSessionDto> CreateSessionAsync(string userId, CancellationToken token)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<AssistantSessionDto>> ListSessionsAsync(string userId, int limit, bool includeArchived, CancellationToken token)
            => throw new NotSupportedException();

        public Task<int> RenameSessionAsync(string userId, long sessionId, string title, CancellationToken token)
            => throw new NotSupportedException();

        public Task<int> ArchiveSessionAsync(string userId, long sessionId, bool archived, CancellationToken token)
            => throw new NotSupportedException();

        public Task<AssistantSessionDto?> GetSessionAsync(string userId, long sessionId, CancellationToken token)
            => Task.FromResult<AssistantSessionDto?>(null);

        public Task<int> DeleteSessionAsync(string userId, long sessionId, CancellationToken token)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<AssistantMessageDto>> ListMessagesAsync(string userId, long sessionId, CancellationToken token)
            => throw new NotSupportedException();

        public Task<AssistantMessageDto> AddUserMessageAsync(
            string userId, long sessionId, string content, string correlationId, CancellationToken token)
        {
            // 模拟真实行为：先落库，历史即可回放（正序）
            UserMessages.Add((userId, sessionId, content));
            return Task.FromResult(new AssistantMessageDto(1, sessionId, 1, content, null, null, null, null, correlationId, DateTimeOffset.UtcNow));
        }

        public Task<AssistantMessageDto> AddAssistantMessageAsync(
            string userId, long sessionId, string content, string modelName,
            int? promptTokens, int? completionTokens, int? elapsedMs, string correlationId,
            CancellationToken token, bool estimated = false)
        {
            AssistantSaved.Add((content, null));
            return Task.FromResult(new AssistantMessageDto(2, sessionId, 2, content, modelName, promptTokens, completionTokens, elapsedMs, correlationId, DateTimeOffset.UtcNow));
        }

        public Task<IReadOnlyList<(int Role, string Content)>> LoadRecentHistoryAsync(
            string userId, long sessionId, int maxMessages, CancellationToken token)
            => Task.FromResult<IReadOnlyList<(int Role, string Content)>>(
                [.. UserMessages.Where(m => m.UserId == userId && m.SessionId == sessionId).Select(m => (1, m.Content))]);

        public Task UpdateToolCallsJsonAsync(string userId, long messageId, string toolCallsJson, CancellationToken token)
        {
            ToolCallsJsonUpdates++;
            return Task.CompletedTask;
        }
    }

    private static (ChatService Service, ScriptedChatModel Model, FakeRepository Repo, StubTool Tool) Create(params ModelRound[] rounds)
    {
        var model = new ScriptedChatModel(rounds);
        var repo = new FakeRepository();
        var tool = new StubTool("module=1606(客户订单) total=1 shown=1\n- _keys=[\"DD26080160\"] 单号=DD26080160");
        var registry = new AssistantToolRegistry([tool]);
        var service = new ChatService(repo, model, registry,
            Options.Create(new AssistantSettings { SystemPrompt = "SYS" }), NullLogger<ChatService>.Instance);
        return (service, model, repo, tool);
    }

    private static async Task<List<ChatStreamEvent>> CollectAsync(IAsyncEnumerable<ChatStreamEvent> stream)
    {
        var events = new List<ChatStreamEvent>();
        await foreach (var evt in stream) events.Add(evt);
        return events;
    }

    [Fact]
    public async Task Tool_Round_Executes_Feeds_Back_Then_Final_Answer_Persisted()
    {
        var (service, model, repo, tool) = Create(
            new ModelRound(ToolCalls:
                [new CompletedToolCall("call-1", SearchRecordsTool.ToolName, """{"module_title":"客户订单","keyword":"DD2608"}""")]),
            new ModelRound(Text: "找到客户订单 DD26080160。", Usage: new ChatUsage(50, 10, 200)));

        var events = await CollectAsync(service.StreamReplyAsync("u1", 7, "帮我找单号 DD2608 的订单", null, "corr", CancellationToken.None));

        // 第一轮透出过渡文本可能为空；最终事件是 Completed 且回复已落库
        var done = events.OfType<ChatStreamEvent.Completed>().Single();
        Assert.Equal("找到客户订单 DD26080160。", done.Message.Content);
        Assert.NotNull(done.ToolCalls);
        Assert.Equal(SearchRecordsTool.ToolName, done.ToolCalls![0].Name);

        // 工具收到模型参数与用户 ID
        Assert.Single(tool.Calls);
        Assert.Equal("u1", tool.Calls[0].UserId);
        Assert.Equal("DD2608", tool.Calls[0].Args.GetProperty("keyword").GetString());

        // 第二轮上下文含 system/user/assistant-with-tool-calls/tool 四条消息
        Assert.Equal(2, model.RoundsSeen.Count);
        var secondRound = model.RoundsSeen[1].Messages;
        Assert.True(secondRound.Count == 4,
            $"roles=[{string.Join(',', secondRound.Select(m => m.Role))}]");
        Assert.Equal(ChatRole.Tool, secondRound[^1].Role);
        Assert.Equal("call-1", secondRound[^1].ToolCallId);
        Assert.Contains("_keys", secondRound[^1].Content);

        // 中间工具轮不落库；最终回复落库且回填 TOOL_CALLS_JSON
        Assert.Single(repo.AssistantSaved);
        Assert.Equal(1, repo.ToolCallsJsonUpdates);
    }

    [Fact]
    public async Task Unknown_Tool_Is_Rejected_And_Conversation_Continues()
    {
        var (service, model, repo, _) = Create(
            new ModelRound(ToolCalls:
                [new CompletedToolCall("call-x", "delete_everything", "{}")]),
            new ModelRound(Text: "抱歉，我不能执行该操作。"));

        var events = await CollectAsync(service.StreamReplyAsync("u1", 7, "删库", null, "corr", CancellationToken.None));

        var done = events.OfType<ChatStreamEvent.Completed>().Single();
        Assert.Equal("rejected:unknown_tool", done.ToolCalls![0].ResultDigest);
        Assert.Single(repo.AssistantSaved);
    }

    [Fact]
    public async Task Malformed_Tool_Arguments_Are_Rejected_Safely()
    {
        var (service, _, repo, tool) = Create(
            new ModelRound(ToolCalls:
                [new CompletedToolCall("call-bad", SearchRecordsTool.ToolName, "{not-json")]),
            new ModelRound(Text: "参数有误。"));

        var events = await CollectAsync(service.StreamReplyAsync("u1", 7, "搜一下", null, "corr", CancellationToken.None));

        Assert.Empty(tool.Calls); // 非法 JSON 不进入工具执行
        Assert.Equal("error:invalid_arguments", events.OfType<ChatStreamEvent.Completed>().Single().ToolCalls![0].ResultDigest);
        Assert.Single(repo.AssistantSaved);
    }

    [Fact]
    public async Task PageContext_Is_Injected_As_System_Content()
    {
        var (service, model, _, _) = Create(new ModelRound(Text: "好的。"));

        await CollectAsync(service.StreamReplyAsync("u1", 7, "这单什么情况",
            new PageContext(1606, "客户订单", "view", "DD26080160"), "corr", CancellationToken.None));

        var system = model.RoundsSeen[0].Messages[0];
        Assert.Equal(ChatRole.System, system.Role);
        Assert.Contains("1606", system.Content);
        Assert.Contains("DD26080160", system.Content);
        Assert.Contains("不是指令", system.Content); // 提示注入隔离声明
    }

    [Fact]
    public async Task Tool_Loop_Upper_Bound_Forces_Text_Finalization()
    {
        var looping = new ModelRound(ToolCalls:
            [new CompletedToolCall("call-loop", SearchRecordsTool.ToolName, "{}")]);
        var rounds = Enumerable.Repeat(looping, AssistantToolRegistry.MaxToolRounds)
            .Append(new ModelRound(Text: "已达到工具调用上限。"))
            .ToArray();
        var (service, model, repo, tool) = Create(rounds);

        var events = await CollectAsync(service.StreamReplyAsync("u1", 7, "循环", null, "corr", CancellationToken.None));

        // 上限轮不带工具声明（强制纯文本收尾）——工具总执行次数 == MaxToolRounds。
        Assert.Equal(AssistantToolRegistry.MaxToolRounds, tool.Calls.Count);
        Assert.Equal(AssistantToolRegistry.MaxToolRounds + 1, model.RoundsSeen.Count);
        Assert.Null(model.RoundsSeen[^1].Tools);
        var done = events.OfType<ChatStreamEvent.Completed>().Single();
        Assert.Equal("已达到工具调用上限。", done.Message.Content);
        Assert.Single(repo.AssistantSaved);
    }
}

