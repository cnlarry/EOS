using System.Text;
using System.Text.Json;

namespace EOS.API.Features.Assistant.ModelAccess;

/// <summary>
/// OpenAI 兼容 <c>POST /v1/embeddings</c> 的实现（百炼 / 智谱 / OpenAI / 自建 TEI 同属一类）。
///
/// <para>
/// 名字按**协议**取，不按厂商取：与 <see cref="OpenAiCompatibleChatModel"/> 同一理由——
/// 端点、型号、维度、认证样式全是数据，换供应商不该换客户端。
/// </para>
///
/// <para>
/// 只做出网与解析：失败一律归一成 <see cref="AssistantModelException"/>
/// （与对话**同一张映射表**，见 <see cref="OpenAiCompatibleChatModel.FromResponse"/>），
/// 由调用方决定"怎么告诉用户"。
/// </para>
/// </summary>
public sealed class OpenAiCompatibleEmbeddingModel(
    IHttpClientFactory httpClientFactory,
    AssistantEmbeddingConfig config,
    ILogger<OpenAiCompatibleEmbeddingModel> logger) : IEmbeddingModel
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <inheritdoc />
    public string ModelId => config.ModelId;

    /// <inheritdoc />
    public int Dimension => config.Dimension;

    /// <inheritdoc />
    public async Task<float[]> EmbedAsync(string text, CancellationToken token)
    {
        var vectors = await EmbedManyAsync([text], token);
        return vectors[0];
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<float[]>> EmbedManyAsync(IReadOnlyList<string> texts, CancellationToken token)
    {
        if (texts.Count == 0)
        {
            return [];
        }

        var vectors = new List<float[]>(texts.Count);
        foreach (var batch in Batch(texts, config.BatchMax))
        {
            vectors.AddRange(await EmbedBatchAsync(batch, token));
        }

        return vectors;
    }

    private async Task<IReadOnlyList<float[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken token)
    {
        // 超时按供应商/模型配置，与拉取型号那条非流式链路同一口径（10–3600 秒由 3102 的校验保证）
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(config.TimeoutSeconds, 10, 3600)));

        // 复用既有的命名客户端：超时与代理只在 Program.cs 一处配，不给嵌入另开一条通路
        var client = httpClientFactory.CreateClient("AssistantModel");
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{config.BaseUrl.TrimEnd('/')}/embeddings")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(BuildPayload(config, texts), JsonOptions), Encoding.UTF8, "application/json"),
        };
        ApplyAuth(request, config.AuthStyle, config.ApiKey);

        try
        {
            using var response = await client.SendAsync(request, timeout.Token);
            var body = await response.Content.ReadAsStringAsync(timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("嵌入调用失败 provider={Provider} model={Model} status={Status}",
                    config.ProviderDisplayName, config.ModelCode, (int)response.StatusCode);
                // 映射表只留一处：分成两份，早晚会出现"同一个 402 在这边叫余额不足、在那边叫未识别"
                throw OpenAiCompatibleChatModel.FromResponse((int)response.StatusCode, body);
            }

            return Parse(body, texts.Count, config.Dimension);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new AssistantModelException(AssistantModelErrorKind.Timeout,
                $"嵌入端点 {config.TimeoutSeconds} 秒内没有响应（{config.ProviderDisplayName}）");
        }
        catch (HttpRequestException ex)
        {
            throw new AssistantModelException(AssistantModelErrorKind.ProviderUnavailable,
                $"连不上嵌入端点（{config.BaseUrl}）：{ex.Message}", ex);
        }
    }

    /// <summary>
    /// 认证头按**供应商预设的样式**发（与拉取型号同一处口径）：实测小米 MiMo 用的是 <c>api-key</c>
    /// 头而不是 <c>Authorization: Bearer</c>。密钥为空时**不带任何认证头**——无凭据端点
    /// （本机/内网自建服务）是合法配置，硬塞一个空 Bearer 会让它 401。
    /// </summary>
    private static void ApplyAuth(HttpRequestMessage request, AssistantAuthStyle style, string? apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return;
        }

        if (style == AssistantAuthStyle.ApiKeyHeader)
        {
            request.Headers.TryAddWithoutValidation("api-key", apiKey);
            return;
        }

        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {apiKey}");
    }

    /// <summary>
    /// 请求体。<c>dimensions</c> **总是显式带上**：智谱 <c>embedding-3</c> 默认 2048、
    /// OpenAI <c>3-small</c> 默认 1536——不指定就会拿回一个与集合列宽不符的向量，
    /// 而那时报出来的原因是"维度不符"，看起来像配置写错了，实际是"没告诉它要几维"。
    ///
    /// <para>
    /// 自建端点若不认这个字段，厂商会直接报参数错——错误体原样带出来（比静默按默认维度返回强）。
    /// </para>
    /// </summary>
    internal static Dictionary<string, object?> BuildPayload(
        AssistantEmbeddingConfig config, IReadOnlyList<string> texts) => new()
    {
        ["model"] = config.ModelCode,
        ["input"] = texts.ToList(),
        ["dimensions"] = config.Dimension,
    };

    /// <summary>
    /// 按厂商的单请求上限切片。上限**至少 1**：配成 0 或负数会切出一个空批次死循环，
    /// 而那种配置不该让整条链路卡住。
    /// </summary>
    internal static IReadOnlyList<IReadOnlyList<string>> Batch(IReadOnlyList<string> texts, int batchMax)
    {
        var size = Math.Max(1, batchMax);
        var batches = new List<IReadOnlyList<string>>();
        for (var start = 0; start < texts.Count; start += size)
        {
            batches.Add(texts.Skip(start).Take(size).ToList());
        }

        return batches;
    }

    /// <summary>
    /// 解析响应并**逐条验维度**。两件事都不能省：
    ///
    /// <para>
    /// ① 条数：少一条就会把后一条的向量配到前一条的内容上（入库后表现为"检索答非所问"，无从追查）；
    /// ② 维度：与集合列宽不符时，入库会在**另一个地方**报"维度不符"，而那里的信息量不足以指出
    /// "厂商没按 dimensions 返回"。
    /// </para>
    ///
    /// <para>顺序按 <c>index</c> 校正（厂商可能重排），没有 <c>index</c> 的按自然顺序。</para>
    /// </summary>
    internal static IReadOnlyList<float[]> Parse(string json, int expectedCount, int expectedDimension)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new AssistantModelException(AssistantModelErrorKind.Unknown,
                "嵌入端点返回的不是合法 JSON，读不懂它的响应。", ex);
        }

        using (document)
        {
            if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            {
                throw new AssistantModelException(AssistantModelErrorKind.Unknown,
                    "嵌入端点的响应里没有 data 数组，读不懂它的响应。");
            }

            var items = new List<(int Index, float[] Vector)>();
            var position = 0;
            foreach (var item in data.EnumerateArray())
            {
                var index = item.TryGetProperty("index", out var indexEl) && indexEl.TryGetInt32(out var parsed)
                    ? parsed
                    : position;
                if (!item.TryGetProperty("embedding", out var vectorEl) || vectorEl.ValueKind != JsonValueKind.Array)
                {
                    throw new AssistantModelException(AssistantModelErrorKind.Unknown,
                        $"嵌入响应里第 {position + 1} 条没有 embedding 数组。");
                }

                var vector = new float[vectorEl.GetArrayLength()];
                var i = 0;
                foreach (var value in vectorEl.EnumerateArray())
                {
                    vector[i++] = value.GetSingle();
                }

                if (vector.Length != expectedDimension)
                {
                    throw new AssistantModelException(AssistantModelErrorKind.Unknown,
                        $"嵌入维度不符：模型行上写的是 {expectedDimension} 维，端点返回 {vector.Length} 维。"
                        + "请检查该模型是否支持指定维度（本系统请求里带了 dimensions），"
                        + "并把模型行上的维度改成与知识库集合登记一致的值。");
                }

                items.Add((index, vector));
                position++;
            }

            if (items.Count != expectedCount)
            {
                throw new AssistantModelException(AssistantModelErrorKind.Unknown,
                    $"嵌入返回条数与请求不符：请求 {expectedCount} 条，返回 {items.Count} 条。"
                    + "条数对不上会把向量与内容错位配对，因此整批作废。");
            }

            return items.OrderBy(item => item.Index).Select(item => item.Vector).ToList();
        }
    }
}
