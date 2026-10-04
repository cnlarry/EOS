using System.Text.Json;
using EOS.API.Data;
using EOS.API.Data.Assistant;
using Microsoft.Extensions.Logging;

namespace EOS.API.Features.Assistant.ModelAccess;

/// <summary>从厂商拉回来的一个候选模型：厂商给多少填多少，给不出的留空（不猜）。</summary>
public sealed record DiscoveredModel(
    string ModelCode,
    string? DisplayName = null,
    int? ContextWindow = null,
    int? MaxOutputTokens = null);

/// <summary>
/// 拉取结果：要么一串候选，要么一个**可读的失败原因**。
///
/// <para>
/// 失败**不返回空清单**——"这家确实没有可选模型"与"这次没拉到"给管理员的下一步完全不同：
/// 前者该手工填标识，后者该看密钥与网络。用一个空数组把两者混在一起，管理员只会反复点同一个按钮。
/// </para>
/// </summary>
public sealed record ModelDiscoveryResult(IReadOnlyList<DiscoveredModel> Models, string? Code, string? Message)
{
    public bool Ok => Code is null;

    public static ModelDiscoveryResult Success(IReadOnlyList<DiscoveredModel> models) => new(models, null, null);

    public static ModelDiscoveryResult Failure(string code, string message) => new([], code, message);
}

/// <summary>
/// 向厂商拉取可用模型清单（ADR-030 §12.3：型号清单不硬编码，由拉取得到；对话与嵌入共用同一机制）。
/// </summary>
public interface IAssistantModelDiscovery
{
    Task<ModelDiscoveryResult> DiscoverAsync(AssistantProviderRow provider, CancellationToken token);
}

