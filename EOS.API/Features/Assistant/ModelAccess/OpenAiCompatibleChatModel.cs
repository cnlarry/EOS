using System.Diagnostics;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace EOS.API.Features.Assistant.ModelAccess;

/// <summary>
/// OpenAI 兼容 <c>/chat/completions</c> 的流式实现（DeepSeek / OpenAI / 百炼 / 智谱 / Moonshot / 自定义同属一类）。
///
/// <para>
/// 名字按**协议**取，不按厂商取：目录里所有供应商走的都是同一套 HTTP 契约，
/// 用某一家给类命名会让人以为"换供应商要换客户端"。真正的差异（端点、模型标识、窗口、
/// 是否支持工具）都是数据，来自 3102 的供应商行与模型行；协议由 <see cref="AssistantProviderCatalog.ProtocolOf"/>
/// 决定，新增非兼容协议时在 <c>ResolvingChatModel</c> 里分派。
/// </para>
///
/// <para>
/// 只做出网调用与 SSE 解析：无权限语义、无落库、无提示词组装、不执行工具。
/// 失败一律归一成 <see cref="AssistantModelException"/>（类别决定文案与是否熔断），
/// 不在这里决定"怎么告诉用户"。
/// </para>
/// </summary>
public sealed class OpenAiCompatibleChatModel(
    IHttpClientFactory httpClientFactory,
    IOptions<AssistantSettings> settings) : IChatModel
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public string ModelName => settings.Value.Model;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(settings.Value.ApiKey);

    public async IAsyncEnumerable<ChatDelta> StreamAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolDefinition>? tools,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var config = settings.Value;
        if (string.IsNullOrWhiteSpace(config.ApiKey))
        {
            throw new InvalidOperationException("Assistant:ApiKey 未配置。");
        }

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(30, config.TimeoutSeconds)));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        var client = httpClientFactory.CreateClient("AssistantModel");
        client.BaseAddress = new Uri(config.BaseUrl.TrimEnd('/') + "/");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", config.ApiKey);
        // 流式全程不适用 HttpClient.Timeout（默认 100s 会掐断长回复），由 timeoutCts 控制总时长。
        client.Timeout = Timeout.InfiniteTimeSpan;

        using var request = new HttpRequestMessage(HttpMethod.Post, "chat/completions")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(BuildPayload(config, messages, tools), JsonOptions),
                Encoding.UTF8,
                "application/json"),
        };

        var started = Stopwatch.StartNew();
        using var response = await client.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(linked.Token).ConfigureAwait(false);
            throw FromResponse((int)response.StatusCode, body);
        }

        var stream = await response.Content.ReadAsStreamAsync(linked.Token).ConfigureAwait(false);
        await foreach (var delta in ParseSseAsync(stream, linked.Token).ConfigureAwait(false))
        {
            yield return delta;
        }
    }

    /// <summary>
    /// 把厂商响应归一成失败类别。状态码先判一轮，认不出来时按错误体特征再判一次——
    /// 同一件事各家用的码不一样（余额不足有 402、也有 400；超上下文有 413、也有 400），
    /// 只认状态码会让一半的失败落进"未识别"，而这几类恰好都有明确去处。
    /// </summary>
    internal static AssistantModelException FromResponse(int status, string body)
    {
        var lower = body.ToLowerInvariant();
        var kind = status switch
        {
            401 or 403 => AssistantModelErrorKind.Unauthorized,
            402 => AssistantModelErrorKind.InsufficientBalance,
            429 => AssistantModelErrorKind.RateLimited,
            413 => AssistantModelErrorKind.ContextLengthExceeded,
            >= 500 => AssistantModelErrorKind.ProviderUnavailable,
            _ => AssistantModelErrorKind.Unknown,
        };

        if (kind is AssistantModelErrorKind.Unknown or AssistantModelErrorKind.RateLimited)
        {
            if (ContainsAny(lower, "insufficient_quota", "insufficient balance", "余额不足", "quota exceeded",
                    "exceeded your current quota", "account balance"))
            {
                kind = AssistantModelErrorKind.InsufficientBalance;
            }
            else if (ContainsAny(lower, "context length", "context_length", "maximum context", "too many tokens",
                         "reduce the length", "max_tokens", "token limit"))
            {
                kind = AssistantModelErrorKind.ContextLengthExceeded;
            }
        }

        return new AssistantModelException(kind, $"模型服务返回 {status}：{Truncate(body, 300)}");
    }

    private static bool ContainsAny(string haystack, params string[] needles) =>
        needles.Any(needle => haystack.Contains(needle, StringComparison.Ordinal));

    internal static Dictionary<string, object?> BuildPayload(
        AssistantSettings config,
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolDefinition>? tools)
    {
        var payload = new Dictionary<string, object?>
        {
            ["model"] = config.Model,
            ["messages"] = messages.Select(SerializeMessage).ToList(),
            ["stream"] = true,
            ["stream_options"] = new { include_usage = true },
        };
        // 只在配了才带上：传 null 会被部分厂商当成非法值，而"不传"才是各家一致认可的"用默认"
        if (config.Temperature is { } temperature)
        {
            payload["temperature"] = temperature;
        }

        if (config.MaxTokens is { } maxTokens)
        {
            payload["max_tokens"] = maxTokens;
        }
        // 模型不支持工具调用时**不能**带 tools 过去：厂商会直接报参数不合法，而错误信息里看不出
        // 真正的原因是"这个模型不支持工具"。能力来自模型行上的 SUPPORTS_TOOLS。
        if (config.SupportsTools && tools is { Count: > 0 })
        {
            payload["tools"] = tools.Select(t => new
            {
                type = "function",
                function = new { name = t.Name, description = t.Description, parameters = JsonDocument.Parse(t.ParametersJson).RootElement },
            }).ToList();
        }

        return payload;
    }

    private static Dictionary<string, object?> SerializeMessage(ChatMessage message) => message.Role switch
    {
        ChatRole.Assistant when message.ToolCalls is { Count: > 0 } => new Dictionary<string, object?>
        {
            ["role"] = "assistant",
            ["content"] = string.IsNullOrEmpty(message.Content) ? null : message.Content,
            ["tool_calls"] = message.ToolCalls.Select(tc => new
            {
                id = tc.Id,
                type = "function",
                function = new { name = tc.Name, arguments = tc.ArgumentsJson },
            }).ToList(),
        },
        ChatRole.Tool => new Dictionary<string, object?>
        {
            ["role"] = "tool",
            ["tool_call_id"] = message.ToolCallId ?? throw new ArgumentException("工具结果消息缺少 ToolCallId。"),
            ["content"] = message.Content,
        },
        _ => new Dictionary<string, object?>
        {
            ["role"] = message.Role switch
            {
                ChatRole.System => "system",
                ChatRole.Assistant => "assistant",
                ChatRole.Tool => "tool",
                _ => "user",
            },
            ["content"] = message.Content,
        },
    };

    /// <summary>解析 OpenAI 兼容 SSE 帧：data:{json} 行 + data:[DONE] 收尾；usage/tool_calls 帧转结构化增量。</summary>
    private static async IAsyncEnumerable<ChatDelta> ParseSseAsync(
        Stream stream, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var elapsed = Stopwatch.StartNew();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null) break;
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            var payload = line["data:".Length..].Trim();
            if (payload.Length == 0) continue;
            if (payload == "[DONE]") yield break;

            ChatDelta? delta;
            try
            {
                delta = ParseChunk(payload, elapsed.ElapsedMilliseconds);
            }
            catch (JsonException)
            {
                continue; // 单帧损坏跳过，不中断整个流
            }

            if (delta is not null) yield return delta;
        }
    }

    private static ChatDelta? ParseChunk(string json, long elapsedMs)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
        {
            return new ChatDelta(
                null,
                new ChatUsage(
                    usage.TryGetProperty("prompt_tokens", out var p) ? p.GetInt32() : 0,
                    usage.TryGetProperty("completion_tokens", out var c) ? c.GetInt32() : 0,
                    (int)elapsedMs));
        }

        string? text = null;
        string? reasoning = null;
        List<ProposedToolCallFragment>? fragments = null;
        string? finishReason = null;
        if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array
            && choices.GetArrayLength() > 0)
        {
            var choice = choices[0];
            if (choice.TryGetProperty("delta", out var deltaEl))
            {
                if (deltaEl.TryGetProperty("content", out var contentEl)
                    && contentEl.ValueKind == JsonValueKind.String)
                {
                    text = contentEl.GetString();
                }

                // 推理内容各家字段名不同（DeepSeek 用 reasoning_content，部分兼容实现用 reasoning）。
                // 它不进后续上下文，仅透传给界面：丢掉等于"推理 token 已付费、用户看不到"。
                if (deltaEl.TryGetProperty("reasoning_content", out var reasoningEl)
                    && reasoningEl.ValueKind == JsonValueKind.String)
                {
                    reasoning = reasoningEl.GetString();
                }
                else if (deltaEl.TryGetProperty("reasoning", out var reasoningAlt)
                    && reasoningAlt.ValueKind == JsonValueKind.String)
                {
                    reasoning = reasoningAlt.GetString();
                }

                if (deltaEl.TryGetProperty("tool_calls", out var callsEl)
                    && callsEl.ValueKind == JsonValueKind.Array)
                {
                    fragments = [];
                    foreach (var call in callsEl.EnumerateArray())
                    {
                        int index = call.TryGetProperty("index", out var idxEl) ? idxEl.GetInt32() : 0;
                        string? id = call.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String ? idEl.GetString() : null;
                        string? name = null;
                        string? argsFragment = null;
                        if (call.TryGetProperty("function", out var fnEl) && fnEl.ValueKind == JsonValueKind.Object)
                        {
                            if (fnEl.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String)
                            {
                                name = nameEl.GetString();
                            }

                            if (fnEl.TryGetProperty("arguments", out var argsEl) && argsEl.ValueKind == JsonValueKind.String)
                            {
                                argsFragment = argsEl.GetString();
                            }
                        }

                        fragments.Add(new ProposedToolCallFragment(index, id, name, argsFragment));
                    }
                }
            }

            if (choice.TryGetProperty("finish_reason", out var finishEl) && finishEl.ValueKind == JsonValueKind.String)
            {
                finishReason = finishEl.GetString();
            }
        }

        if (text is null && fragments is null && finishReason is null && reasoning is null) return null;
        return new ChatDelta(text, null, fragments, finishReason, reasoning);
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];
}
