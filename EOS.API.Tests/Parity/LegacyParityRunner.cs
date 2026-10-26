using System.Runtime.CompilerServices;
using System.Text.Json;
using EOS.API.Data;
using EOS.API.Features.Assistant;
using EOS.API.Features.Assistant.ModelAccess;
using EOS.API.Features.Assistant.Parameters;
using EOS.API.Features.Assistant.Recording;
using EOS.API.Features.Assistant.Tools;
using Microsoft.Extensions.Logging.Abstractions;

namespace EOS.API.Tests.Parity;

/// <summary>
/// 用**真实的旧引擎**（<see cref="ChatService"/> 的多轮工具循环）回放一条 parity 用例，
/// 产出归一化结果供 Harness 侧与新引擎（<c>MafAgentEngine</c>）对比。
///
/// <para>
/// 跑的是生产代码，不是复刻：模型、仓储、工具分别用脚本化替身与桩，
/// 循环、收尾轮、摘要截断、回喂格式全部走 <see cref="ChatService"/> 本体。
/// 复刻一份"旧引擎"再比，比的只是复刻像不像。
/// </para>
/// </summary>
internal static class LegacyParityRunner
{
    public static Task<LegacyTurnOutcome> RunAsync(
        ParityCase parityCase,
        CancellationToken token = default) =>
        RunCoreAsync(parityCase, recorder: null, token);

    /// <summary>
    /// 与 <see cref="RunAsync"/> 同一条路径，但把模型 I/O 录进 <paramref name="recorder"/>：
    /// 用于"合成用例 → 真实引擎跑一遍 → 录成转录 → 再转回用例"的闭环验证。
    /// </summary>
    public static Task<LegacyTurnOutcome> RunWithRecordingAsync(
        ParityCase parityCase,
        AssistantTranscriptRecorder recorder,
        CancellationToken token = default) =>
        RunCoreAsync(parityCase, recorder, token);

    private static async Task<LegacyTurnOutcome> RunCoreAsync(
        ParityCase parityCase,
        AssistantTranscriptRecorder? recorder,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(parityCase);

        var sequence = new SequenceChatModel(parityCase.Calls, parityCase.ModelName);
        IChatModel model = sequence;
        if (recorder is not null)
        {
            model = new RecordingChatModel(sequence, recorder);
        }

        var tools = parityCase.Tools.Select(stub => (IAssistantTool)new StubTool(stub)).ToArray();
        var registry = new AssistantToolRegistry(tools);
        var repository = new ParityRepository();

        // 只把用例显式约束的旋钮（工具轮数）改掉，其余取生产默认值：
        // parity 要的是"同一批模型输出下旧引擎的真实行为"，不是一套特调参数。
        var policy = AssistantPolicyValues.Default with
        {
            Chat = new AssistantChatLimitsOptions { MaxToolRounds = parityCase.MaxToolRounds },
        };
        var settings = new AssistantSettings
        {
            SystemPrompt = parityCase.SystemPrompt,
            EnableAutoDistill = false,
        };

        var service = new ChatService(
            repository,
            model,
            registry,
            AssistantTestRuntime.Fixed(settings, policy),
            NullLogger<ChatService>.Instance);

        var events = new List<ChatStreamEvent>();
        await foreach (var streamEvent in service.StreamReplyAsync(
            "parity", 1, parityCase.UserInput, null, "parity", token))
        {
            events.Add(streamEvent);
        }

        var completed = events.OfType<ChatStreamEvent.Completed>().LastOrDefault();
        var failure = events.OfType<ChatStreamEvent.Failed>().LastOrDefault();
        var summaries = completed?.ToolCalls ?? [];

        return new LegacyTurnOutcome(
            parityCase.Name,
            "legacy",
            sequence.Calls,
            completed?.Message.Content ?? string.Empty,
            completed?.FinishReason,
            PairToolCalls(events, summaries),
            new LegacyOutcomeUsage(
                repository.LastPromptTokens ?? 0,
                repository.LastCompletionTokens ?? 0),
            sequence.RequestShapes,
            failure?.Code,
            failure?.Message);
    }

    /// <summary>
    /// 把工具摘要与成功标志配对。
    ///
    /// <para>
    /// 旧引擎的 <c>ToolCallSummary</c> 不带 Ok（Ok 只在流式事件里），而**只读工具会被并行执行**：
    /// <see cref="ChatStreamEvent.ToolFinished"/> 的顺序是完成顺序，与调用顺序无关。所以不能按位置配，
    /// 只能按 <c>(工具名, 结果摘要)</c> 配——两者同源于同一个 <c>ToolOutcome</c>，是精确的。
    /// 按位置配在并行下会随机错位，报出来的就是**假回归**。
    /// </para>
    /// </summary>
    private static IReadOnlyList<LegacyOutcomeToolCall> PairToolCalls(
        IReadOnlyList<ChatStreamEvent> events,
        IReadOnlyList<ToolCallSummary> summaries)
    {
        var finished = events.OfType<ChatStreamEvent.ToolFinished>().ToList();

        if (finished.Count != summaries.Count)
        {
            throw new InvalidOperationException(
                $"parity 用例超出本版支持范围：工具摘要 {summaries.Count} 条、完成事件 {finished.Count} 条。");
        }

        var calls = new List<LegacyOutcomeToolCall>(summaries.Count);
        foreach (var summary in summaries)
        {
            var index = finished.FindIndex(evt =>
                string.Equals(evt.Name, summary.Name, StringComparison.Ordinal)
                && string.Equals(evt.Digest, summary.ResultDigest, StringComparison.Ordinal));

            // 摘要被截断成一样长时可能重名重摘要：退回按名取第一条尚未消费的。
            if (index < 0)
            {
                index = finished.FindIndex(evt => string.Equals(evt.Name, summary.Name, StringComparison.Ordinal));
            }

            if (index < 0)
            {
                throw new InvalidOperationException(
                    $"parity 用例超出本版支持范围：找不到工具 {summary.Name} 的完成事件。");
            }

            var matched = finished[index];
            finished.RemoveAt(index);

            calls.Add(new LegacyOutcomeToolCall(
                summary.Name,
                summary.ArgumentsJson,
                summary.ResultDigest,
                matched.Ok));
        }

        return calls;
    }

