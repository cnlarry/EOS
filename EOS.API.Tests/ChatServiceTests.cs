using EOS.API.Data;
using EOS.API.Features.Assistant;
using EOS.API.Features.Assistant.ModelAccess;
using EOS.API.Features.Assistant.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// ChatService 编排单元测试：用 fake 模型验证多轮上下文组装、
/// 流事件序列、落库时机与失败降级。仓储用内存 fake，不发 SQL。
/// </summary>
public sealed class ChatServiceTests
{
    private sealed class FakeChatModel(
        bool configured = true,
        string[]? chunks = null,
        Exception? throwOnStream = null,
        bool emitUsage = true) : IChatModel
    {
        public string ModelName => "fake-model";

        public bool IsConfigured { get; } = configured;

        public IReadOnlyList<ChatMessage> LastMessages { get; private set; } = [];

        public async IAsyncEnumerable<ChatDelta> StreamAsync(
            IReadOnlyList<ChatMessage> messages,
            IReadOnlyList<ToolDefinition>? tools,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            LastMessages = messages;
            if (throwOnStream is not null)
            {
                await Task.Yield();
                throw throwOnStream;
            }

            foreach (var chunk in chunks ?? [])
            {
                await Task.Yield();
                cancellationToken.ThrowIfCancellationRequested();
                yield return new ChatDelta(chunk, null);
            }

            if (emitUsage)
            {
                yield return new ChatDelta(null, new ChatUsage(10, 5, 123));
            }
        }
    }

    private sealed class FakeRepository : IAssistantRepository
    {
        public List<(string UserId, long SessionId, string Content)> UserMessages { get; } = [];
        public List<(string UserId, long SessionId, string Content, string Model)> AssistantMessages { get; } = [];
        public Func<string, long, IReadOnlyList<(int Role, string Content)>>? HistoryProvider { get; set; }

        public Task<AssistantSessionDto> CreateSessionAsync(string userId, CancellationToken token)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<AssistantSessionDto>> ListSessionsAsync(string userId, int limit, CancellationToken token)
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
            UserMessages.Add((userId, sessionId, content));
            return Task.FromResult(new AssistantMessageDto(1, sessionId, 1, content, null, null, null, null, correlationId, DateTimeOffset.UtcNow));
        }

        public Task<AssistantMessageDto> AddAssistantMessageAsync(
            string userId, long sessionId, string content, string modelName,
            int? promptTokens, int? completionTokens, int? elapsedMs, string correlationId,
            CancellationToken token, bool estimated = false)
        {
            AssistantMessages.Add((userId, sessionId, content, modelName));
            return Task.FromResult(new AssistantMessageDto(2, sessionId, 2, content, modelName, promptTokens, completionTokens, elapsedMs, correlationId, DateTimeOffset.UtcNow));
        }

        public async Task<IReadOnlyList<(int Role, string Content)>> LoadRecentHistoryAsync(
            string userId, long sessionId, int maxMessages, CancellationToken token)
        {
            if (HistoryProvider is not null) return HistoryProvider(userId, sessionId);
            await Task.CompletedTask;
            return [];
        }

