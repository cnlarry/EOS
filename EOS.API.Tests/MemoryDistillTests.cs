using EOS.API.Data;
using EOS.API.Features.Assistant;
using EOS.API.Features.Assistant.Memory;
using EOS.API.Features.Assistant.ModelAccess;
using EOS.API.Features.Assistant.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace EOS.API.Tests;

public sealed class MemoryDistillTests
{
    [Fact]
    public void Parse_KeepsValidCandidates_WithConfidence()
    {
        var candidates = MemoryDistiller.Parse("""
            {"memories":[
              {"type":"preference","key":"常用模块","value":"先看送货单","confidence":85,"reason":"多次查看"},
              {"type":"fact","key":"仓库","value":"主仓是示例仓","confidence":60,"reason":"用户说明"}
            ]}
            """);

        Assert.Equal(2, candidates.Count);
        Assert.Equal(85, candidates[0].Confidence);
        Assert.Equal("preference", candidates[0].Type);
    }

    [Fact]
    public void Parse_DropsLowConfidence_Sensitive_AndMalformed()
    {
        var candidates = MemoryDistiller.Parse("""
            {"memories":[
              {"type":"fact","key":"低置信","value":"随口一提","confidence":20,"reason":"x"},
              {"type":"fact","key":"电话","value":"13800138000","confidence":90,"reason":"x"},
              {"type":"nope","key":"坏类型","value":"内容","confidence":90,"reason":"x"},
              {"type":"fact","key":"","value":"空标题","confidence":90,"reason":"x"},
              "not-an-object"
            ]}
            """);

        Assert.Empty(candidates);
        Assert.Empty(MemoryDistiller.Parse("这不是 JSON"));
        Assert.Empty(MemoryDistiller.Parse("""{"other":[]}"""));
    }

    [Fact]
    public void Parse_CapsAtFive()
    {
        var items = string.Join(",", Enumerable.Range(1, 8)
            .Select(index => $"{{\"type\":\"fact\",\"key\":\"事项{index}\",\"value\":\"内容{index}\",\"confidence\":70,\"reason\":\"x\"}}"));
        var candidates = MemoryDistiller.Parse($"{{\"memories\":[{items}]}}");

        Assert.Equal(MemoryDistiller.MaxCandidates, candidates.Count);
    }

    private sealed class FakeStore : IAssistantMemoryStore
    {
        public List<(string Key, int Confidence)> Pendings { get; } = [];

        public Task<string?> GetPreferencesAsync(string userId, CancellationToken token) =>
            Task.FromResult<string?>(null);

        public Task SetPreferencesAsync(string userId, string? preferencesJson, CancellationToken token) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<AssistantMemoryItem>> ListMemoriesAsync(string userId, CancellationToken token) =>
            Task.FromResult<IReadOnlyList<AssistantMemoryItem>>([]);

        public Task<AssistantMemoryItem> AddMemoryAsync(string userId, string memoryType, string memoryKey,
            string memoryValue, long? sourceMessageId, CancellationToken token) => throw new NotSupportedException();