    /// <summary>按序号回放模型输出，不校验请求（跨引擎对比时两端请求形状本就不同）。</summary>
    private sealed class SequenceChatModel(
        IReadOnlyList<ParityModelCall> calls,
        string modelName) : IChatModel
    {
        private readonly List<string> _requestShapes = [];
        private int _index;

        public string ModelName { get; } = modelName;

        public bool IsConfigured => true;

        public int Calls => _index;

        public IReadOnlyList<string> RequestShapes => _requestShapes;

        public async IAsyncEnumerable<ChatDelta> StreamAsync(
            IReadOnlyList<ChatMessage> messages,
            IReadOnlyList<ToolDefinition>? tools,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            _requestShapes.Add(Shape(messages, tools));

            if (_index >= calls.Count)
            {
                throw new InvalidOperationException(
                    $"回放：模型被调用了 {_index + 1} 次，但用例只脚本化了 {calls.Count} 次。");
            }

            var call = calls[_index];
            _index++;

            foreach (var delta in call.Output)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Yield();
                yield return delta;
            }
        }

        /// <summary>请求形状：角色序列 + 本轮下发的工具数（"最后一轮还发不发工具"只在这里看得见）。</summary>
        private static string Shape(
            IReadOnlyList<ChatMessage> messages,
            IReadOnlyList<ToolDefinition>? tools)
        {
            var roles = string.Join(',', messages.Select(message => message.Role.ToString()));
            return tools is null ? $"{roles} tools=none" : $"{roles} tools={tools.Count}";
        }
    }

    /// <summary>用例里的工具桩：声明逐字来自用例，执行结果固定。</summary>
    private sealed class StubTool(ParityToolStub stub) : IAssistantTool
    {
        public string Name => stub.Name;

        public AssistantToolRisk Risk => stub.Risk;

        public string Description => stub.Description;

        public string ParametersJson => stub.ParametersJson;

        public Task<ToolExecutionResult> ExecuteAsync(
            string userId, JsonElement arguments, CancellationToken token) =>
            Task.FromResult(stub.Ok
                ? ToolExecutionResult.Success(stub.ResultContent)
                : ToolExecutionResult.Deny(stub.ResultContent));
    }

    /// <summary>
    /// 最小仓储替身：只实现对话循环真正用到的几个成员（其余显式不支持）。
    ///
    /// <para>
    /// 历史按"先落用户消息、再读回历史"的真实顺序返回，这样 <c>BuildModelMessages</c>
    /// 拿到的输入与生产一致（system + 本轮用户消息）。
    /// </para>
    /// </summary>
    private sealed class ParityRepository : IAssistantRepository
    {
        private readonly List<(int Role, string Content)> _history = [];

        public int? LastPromptTokens { get; private set; }

        public int? LastCompletionTokens { get; private set; }

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
        {
            _history.Add((1, content));
            return Task.FromResult(new AssistantMessageDto(
                1, sessionId, 1, content, null, null, null, null, correlationId, DateTimeOffset.UtcNow));
        }

        public Task<AssistantMessageDto> AddAssistantMessageAsync(
            string userId, long sessionId, string content, string modelName,
            int? promptTokens, int? completionTokens, int? elapsedMs, string correlationId,
            CancellationToken token, bool estimated = false, string? finishReason = null)
        {
            LastPromptTokens = promptTokens;
            LastCompletionTokens = completionTokens;
            return Task.FromResult(new AssistantMessageDto(
                2, sessionId, 2, content, modelName, promptTokens, completionTokens, elapsedMs,
                correlationId, DateTimeOffset.UtcNow));
        }

        public Task<IReadOnlyList<(int Role, string Content)>> LoadRecentHistoryAsync(
            string userId, long sessionId, int maxMessages, CancellationToken token)
            => Task.FromResult<IReadOnlyList<(int Role, string Content)>>([.. _history]);

        public Task UpdateToolCallsJsonAsync(
            string userId, long messageId, string toolCallsJson, CancellationToken token)
            => Task.CompletedTask;

        public Task<int> SetMessageFeedbackAsync(
            string userId, long messageId, int? feedback, string? reason, CancellationToken token)
            => Task.FromResult(0);
    }
}
