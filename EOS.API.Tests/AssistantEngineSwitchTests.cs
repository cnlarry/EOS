using System.Runtime.CompilerServices;
using System.Text.Json;
using EOS.API.Data;
using EOS.API.Features.Assistant;
using EOS.API.Features.Assistant.ModelAccess;
using EOS.API.Features.Assistant.Tools;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 引擎灰度开关（<c>AssistantEngine:Kind</c>）：同一套准备段与收尾段，只换"模型 + 工具循环"。
///
/// <para>
/// 这里钉两件事：**默认走旧循环**（灰度是选择加入，不是选择退出），以及切到 <c>maf</c> 后
/// 一轮纯文本对话仍能产出 <c>delta</c> 与 <c>completed</c>——回滚只需把配置改回来，不用改代码。
/// </para>
/// </summary>
public sealed class AssistantEngineSwitchTests
{
    private static ChatService Create(string? kind, IChatModel model, FakeRepository repository) =>
        new(
            repository,
            model,
            new AssistantToolRegistry([]),
            AssistantTestRuntime.Fixed(new AssistantSettings { SystemPrompt = "SYS" }),
            NullLogger<ChatService>.Instance,
            configuration: kind is null
                ? null
                : new ConfigurationBuilder()
                    .AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["AssistantEngine:Kind"] = kind,
                    })
                    .Build());

    [Theory]
    [InlineData(null)]
    [InlineData("legacy")]
    [InlineData("maf")]
    public async Task 两种引擎都能完成一轮纯文本对话(string? kind)
    {
        var model = new TextChatModel("找到了。");
        var repository = new FakeRepository();

        var events = new List<ChatStreamEvent>();
        await foreach (var evt in Create(kind, model, repository)
            .StreamReplyAsync("u1", 7, "你好", null, "corr", CancellationToken.None))
        {
            events.Add(evt);
        }

        Assert.Equal("找到了。", string.Concat(events.OfType<ChatStreamEvent.Delta>().Select(delta => delta.Text)));
        var done = Assert.Single(events.OfType<ChatStreamEvent.Completed>());
        Assert.Equal("找到了。", done.Message.Content);
        Assert.Single(repository.AssistantSaved);
        Assert.Empty(events.OfType<ChatStreamEvent.Failed>());
    }

    /// <summary>脚本化模型：一次调用返回正文与用量。</summary>
    private sealed class TextChatModel(string text) : IChatModel
    {
        public string ModelName => "switch-model";

        public bool IsConfigured => true;

        public async IAsyncEnumerable<ChatDelta> StreamAsync(
            IReadOnlyList<ChatMessage> messages,
            IReadOnlyList<ToolDefinition>? tools,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            yield return new ChatDelta(text, null);
            yield return new ChatDelta(null, new ChatUsage(10, 5, 20));
            yield return new ChatDelta(null, null, null, "stop");
        }
    }

    private sealed class FakeRepository : IAssistantRepository
    {
        public List<(string Content, string? ToolCallsJson)> AssistantSaved { get; } = [];

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
            AssistantSaved.Add((content, null));
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
