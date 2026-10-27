using System.Runtime.CompilerServices;
using System.Text.Json;
using EOS.API.Data;
using EOS.API.Features.Assistant;
using EOS.API.Features.Assistant.ModelAccess;
using EOS.API.Features.Assistant.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 对话引擎（Agent Harness 的 <c>MafAgentEngine</c>）与 EOS 编排的接缝。
///
/// <para>
/// 这里钉的是"引擎换了，账本不能换"：增量与完成事件照旧、用量与耗时照旧落库、
/// 工具轮的过渡正文只流给用户不进会话历史。工具执行细节见 <see cref="ChatToolOrchestrationTests"/>。
/// </para>
/// </summary>
public sealed class AssistantChatEngineTests
{
    [Fact]
    public async Task 纯文本回合产出增量与完成事件并落库()
    {
        var model = new ScriptedModel(
            [new Round(Text: "找到了。", Usage: new ChatUsage(10, 5, 20), Finish: "stop")]);
        var repository = new FakeRepository();

        var events = await CollectAsync(Create(model, repository), "你好");

        Assert.Equal("找到了。", string.Concat(events.OfType<ChatStreamEvent.Delta>().Select(delta => delta.Text)));
        var done = Assert.Single(events.OfType<ChatStreamEvent.Completed>());
        Assert.Equal("找到了。", done.Message.Content);
        var saved = Assert.Single(repository.AssistantSaved);
        Assert.Equal(10, saved.PromptTokens);
        Assert.Equal(5, saved.CompletionTokens);
        Assert.Equal(20, saved.ElapsedMs); // 引擎的用量事件不带耗时，适配层逐帧累加
        Assert.False(saved.Estimated);
        Assert.Empty(events.OfType<ChatStreamEvent.Failed>());
    }

    [Fact]
    public async Task 工具轮的过渡正文只流给用户_最终回复只取最后一轮()
    {
        var tool = new StubTool("module=1606 total=1");
        var model = new ScriptedModel(
        [
            new Round(Text: "我查一下。", Tool: new CompletedToolCall(
                "call-1", SearchRecordsTool.ToolName, """{"keyword":"DD2608"}"""), Finish: "tool_calls"),
            new Round(Text: "查到了。", Usage: new ChatUsage(30, 6, 40), Finish: "stop"),
        ]);
        var repository = new FakeRepository();

        var events = await CollectAsync(Create(model, repository, tool), "帮我找单");

        // 两轮正文都流给了界面，但落库与历史里只有最后一轮——
        // 拼各轮正文会让"我查一下"永久留在会话里，下一轮又被当成助手说过的话回喂。
        Assert.Equal("我查一下。查到了。", string.Concat(events.OfType<ChatStreamEvent.Delta>().Select(delta => delta.Text)));
        var done = Assert.Single(events.OfType<ChatStreamEvent.Completed>());
        Assert.Equal("查到了。", done.Message.Content);
        Assert.Equal(SearchRecordsTool.ToolName, Assert.Single(done.ToolCalls!).Name);
        Assert.Single(tool.Calls);
        Assert.Equal("DD2608", tool.Calls[0].GetProperty("keyword").GetString());
    }

    [Fact]
    public async Task 模型不返回用量时按估算落库并标记()
    {
        var model = new ScriptedModel([new Round(Text: "好的。", Usage: null, Finish: "stop")]);
        var repository = new FakeRepository();

        var events = await CollectAsync(Create(model, repository), "你好");

        Assert.Contains(events, evt => evt is ChatStreamEvent.Completed);
        var saved = Assert.Single(repository.AssistantSaved);
        Assert.True(saved.Estimated);
        Assert.True(saved.PromptTokens > 0 && saved.CompletionTokens > 0);
    }

    private static ChatService Create(
        IChatModel model, FakeRepository repository, IAssistantTool? tool = null) =>
        new(
            repository,
            model,
            new AssistantToolRegistry(tool is null ? [] : [tool]),
            AssistantTestRuntime.Fixed(new AssistantSettings { SystemPrompt = "SYS" }),
            NullLogger<ChatService>.Instance);

    private static async Task<List<ChatStreamEvent>> CollectAsync(ChatService service, string content)
    {
        var events = new List<ChatStreamEvent>();
        await foreach (var evt in service.StreamReplyAsync("u1", 7, content, null, "corr", CancellationToken.None))
        {
            events.Add(evt);
        }

        return events;
    }

    private sealed record Round(
        string? Text = null,
        CompletedToolCall? Tool = null,
        ChatUsage? Usage = null,
        string? Finish = null);