/// <inheritdoc />
public sealed class AssistantModelDiscovery(
    IHttpClientFactory httpClientFactory,
    IAssistantSecretStore secrets,
    ILogger<AssistantModelDiscovery> logger) : IAssistantModelDiscovery
{
    /// <summary>密钥没配：发出去也只会换回一个 401，不如在这里把话说清。</summary>
    public const string NoKeyCode = "MODEL_DISCOVERY_NO_KEY";

    /// <summary>厂商拒了这把密钥（401/403）。</summary>
    public const string KeyRejectedCode = "MODEL_DISCOVERY_KEY_REJECTED";

    /// <summary>这家没有「列出模型」的端点（404/405）。</summary>
    public const string NotSupportedCode = "MODEL_DISCOVERY_NOT_SUPPORTED";

    /// <summary>网络、超时、5xx 之类的技术性失败。</summary>
    public const string FailedCode = "MODEL_DISCOVERY_FAILED";

    /// <summary>回来了但读不懂（不是预期的 JSON 形状）。</summary>
    public const string InvalidResponseCode = "MODEL_DISCOVERY_INVALID_RESPONSE";

    /// <inheritdoc />
    public async Task<ModelDiscoveryResult> DiscoverAsync(AssistantProviderRow provider, CancellationToken token)
    {
        var authStyle = AssistantProviderCatalog.AuthStyleOf(provider.Code);
        string? apiKey = null;
        if (authStyle != AssistantAuthStyle.None)
        {
            apiKey = secrets.Read(provider.ApiKeyEnvVar);
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                return ModelDiscoveryResult.Failure(NoKeyCode,
                    $"供应商「{provider.DisplayName}」的密钥还没配（环境变量 {provider.ApiKeyEnvVar} 为空）——"
                    + "先设置密钥再来拉取。");
            }
        }

        var url = $"{provider.BaseUrl.TrimEnd('/')}/models";
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(provider.TimeoutSeconds, 10, 3600)));

        try
        {
            // 复用既有的命名客户端：超时与代理这类传输设置只在一处配，不给拉取另开一条通路
            var client = httpClientFactory.CreateClient("AssistantModel");
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (apiKey is not null)
            {
                ApplyAuth(request, authStyle, apiKey);
            }

            using var response = await client.SendAsync(request, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(timeout.Token);
                logger.LogWarning("拉取模型清单失败 provider={Provider} status={Status}",
                    provider.Code, (int)response.StatusCode);
                return FailureForStatus(provider, (int)response.StatusCode, body);
            }

            return Parse(await response.Content.ReadAsStringAsync(timeout.Token));
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return ModelDiscoveryResult.Failure(FailedCode,
                $"请求 {provider.DisplayName} 超时（{provider.TimeoutSeconds} 秒内没有响应）。");
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "拉取模型清单出错 provider={Provider}", provider.Code);
            return ModelDiscoveryResult.Failure(FailedCode,
                $"连不上 {provider.DisplayName}（{provider.BaseUrl}）：{ex.Message}");
        }
    }

    /// <summary>
    /// 认证头按**供应商预设的样式**发：实测小米 MiMo 用的是 <c>api-key</c> 头而不是
    /// <c>Authorization: Bearer</c>——把样式写死在客户端里，那家就永远 401。
    /// </summary>
    private static void ApplyAuth(HttpRequestMessage request, AssistantAuthStyle style, string apiKey)
    {
        if (style == AssistantAuthStyle.ApiKeyHeader)
        {
            request.Headers.TryAddWithoutValidation("api-key", apiKey);
            return;
        }

        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {apiKey}");
    }

    /// <summary>
    /// 把状态码翻成可读原因。401/403 与 404/405 分开：处理方式不同（换密钥 vs 手工填标识），
    /// 合成一句"拉取失败"会让管理员在两个方向上都试一遍。
    /// </summary>
    internal static ModelDiscoveryResult FailureForStatus(AssistantProviderRow provider, int status, string? body)
    {
        var detail = string.IsNullOrWhiteSpace(body) ? string.Empty : $" 厂商返回：{Truncate(body.Trim())}";
        return status switch
        {
            401 or 403 => ModelDiscoveryResult.Failure(KeyRejectedCode,
                $"{provider.DisplayName} 拒绝了这把密钥（HTTP {status}），请检查密钥是否正确或已过期。{detail}"),
            404 or 405 => ModelDiscoveryResult.Failure(NotSupportedCode,
                $"{provider.DisplayName} 没有「列出模型」这个端点（HTTP {status}）——请手工填模型标识。{detail}"),
            _ => ModelDiscoveryResult.Failure(FailedCode,
                $"拉取失败：{provider.DisplayName} 返回 HTTP {status}。{detail}"),
        };
    }

    /// <summary>
    /// 解析厂商返回的模型清单。**按 OpenAI 兼容的最小子集读**（<c>data[].id</c>），
    /// 厂商多给的字段顺手用上（实测 DeepSeek 会带窗口与最大输出），不认识的一律忽略——
    /// 各家的扩展字段不同，按需读比按需校验更能活下来。
    /// </summary>
    internal static ModelDiscoveryResult Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            {
                return ModelDiscoveryResult.Failure(InvalidResponseCode,
                    "厂商返回的内容里没有 data 数组，读不懂它的模型清单（如实报失败，不退回预设型号）。");
            }

            var models = new List<DiscoveredModel>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in data.EnumerateArray())
            {
                var code = item.TryGetProperty("id", out var id) ? id.GetString()?.Trim() : null;
                // 没有标识的条目跳过（拿它落库会得到一个永远打不通的行）；重复的只留一条
                if (string.IsNullOrWhiteSpace(code) || !seen.Add(code))
                {
                    continue;
                }

                models.Add(new DiscoveredModel(
                    code,
                    ReadString(item, "name"),
                    ReadInt(item, "context_window"),
                    ReadInt(item, "max_output_tokens")));
            }

            // 厂商的返回顺序不稳定，按标识排序：管理员两次点出来的顺序一致，勾选时不容易看漏
            models.Sort((left, right) => string.CompareOrdinal(left.ModelCode, right.ModelCode));
            return ModelDiscoveryResult.Success(models);
        }
        catch (JsonException)
        {
            return ModelDiscoveryResult.Failure(InvalidResponseCode,
                "厂商返回的不是合法 JSON，读不懂它的模型清单。");
        }
    }

    private static string? ReadString(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim()
            : null;

    private static int? ReadInt(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var number)
                ? number
                : null;

    private static string Truncate(string text) => text.Length <= 200 ? text : text[..200] + "…";
}
