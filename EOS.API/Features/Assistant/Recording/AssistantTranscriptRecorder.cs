using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using EOS.API.Features.Assistant.ModelAccess;
using Microsoft.Extensions.Options;

namespace EOS.API.Features.Assistant.Recording;

/// <summary>一次模型调用的录制：发出去的消息、下发的工具声明、收到的分片。</summary>
internal sealed record TranscriptCall(
    IReadOnlyList<TranscriptMessage> Messages,
    IReadOnlyList<TranscriptTool>? Tools,
    IReadOnlyList<TranscriptDelta> Output);

/// <summary>一条进入模型的消息（角色按字符串存，便于直接读文件）。</summary>
internal sealed record TranscriptMessage(
    string Role,
    string Content,
    IReadOnlyList<TranscriptToolCall>? ToolCalls = null,
    string? ToolCallId = null);

/// <summary>回喂用的完整工具调用。</summary>
internal sealed record TranscriptToolCall(string Id, string Name, string ArgumentsJson);

/// <summary>下发给模型的工具声明（含描述里的风险前缀与上限说明）。</summary>
internal sealed record TranscriptTool(string Name, string Description, string ParametersJson);

/// <summary>一个流式分片。</summary>
internal sealed record TranscriptDelta(
    string? Content,
    string? Reasoning,
    IReadOnlyList<TranscriptToolFragment>? ToolCalls,
    string? FinishReason,
    TranscriptUsage? Usage);

/// <summary>工具调用分片（arguments 按 Index 顺序拼接）。</summary>
internal sealed record TranscriptToolFragment(int Index, string? Id, string? Name, string? Arguments);

/// <summary>一次调用的用量收尾帧。</summary>
internal sealed record TranscriptUsage(int PromptTokens, int CompletionTokens, int ElapsedMs);

/// <summary>一个回合的完整录制。</summary>
internal sealed record TranscriptTurn(
    string ModelName,
    DateTimeOffset CapturedAt,
    IReadOnlyList<TranscriptCall> Calls);

/// <summary>
/// 把当前请求内发生的模型调用累积成**一个回合**，并在回合结束时落盘。
///
/// <para>
/// **回合边界不靠调用方通知**（那要在 <c>ChatService</c> 里插钩子），而是从请求形状判定：
/// 一条对话回合的每次调用都以 System 消息开头；回合的**首次**调用末尾是本次的用户消息。
/// 于是：以 System 开头 + 末尾是 User ⇒ 新回合开始（先把上一轮落盘）。
/// 记忆提炼那类调用没有 System 消息，直接不录。
/// </para>
///
/// <para>
/// **任何失败都不许影响对话**：写盘、序列化、目录创建全部 try/catch，只记 Warning
/// （与"工具摘要回填失败只降级"同一口径）。录制是旁路，不是功能。
/// </para>
/// </summary>
public sealed class AssistantTranscriptRecorder(
    IOptions<AssistantTranscriptSettings> options,
    string contentRootPath,
    ILogger<AssistantTranscriptRecorder> logger) : IDisposable
{
    private static int _turnsWritten;

    private readonly AssistantTranscriptSettings _settings = options.Value;
    private readonly List<TranscriptCall> _calls = [];
    private string? _modelName;
    private bool _disposed;

    /// <summary>是否已写满 <see cref="AssistantTranscriptSettings.MaxTurns"/>（写满后不再落盘）。</summary>
    public bool Exhausted => Volatile.Read(ref _turnsWritten) >= _settings.MaxTurns;

    /// <summary>记一次已完成的模型调用。非对话回合（无 System 消息）不录。</summary>
    public void Capture(
        string modelName,
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolDefinition>? tools,
        IReadOnlyList<ChatDelta> output)
    {
        if (messages.Count == 0 || messages[0].Role != ChatRole.System)
        {
            // 记忆提炼这类"非对话"调用：它不在同一段对话上下文里，混进来会把回合拆错。
            return;
        }

        if (messages[^1].Role == ChatRole.User)
        {
            Flush(); // 上一回合到此为止
        }

        _modelName ??= modelName;

        _calls.Add(new TranscriptCall(
            [.. messages.Select(ToMessage)],
            tools is null ? null : [.. tools.Select(tool => new TranscriptTool(tool.Name, tool.Description, tool.ParametersJson))],
            [.. output.Select(ToDelta)]));
    }

    /// <summary>把累积的调用写成一份文件；没有调用时什么也不做。</summary>
    public void Flush()
    {
        if (_calls.Count == 0)
        {
            return;
        }

        var calls = _calls.ToArray();
        _calls.Clear();

        try
        {
            if (Exhausted)
            {
                return;
            }

            var directory = Path.Combine(contentRootPath, _settings.Directory);
            System.IO.Directory.CreateDirectory(directory);

            var turn = new TranscriptTurn(
                ModelName: _modelName ?? string.Empty,
                CapturedAt: DateTimeOffset.Now,
                Calls: calls);

            var path = Path.Combine(
                directory,
                $"turn-{DateTime.Now:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid().ToString("N")[..8]}.json");
            File.WriteAllText(path, JsonSerializer.Serialize(turn, TranscriptJson.Options));

            var written = Interlocked.Increment(ref _turnsWritten);
            logger.LogInformation(
                "助手回合已录制（{Calls} 次模型调用）→ {Path}；本进程累计 {Written}/{Max} 轮",
                calls.Length, path, written, _settings.MaxTurns);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 录制失败只降级：它不参与对话，也没有任何业务后果。
            logger.LogWarning(ex, "助手回合录制失败（已忽略，不影响对话）");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Flush();
    }

    private static TranscriptMessage ToMessage(ChatMessage message) => new(
        Role: message.Role.ToString().ToLowerInvariant(),
        Content: message.Content,
        ToolCalls: message.ToolCalls is null
            ? null
            : [.. message.ToolCalls.Select(call => new TranscriptToolCall(call.Id, call.Name, call.ArgumentsJson))],
        ToolCallId: message.ToolCallId);

    private static TranscriptDelta ToDelta(ChatDelta delta) => new(
        Content: delta.ContentDelta,
        Reasoning: delta.ReasoningDelta,
        ToolCalls: delta.ToolCallDeltas is null
            ? null
            : [.. delta.ToolCallDeltas.Select(fragment =>
                new TranscriptToolFragment(fragment.Index, fragment.Id, fragment.Name, fragment.ArgumentsFragment))],
        FinishReason: delta.FinishReason,
        Usage: delta.Usage is null
            ? null
            : new TranscriptUsage(delta.Usage.PromptTokens, delta.Usage.CompletionTokens, delta.Usage.ElapsedMs));
}

/// <summary>录制文件的序列化口径：camelCase、中文不转义（文件要能直接读）。</summary>
internal static class TranscriptJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