        public Task UpdateToolCallsJsonAsync(string userId, long messageId, string toolCallsJson, CancellationToken token)
            => Task.CompletedTask;
    }

    private static ChatService CreateService(IChatModel model, FakeRepository repo) =>
        new(repo, model, new AssistantToolRegistry([]), Options.Create(new AssistantSettings { SystemPrompt = "SYS" }), NullLogger<ChatService>.Instance);

    private static async Task<List<ChatStreamEvent>> CollectAsync(IAsyncEnumerable<ChatStreamEvent> stream)
    {
        var events = new List<ChatStreamEvent>();
        await foreach (var evt in stream) events.Add(evt);
        return events;
    }

    [Fact]
    public async Task Streams_Deltas_Then_Completed_And_Persists_Reply()
    {
        var model = new FakeChatModel(chunks: ["你好", "！", "在吗"]);
        var repo = new FakeRepository();
        var service = CreateService(model, repo);

        var events = await CollectAsync(service.StreamReplyAsync("u1", 7, "  你好呀 ", null, "corr", CancellationToken.None));

        Assert.Equal(4, events.Count); // 3 delta + done
        Assert.IsType<ChatStreamEvent.Delta>(events[0]);
        var done = Assert.IsType<ChatStreamEvent.Completed>(events[^1]);
        Assert.Equal("fake-model", done.Message.ModelName);
        Assert.Equal(10, done.Message.PromptTokens);

        // 用户消息（trim 后）与助手回复均已落库
        Assert.Single(repo.UserMessages);
        Assert.Equal("你好呀", repo.UserMessages[0].Content);
        Assert.Equal("你好！在吗", repo.AssistantMessages[0].Content);
        Assert.Equal("fake-model", repo.AssistantMessages[0].Model);
    }

    [Fact]
    public async Task Builds_Messages_From_History_With_System_First_And_Trims()
    {
        var model = new FakeChatModel(chunks: ["ok"]);
        var repo = new FakeRepository
        {
            HistoryProvider = (_, _) => (IReadOnlyList<(int, string)>)[
                (3, "SYSTEM 脏数据应跳过"),
                (1, "第一问"),
                (2, "第一答"),
                (9, "未知角色跳过"),
                (1, "第二问"),
            ],
        };
        var service = CreateService(model, repo);

        await CollectAsync(service.StreamReplyAsync("u1", 7, "第二问补充", null, "corr", CancellationToken.None));

        // 系统提示 + 有效历史 3 条（脏数据/未知角色剔除）
        Assert.Equal(4, model.LastMessages.Count);
        Assert.Equal(ChatRole.System, model.LastMessages[0].Role);
        Assert.Equal("SYS", model.LastMessages[0].Content);
        Assert.Equal(("第一问"), model.LastMessages[1].Content);
        Assert.Equal(ChatRole.User, model.LastMessages[^1].Role);
    }

    [Fact]
    public async Task Not_Configured_Fails_Fast_Without_Persisting()
    {
        var model = new FakeChatModel(configured: false);
        var repo = new FakeRepository();
        var service = CreateService(model, repo);

        var events = await CollectAsync(service.StreamReplyAsync("u1", 7, "hi", null, "corr", CancellationToken.None));

        var fail = Assert.IsType<ChatStreamEvent.Failed>(Assert.Single(events));
        Assert.Equal("AI_MODEL_NOT_CONFIGURED", fail.Code);
        Assert.Empty(repo.UserMessages);
        Assert.Empty(repo.AssistantMessages);
    }

    [Fact]
    public async Task Empty_Or_Oversize_Content_Rejected_Before_Persisting()
    {
        var model = new FakeChatModel(chunks: ["x"]);
        var repo = new FakeRepository();
        var service = CreateService(model, repo);

        var blank = await CollectAsync(service.StreamReplyAsync("u1", 7, "   ", null, "corr", CancellationToken.None));
        Assert.Equal("INVALID_ARGUMENT", Assert.IsType<ChatStreamEvent.Failed>(Assert.Single(blank)).Code);

        var oversize = await CollectAsync(service.StreamReplyAsync("u1", 7, new string('长', ChatService.MaxContentLength + 1), null, "corr", CancellationToken.None));
        Assert.Equal("INVALID_ARGUMENT", Assert.IsType<ChatStreamEvent.Failed>(Assert.Single(oversize)).Code);

        Assert.Empty(repo.UserMessages);
    }

    [Fact]
    public async Task Model_Exception_Yields_Error_Event_Without_Persisting_Reply()
    {
        var model = new FakeChatModel(throwOnStream: new InvalidOperationException("boom"));
        var repo = new FakeRepository();
        var service = CreateService(model, repo);

        var events = await CollectAsync(service.StreamReplyAsync("u1", 7, "hi", null, "corr", CancellationToken.None));

        var fail = Assert.IsType<ChatStreamEvent.Failed>(Assert.Single(events));
        Assert.Equal("AI_MODEL_ERROR", fail.Code);
        // 用户消息已落库（事实保留），半截回复不落库
        Assert.Single(repo.UserMessages);
        Assert.Empty(repo.AssistantMessages);
    }
}