    /// <summary>按剧本逐轮输出；剧本用完后重复最后一轮。</summary>
    private sealed class ScriptedModel(IReadOnlyList<Round> rounds) : IChatModel
    {
        public string ModelName => "engine-model";

        public bool IsConfigured => true;

        private int _index;

        public async IAsyncEnumerable<ChatDelta> StreamAsync(
            IReadOnlyList<ChatMessage> messages,
            IReadOnlyList<ToolDefinition>? tools,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var round = rounds[Math.Min(_index++, rounds.Count - 1)];
            await Task.Yield();

            if (round.Text is not null)
            {
                yield return new ChatDelta(round.Text, null);
            }

            if (round.Tool is not null)
            {
                yield return new ChatDelta(null, null,
                    [new ProposedToolCallFragment(0, round.Tool.Id, round.Tool.Name, null)]);
                yield return new ChatDelta(null, null,
                    [new ProposedToolCallFragment(0, null, null, round.Tool.ArgumentsJson)]);
            }

            if (round.Usage is not null)
            {
                yield return new ChatDelta(null, round.Usage, null, round.Finish);
                yield break;
            }

            if (round.Finish is not null)
            {
                yield return new ChatDelta(null, null, null, round.Finish);
            }
        }
    }

    private sealed class StubTool(string resultContent) : IAssistantTool
    {
        public List<JsonElement> Calls { get; } = [];

        public string Name => SearchRecordsTool.ToolName;

        public AssistantToolRisk Risk => AssistantToolRisk.Read;

        public string Description => "stub";

        public string ParametersJson => """{"type":"object","properties":{"keyword":{"type":"string"}}}""";

        public Task<ToolExecutionResult> ExecuteAsync(string userId, JsonElement arguments, CancellationToken token)
        {
            Calls.Add(arguments.Clone());
            return Task.FromResult(ToolExecutionResult.Success(resultContent));
        }
    }

    private sealed class FakeRepository : IAssistantRepository
    {
        public List<(string Content, int? PromptTokens, int? CompletionTokens, int? ElapsedMs, bool Estimated)>
            AssistantSaved { get; } = [];

        public Task<AssistantSessionDto> CreateSessionAsync(string userId, CancellationToken token)
            => throw new NotSupportedException();

        public Task<(IReadOnlyList<AssistantSessionDto> Items, int Total)> ListSessionsAsync(
            string userId, int offset, int limit, AssistantSessionListState state, string? keyword, CancellationToken token)
            => throw new NotSupportedException();

        public Task<int> RenameSessionAsync(string userId, long sessionId, string title, CancellationToken token)
            => throw new NotSupportedException();

        public Task<int> ArchiveSessionAsync(string userId, long sessionId, bool archived, CancellationToken token)
            => throw new NotSupportedException();

        public Task<AssistantSessionDto?> GetSessionAsync(string userId, long sessionId, CancellationToken token)
            => Task.FromResult<AssistantSessionDto?>(null);

        public Task<int> DeleteSessionAsync(string userId, long sessionId, CancellationToken token)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<AssistantMessageDto>> ListMessagesAsync(
            string userId, long sessionId, CancellationToken token)
            => throw new NotSupportedException();

        public Task<AssistantMessageDto> AddUserMessageAsync(
            string userId, long sessionId, string content, string correlationId, CancellationToken token)
            => Task.FromResult(new AssistantMessageDto(
                1, sessionId, 1, content, null, null, null, null, correlationId, DateTimeOffset.UtcNow));

        public Task<AssistantMessageDto> AddAssistantMessageAsync(
            string userId, long sessionId, string content, string modelName,
            int? promptTokens, int? completionTokens, int? elapsedMs, string correlationId,
            CancellationToken token, bool estimated = false, string? finishReason = null)
        {
            AssistantSaved.Add((content, promptTokens, completionTokens, elapsedMs, estimated));
            return Task.FromResult(new AssistantMessageDto(
                2, sessionId, 2, content, modelName, promptTokens, completionTokens, elapsedMs,
                correlationId, DateTimeOffset.UtcNow));
        }

        public Task<IReadOnlyList<(int Role, string Content)>> LoadRecentHistoryAsync(
            string userId, long sessionId, int maxMessages, CancellationToken token)
            => Task.FromResult<IReadOnlyList<(int Role, string Content)>>([]);

        public Task UpdateToolCallsJsonAsync(
            string userId, long messageId, string toolCallsJson, CancellationToken token)
            => Task.CompletedTask;

        public Task<int> SetMessageFeedbackAsync(
            string userId, long messageId, int? feedback, string? reason, CancellationToken token)
            => Task.FromResult(0);
    }
}