        public Task<AssistantMemoryItem> AddPendingAsync(string userId, string memoryType, string memoryKey,
            string memoryValue, long? sourceMessageId, int confidence, CancellationToken token)
        {
            Pendings.Add((memoryKey, confidence));
            return Task.FromResult(new AssistantMemoryItem(1, memoryType, memoryKey, memoryValue,
                "auto", confidence, "pending", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        }

        public Task<IReadOnlyList<AssistantMemoryItem>> ListPendingAsync(string userId, CancellationToken token) =>
            Task.FromResult<IReadOnlyList<AssistantMemoryItem>>([]);

        public List<(long Id, bool Confirm)> Resolves { get; } = [];

        public Task<string> ResolvePendingAsync(string userId, long memoryId, bool confirm, CancellationToken token)
        {
            Resolves.Add((memoryId, confirm));
            return Task.FromResult("confirmed");
        }

        public Task<bool> DeleteMemoryAsync(string userId, long memoryId, CancellationToken token) =>
            Task.FromResult(true);

        public Task ForgetMeAsync(string userId, CancellationToken token) => Task.CompletedTask;

        public Task<string> BuildMemoryPrefixAsync(string userId, string? keyword, CancellationToken token) =>
            Task.FromResult(string.Empty);
    }

    private sealed class FakeRepo : IAssistantRepository
    {
        public Task<AssistantSessionDto> CreateSessionAsync(string userId, CancellationToken token) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<AssistantSessionDto>> ListSessionsAsync(string userId, int limit, bool includeArchived, CancellationToken token) =>
            throw new NotSupportedException();

        public Task<int> RenameSessionAsync(string userId, long sessionId, string title, CancellationToken token) =>
            throw new NotSupportedException();

        public Task<int> ArchiveSessionAsync(string userId, long sessionId, bool archived, CancellationToken token) =>
            throw new NotSupportedException();

        public Task<AssistantSessionDto?> GetSessionAsync(string userId, long sessionId, CancellationToken token) =>
            throw new NotSupportedException();

        public Task<int> DeleteSessionAsync(string userId, long sessionId, CancellationToken token) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<AssistantMessageDto>> ListMessagesAsync(string userId, long sessionId, CancellationToken token) =>
            throw new NotSupportedException();

        public Task<AssistantMessageDto> AddUserMessageAsync(string userId, long sessionId, string content,
            string correlationId, CancellationToken token) =>
            Task.FromResult(new AssistantMessageDto(1, sessionId, 1, content, null, null, null, null,
                correlationId, DateTimeOffset.UtcNow));

        public Task<AssistantMessageDto> AddAssistantMessageAsync(string userId, long sessionId, string content,
            string modelName, int? promptTokens, int? completionTokens, int? elapsedMs, string correlationId,
            CancellationToken token, bool estimated = false) =>
            Task.FromResult(new AssistantMessageDto(2, sessionId, 2, content, modelName, promptTokens,
                completionTokens, elapsedMs, correlationId, DateTimeOffset.UtcNow));

        public Task<IReadOnlyList<(int Role, string Content)>> LoadRecentHistoryAsync(string userId, long sessionId,
            int maxMessages, CancellationToken token) =>
            Task.FromResult<IReadOnlyList<(int Role, string Content)>>([(1, "送货单怎么查")]);

        public Task UpdateToolCallsJsonAsync(string userId, long messageId, string toolCallsJson, CancellationToken token) =>
            Task.CompletedTask;
    }

    private sealed class ScriptedModel : IChatModel
    {
        public string ModelName => "fake-model";
        public bool IsConfigured => true;
        private int _calls;

        public async IAsyncEnumerable<ChatDelta> StreamAsync(
            IReadOnlyList<ChatMessage> messages,
            IReadOnlyList<ToolDefinition>? tools,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            if (_calls++ == 0)
            {
                yield return new ChatDelta("用 search_records 查一下。", null);
            }
            else
            {
                yield return new ChatDelta(
                    """{"memories":[{"type":"preference","key":"常用模块","value":"先看送货单","confidence":82,"reason":"用户多次查看"}]}""",
                    null);
            }
        }
    }

    [Fact]
    public async Task CompletedReply_TriggersPendingDistillation()
    {
        var store = new FakeStore();
        var service = new ChatService(new FakeRepo(), new ScriptedModel(),
            new AssistantToolRegistry([]),
            Options.Create(new AssistantSettings { SystemPrompt = "SYS" }),
            NullLogger<ChatService>.Instance, store);

        var events = new List<ChatStreamEvent>();
        await foreach (var evt in service.StreamReplyAsync("u1", 7, "送货单怎么查", null, "corr", CancellationToken.None))
        {
            events.Add(evt);
        }

        Assert.Single(events.OfType<ChatStreamEvent.Completed>());
        var pending = Assert.Single(store.Pendings);
        Assert.Equal("常用模块", pending.Key);
        Assert.Equal(82, pending.Confidence);
        // ≥80 已拍板自动转正：提炼后立即确认（覆盖同名）。
        var resolved = Assert.Single(store.Resolves);
        Assert.True(resolved.Confirm);
    }
}

