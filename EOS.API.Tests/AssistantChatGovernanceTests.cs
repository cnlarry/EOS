using EOS.API.Data;
using EOS.API.Features.Assistant;
using EOS.API.Features.Assistant.Governance;
using EOS.API.Features.Assistant.ModelAccess;
using EOS.API.Features.Assistant.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace EOS.API.Tests;

/// <summary>M7 失败口径：模型异常/空回复计入熔断；权限拒绝（工具 Deny）与限额熔断本身不计入。</summary>
public sealed class AssistantChatGovernanceTests
{
    private sealed class FakeRepo : IAssistantRepository
    {
        public Task<AssistantSessionDto> CreateSessionAsync(string userId, CancellationToken token) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<AssistantSessionDto>> ListSessionsAsync(string userId, int limit, CancellationToken token) =>
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
            Task.FromResult<IReadOnlyList<(int Role, string Content)>>([(1, "你好")]);

        public Task UpdateToolCallsJsonAsync(string userId, long messageId, string toolCallsJson, CancellationToken token) =>
            Task.CompletedTask;
    }

    private sealed class FakeUsage(bool reserveOk) : IAssistantUsageRepository
    {
        public int Settles { get; private set; }

        public Task<DailyUsage> GetUserDailyUsageAsync(string userId, DateTimeOffset dayStartUtc, CancellationToken token) =>
            Task.FromResult(new DailyUsage(0, 0, 0));

        public Task<DailyUsage> GetGlobalDailyUsageAsync(DateTimeOffset dayStartUtc, CancellationToken token) =>
            Task.FromResult(new DailyUsage(0, 0, 0));

        public Task<IReadOnlyList<(string UserId, DailyUsage Usage)>> GetPerUserDailyUsageAsync(
            DateTimeOffset dayStartUtc, int top, CancellationToken token) =>
            Task.FromResult<IReadOnlyList<(string UserId, DailyUsage Usage)>>([]);

        public Task<bool> TryReserveAsync(string userId, DateTimeOffset dayStartUtc, long reserveMicro,
            long userCapMicro, long globalCapMicro, CancellationToken token) =>
            Task.FromResult(reserveOk);

        public Task SettleAsync(string userId, DateTimeOffset dayStartUtc, long reserveMicro, long actualMicro,
            bool completed, CancellationToken token)
        {
            Settles++;
            return Task.CompletedTask;
        }
    }

    private sealed class ScriptedModel : IChatModel
    {
        public enum Mode { Text, Error, Empty, UnknownToolThenText }

        public Mode Current { get; set; } = Mode.Text;
        public int Calls { get; private set; }
        public string ModelName => "fake-model";
        public bool IsConfigured => true;

        public async IAsyncEnumerable<ChatDelta> StreamAsync(
            IReadOnlyList<ChatMessage> messages,
            IReadOnlyList<ToolDefinition>? tools,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Calls++;
            await Task.Yield();
            switch (Current)
            {
                case Mode.Error:
                    throw new InvalidOperationException("boom");
                case Mode.Empty:
                    yield break;
                case Mode.UnknownToolThenText when Calls % 2 == 1:
                    yield return new ChatDelta(null, null,
                        [new ProposedToolCallFragment(0, "c1", "nope_tool", "{}")]);
                    yield break;
                default:
                    yield return new ChatDelta("好的。", null);
                    break;
            }
        }
    }

    private static ChatService CreateService(
        FakeRepo repo, ScriptedModel model, FakeUsage usage, FailureBreaker breaker) =>
        new(repo, model, new AssistantToolRegistry([]),
            Options.Create(new AssistantSettings { SystemPrompt = "SYS" }),
            NullLogger<ChatService>.Instance, null, usage, breaker);

    private static async Task<IReadOnlyList<ChatStreamEvent>> ChatAsync(ChatService service, int rounds = 1)
    {
        var events = new List<ChatStreamEvent>();
        for (var index = 0; index < rounds; index++)
        {
            await foreach (var evt in service.StreamReplyAsync("u1", 7, "你好", null, "corr", CancellationToken.None))
            {
                events.Add(evt);
            }
        }

        return events;
    }

    [Fact]
    public async Task ModelErrors_TripBreaker_ThenRateLimit()
    {
        var model = new ScriptedModel { Current = ScriptedModel.Mode.Error };
        var breaker = new FailureBreaker(() => DateTimeOffset.UtcNow, maxFailures: 2, cooldownSeconds: 60);
        var service = CreateService(new FakeRepo(), model, new FakeUsage(true), breaker);

        var events = await ChatAsync(service, rounds: 3);

        Assert.Equal(2, events.OfType<ChatStreamEvent.Failed>().Count(fail => fail.Code == "AI_MODEL_ERROR"));
        var limited = Assert.Single(events.OfType<ChatStreamEvent.Failed>(), fail => fail.Code == "RATE_LIMITED");
        Assert.NotNull(limited);
        Assert.Equal(2, model.Calls);
    }

    [Fact]
    public async Task PermissionDeny_DoesNotTripBreaker()
    {
        var model = new ScriptedModel { Current = ScriptedModel.Mode.UnknownToolThenText };
        var breaker = new FailureBreaker(() => DateTimeOffset.UtcNow, maxFailures: 1, cooldownSeconds: 60);
        var service = CreateService(new FakeRepo(), model, new FakeUsage(true), breaker);

        var events = await ChatAsync(service);

        Assert.Contains(events.OfType<ChatStreamEvent.Completed>(), _ => true);
        Assert.False(breaker.IsBlocked("u1"));
    }

    [Fact]
    public async Task OverCap_RejectsWithoutModelCall_AndSettlesNothing()
    {
        var model = new ScriptedModel();
        var usage = new FakeUsage(reserveOk: false);
        var breaker = new FailureBreaker(() => DateTimeOffset.UtcNow, maxFailures: 1, cooldownSeconds: 60);
        var service = CreateService(new FakeRepo(), model, usage, breaker);

        var events = await ChatAsync(service);

        var failed = Assert.Single(events.OfType<ChatStreamEvent.Failed>());
        Assert.Equal("COST_LIMIT_EXCEEDED", failed.Code);
        Assert.Equal(0, model.Calls);
        Assert.Equal(0, usage.Settles);
        Assert.False(breaker.IsBlocked("u1"));
    }
}
