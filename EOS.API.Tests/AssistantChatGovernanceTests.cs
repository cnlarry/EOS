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
        public bool SessionDenied { get; set; }

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
            string correlationId, CancellationToken token)
        {
            if (SessionDenied) throw new UnauthorizedAccessException("会话不属于当前用户。");
            return Task.FromResult(new AssistantMessageDto(1, sessionId, 1, content, null, null, null, null,
                correlationId, DateTimeOffset.UtcNow));
        }

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
        public int Reserves { get; private set; }
        public int Settles { get; private set; }
        public long LastReserveMicro { get; private set; }
        public List<(long ReserveMicro, long ActualMicro, bool Completed)> SettleCalls { get; } = [];

        public Task<DailyUsage> GetUserDailyUsageAsync(string userId, DateTimeOffset dayStartUtc, CancellationToken token) =>
            Task.FromResult(new DailyUsage(0, 0, 0));

        public Task<DailyUsage> GetGlobalDailyUsageAsync(DateTimeOffset dayStartUtc, CancellationToken token) =>
            Task.FromResult(new DailyUsage(0, 0, 0));

        public Task<IReadOnlyList<(string UserId, DailyUsage Usage)>> GetPerUserDailyUsageAsync(
            DateTimeOffset dayStartUtc, int top, CancellationToken token) =>
            Task.FromResult<IReadOnlyList<(string UserId, DailyUsage Usage)>>([]);

        public Task<bool> TryReserveAsync(string userId, DateTimeOffset dayStartUtc, long reserveMicro,
            long userCapMicro, long globalCapMicro, CancellationToken token)
        {
            Reserves++;
            LastReserveMicro = reserveMicro;
            return Task.FromResult(reserveOk);
        }

        public Task SettleAsync(string userId, DateTimeOffset dayStartUtc, long reserveMicro, long actualMicro,
            bool completed, CancellationToken token)
        {
            Settles++;
            SettleCalls.Add((reserveMicro, actualMicro, completed));
            return Task.CompletedTask;
        }
    }

    private sealed class ScriptedModel : IChatModel
    {
        public enum Mode { Text, Error, Empty, UnknownToolThenText }

        public Mode Current { get; set; } = Mode.Text;
        public int Calls { get; private set; }
        public bool EmitUsage { get; set; }
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
                    break;
                default:
                    yield return new ChatDelta("好的。", null);
                    break;
            }

            if (EmitUsage)
            {
                yield return new ChatDelta(null, new ChatUsage(10, 5, 123));
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

    private static async Task<IReadOnlyList<ChatStreamEvent>> CollectAsync(ChatService service, string content)
    {
        var events = new List<ChatStreamEvent>();
        await foreach (var evt in service.StreamReplyAsync("u1", 7, content, null, "corr", CancellationToken.None))
        {
            events.Add(evt);
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

    [Fact]
    public async Task InvalidContent_DoesNotReserveOrSettle()
    {
        var usage = new FakeUsage(reserveOk: true);
        var service = CreateService(new FakeRepo(), new ScriptedModel(), usage,
            new FailureBreaker(() => DateTimeOffset.UtcNow, maxFailures: 1, cooldownSeconds: 60));

        var blank = await CollectAsync(service, "   ");
        Assert.Equal("INVALID_ARGUMENT", Assert.IsType<ChatStreamEvent.Failed>(Assert.Single(blank)).Code);

        var oversize = await CollectAsync(service, new string('长', ChatService.MaxContentLength + 1));
        Assert.Equal("INVALID_ARGUMENT", Assert.IsType<ChatStreamEvent.Failed>(Assert.Single(oversize)).Code);

        Assert.Equal(0, usage.Reserves);
        Assert.Equal(0, usage.Settles);
    }

    [Fact]
    public async Task ForeignSession_DoesNotReserveOrSettle()
    {
        var usage = new FakeUsage(reserveOk: true);
        var service = CreateService(new FakeRepo { SessionDenied = true }, new ScriptedModel(), usage,
            new FailureBreaker(() => DateTimeOffset.UtcNow, maxFailures: 1, cooldownSeconds: 60));

        var events = await CollectAsync(service, "你好");

        Assert.Equal("NOT_FOUND", Assert.IsType<ChatStreamEvent.Failed>(Assert.Single(events)).Code);
        Assert.Equal(0, usage.Reserves);
        Assert.Equal(0, usage.Settles);
    }

    [Fact]
    public async Task ValidRequest_ReservesOnce_AndSettlesCompleted()
    {
        var usage = new FakeUsage(reserveOk: true);
        var service = CreateService(new FakeRepo(), new ScriptedModel(), usage,
            new FailureBreaker(() => DateTimeOffset.UtcNow, maxFailures: 1, cooldownSeconds: 60));

        var events = await CollectAsync(service, "你好");

        Assert.Contains(events, evt => evt is ChatStreamEvent.Completed);
        Assert.Equal(1, usage.Reserves);
        Assert.Equal(1, usage.Settles);
        // 单请求最多 MaxToolRounds+1 次模型调用：预留 = 每轮 0.05 元 × 5。
        Assert.Equal(50_000L * (AssistantToolRegistry.MaxToolRounds + 1), usage.LastReserveMicro);
    }

    [Fact]
    public async Task MultiRound_SettlesCumulativeUsage_NotJustFinalRound()
    {
        var model = new ScriptedModel
        {
            Current = ScriptedModel.Mode.UnknownToolThenText,
            EmitUsage = true,
        };
        var usage = new FakeUsage(reserveOk: true);
        var service = CreateService(new FakeRepo(), model, usage,
            new FailureBreaker(() => DateTimeOffset.UtcNow, maxFailures: 1, cooldownSeconds: 60));

        var events = await CollectAsync(service, "你好");

        Assert.Contains(events, evt => evt is ChatStreamEvent.Completed);
        Assert.Equal(2, model.Calls); // 工具轮 + 最终文本轮
        var settle = Assert.Single(usage.SettleCalls);
        Assert.True(settle.Completed);
        // 每轮 usage 固定 10 prompt + 5 completion：两轮累计后结算，而非只按末轮。
        var options = new AssistantCostOptions();
        var cumulative = ChatService.ToMicroYuan(AssistantCost.Calculate(20, 10, options));
        var finalRoundOnly = ChatService.ToMicroYuan(AssistantCost.Calculate(10, 5, options));
        Assert.Equal(cumulative, settle.ActualMicro);
        Assert.True(settle.ActualMicro > finalRoundOnly);
    }
}
