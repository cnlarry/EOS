using EOS.API.Data;
using EOS.API.Features.Assistant.ModelAccess;
using Microsoft.Extensions.Options;

namespace EOS.API.Features.Assistant;

/// <summary>SSE 流事件：控制器按此映射为 event: delta|done|error。</summary>
public abstract record ChatStreamEvent
{
    public sealed record Delta(string Text) : ChatStreamEvent;
    public sealed record Completed(AssistantMessageDto Message) : ChatStreamEvent;
    public sealed record Failed(string Code, string Message) : ChatStreamEvent;
}

/// <summary>
/// 工作助手对话编排（ADR-007 §4）：归属校验 → 用户消息落库 → 组装上下文 → 流式调模型 →
/// 回复聚合落库。安全边界：模型只消费本服务组装的消息序列；客户端断开（OperationCanceledException）
/// 即中止出网调用并丢弃半截回复（不落库），用户消息保留、可重发。
/// </summary>
public sealed class ChatService(
    IAssistantRepository repository,
    IChatModel model,
    IOptions<AssistantSettings> settings,
    ILogger<ChatService> logger)
{
    /// <summary>单轮携带的最大历史条数（含双方消息），防上下文无限增长。</summary>
    private const int MaxHistoryMessages = 40;

    /// <summary>单条消息长度上限（字符）。</summary>
    public const int MaxContentLength = 8000;

    public async IAsyncEnumerable<ChatStreamEvent> StreamReplyAsync(
        string userId,
        long sessionId,
        string content,
        string correlationId,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
    {
        if (!model.IsConfigured)
        {
            yield return new ChatStreamEvent.Failed(
                "AI_MODEL_NOT_CONFIGURED", "工作助手尚未配置模型接入（Assistant:ApiKey），请联系管理员。");
            yield break;
        }

        if (string.IsNullOrWhiteSpace(content))
        {
            yield return new ChatStreamEvent.Failed("INVALID_ARGUMENT", "消息内容不能为空。");
            yield break;
        }

        content = content.Trim();
        if (content.Length > MaxContentLength)
        {
            yield return new ChatStreamEvent.Failed("INVALID_ARGUMENT", $"消息长度超过上限（{MaxContentLength} 字符）。");
            yield break;
        }

        // 归属校验 + 用户消息落库（SQL WHERE 内完成隔离，越权会话在此抛 UnauthorizedAccessException）。
        AssistantMessageDto userMessage;
        IReadOnlyList<(int Role, string Content)> history;
        bool sessionDenied = false;
        try
        {
            userMessage = await repository.AddUserMessageAsync(userId, sessionId, content, correlationId, token);
            history = await repository.LoadRecentHistoryAsync(userId, sessionId, MaxHistoryMessages, token);
        }
        catch (UnauthorizedAccessException)
        {
            sessionDenied = true;
            userMessage = null!;
            history = [];
        }

        if (sessionDenied)
        {
            yield return new ChatStreamEvent.Failed("NOT_FOUND", "会话不存在或不属于当前用户。");
            yield break;
        }

        _ = userMessage; // 已随请求持久化；SSE 只回传增量与收尾，前端本地即时渲染用户气泡

        var messages = BuildModelMessages(history);
        var reply = new System.Text.StringBuilder();
        ModelAccess.ChatUsage? usage = null;
        string? failureCode = null;
        string? failureMessage = null;

        var enumerator = model.StreamAsync(messages, token).GetAsyncEnumerator(token);
        while (true)
        {
            ModelAccess.ChatDelta delta;
            try
            {
                if (!await enumerator.MoveNextAsync()) break;
                delta = enumerator.Current;
            }
            catch (OperationCanceledException)
            {
                throw; // 客户端断开/超时：半截回复不落库，交由上层结束响应
            }
            catch (InvalidOperationException ex)
            {
                logger.LogWarning(ex, "助手模型调用失败（session={SessionId}）", sessionId);
                failureCode = "AI_MODEL_ERROR";
                failureMessage = "模型调用失败，请稍后重试。";
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "助手模型流异常（session={SessionId}）", sessionId);
                failureCode = "AI_MODEL_ERROR";
                failureMessage = "模型调用失败，请稍后重试。";
                break;
            }

            if (delta.Usage is not null)
            {
                usage = delta.Usage;
            }
            else if (!string.IsNullOrEmpty(delta.ContentDelta))
            {
                reply.Append(delta.ContentDelta);
                yield return new ChatStreamEvent.Delta(delta.ContentDelta!);
            }
        }

        if (failureCode is not null)
        {
            yield return new ChatStreamEvent.Failed(failureCode, failureMessage ?? "模型调用失败。");
            yield break;
        }

        var text = reply.ToString();
        if (string.IsNullOrEmpty(text))
        {
            yield return new ChatStreamEvent.Failed("AI_MODEL_EMPTY_REPLY", "模型没有返回内容，请重试。");
            yield break;
        }

        var saved = await repository.AddAssistantMessageAsync(
            userId, sessionId, text, model.ModelName,
            usage?.PromptTokens, usage?.CompletionTokens, usage?.ElapsedMs, correlationId, token);
        yield return new ChatStreamEvent.Completed(saved);
    }

    /// <summary>系统提示 + 最近历史（库内 ROLE 枚举转 ChatRole；未知角色跳过防脏数据入模）。</summary>
    private List<ChatMessage> BuildModelMessages(IReadOnlyList<(int Role, string Content)> history)
    {
        var messages = new List<ChatMessage>(history.Count + 1)
        {
            new(ChatRole.System, settings.Value.SystemPrompt),
        };
        foreach (var (role, content) in history)
        {
            var mapped = role switch
            {
                (int)ChatRole.User => ChatRole.User,
                (int)ChatRole.Assistant => ChatRole.Assistant,
                _ => (ChatRole?)null,
            };
            if (mapped is not null && !string.IsNullOrWhiteSpace(content))
            {
                messages.Add(new ChatMessage(mapped.Value, content));
            }
        }

        return messages;
    }
}
