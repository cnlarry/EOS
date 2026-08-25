using System.Diagnostics;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace EOS.API.Features.Assistant.ModelAccess;

/// <summary>
/// DeepSeek（OpenAI 兼容 /chat/completions）流式实现。
/// 只做出网调用与 SSE 解析：无权限语义、无落库、无提示词组装（ADR-007 §4）。
/// 密钥经 IOptions 注入；请求级取消贯穿到 HTTP 流读取，客户端断开即中止出网调用。
/// </summary>
public sealed class DeepSeekChatModel(
    IHttpClientFactory httpClientFactory,
    IOptions<AssistantSettings> settings) : IChatModel
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public string ModelName => settings.Value.Model;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(settings.Value.ApiKey);

    public async IAsyncEnumerable<ChatDelta> StreamAsync(
        IReadOnlyList<ChatMessage> messages,
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
                JsonSerializer.Serialize(new
                {
                    model = config.Model,
                    messages = messages.Select(m => new { role = m.Role switch
                    {
                        ChatRole.System => "system",
                        ChatRole.Assistant => "assistant",
                        _ => "user",
                    }, content = m.Content }),
                    stream = true,
                    stream_options = new { include_usage = true },
                }, JsonOptions),
                Encoding.UTF8,
                "application/json"),
        };

        var started = Stopwatch.StartNew();
        using var response = await client.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(linked.Token).ConfigureAwait(false);
            throw new InvalidOperationException(
                $"模型服务返回 {(int)response.StatusCode}：{Truncate(body, 300)}");
        }

        var stream = await response.Content.ReadAsStreamAsync(linked.Token).ConfigureAwait(false);
        await foreach (var delta in ParseSseAsync(stream, linked.Token).ConfigureAwait(false))
        {
            yield return delta;
        }
    }

    /// <summary>解析 OpenAI 兼容 SSE 帧：data:{json} 行 + data:[DONE] 收尾；usage 帧转 ChatUsage。</summary>
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
        if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array
            && choices.GetArrayLength() > 0)
        {
            var choice = choices[0];
            if (choice.TryGetProperty("delta", out var deltaEl)
                && deltaEl.TryGetProperty("content", out var contentEl)
                && contentEl.ValueKind == JsonValueKind.String)
            {
                text = contentEl.GetString();
            }
        }

        return text is null ? null : new ChatDelta(text, null);
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];
}
