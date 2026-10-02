using System.Net;
using System.Text.Json;
using EOS.API.Features.Assistant.ModelAccess;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 嵌入客户端（OpenAI 兼容 <c>/v1/embeddings</c>）的离线用例。
///
/// <para>
/// 分两层：**纯函数**（请求体 / 分批 / 响应解析）直接断言；**出网那一层**用假的
/// <see cref="HttpMessageHandler"/> 断言"实际发出去的请求长什么样"——URL、认证头、请求体。
/// 这三样错了都不会在本地报错：URL 错了是 404、认证头错了是 401、请求体少了 <c>dimensions</c>
/// 是"维度不符"，而这三条恰好都只有真连厂商才会暴露。
/// </para>
/// </summary>
public sealed class AssistantEmbeddingModelTests
{
    private static AssistantEmbeddingConfig Config(
        int dimension = 1024,
        string? apiKey = "sk-test-key",
        AssistantAuthStyle authStyle = AssistantAuthStyle.Bearer,
        int batchMax = 10,
        int timeoutSeconds = 300) => new(
            ModelId: "text-embedding-v4",
            ProviderDisplayName: "阿里云百炼（通义千问）",
            BaseUrl: "https://dashscope.aliyuncs.com/compatible-mode/v1",
            ApiKey: apiKey,
            ModelCode: "text-embedding-v4",
            Dimension: dimension,
            AuthStyle: authStyle,
            TimeoutSeconds: timeoutSeconds,
            BatchMax: batchMax);

    private static string VectorJson(int dimension, params string[] values)
    {
        var vector = values.Length > 0 ? values : Enumerable.Repeat("0.5", dimension).ToArray();
        return "[" + string.Join(",", vector) + "]";
    }

    private static string Response(params (int Index, string Vector)[] items) =>
        JsonSerializer.Serialize(new
        {
            @object = "list",
            data = items.OrderByDescending(item => item.Index).Select(item => new
            {
                @object = "embedding",
                index = item.Index,
                embedding = JsonSerializer.Deserialize<float[]>(item.Vector),
            }),
            model = "text-embedding-v4",
        });

    // ---- 请求体 ----

    [Fact]
    public void Payload_Carries_Model_Input_And_Dimension()
    {
        var payload = OpenAiCompatibleEmbeddingModel.BuildPayload(Config(), ["甲", "乙"]);

        Assert.Equal("text-embedding-v4", payload["model"]);
        Assert.Equal(["甲", "乙"], Assert.IsType<List<string>>(payload["input"]));
        // dimensions 必须显式带上：智谱默认 2048、OpenAI 默认 1536/3072，
        // 不指定就拿回一个与集合列宽不符的向量（报出来还是"维度不符"，看不出是没指定）
        Assert.Equal(1024, payload["dimensions"]);
    }

    [Fact]
    public void Batch_Splits_By_Vendor_Limit_And_Never_Produces_Empty_Batch()
    {
        var texts = Enumerable.Range(1, 25).Select(index => $"块{index}").ToList();

        var batches = OpenAiCompatibleEmbeddingModel.Batch(texts, 10);
        Assert.Equal(3, batches.Count);
        Assert.Equal(10, batches[0].Count);
        Assert.Equal(10, batches[1].Count);
        Assert.Equal(5, batches[2].Count);
        // 顺序不能乱：切片错了会把向量与内容错位配对
        Assert.Equal("块1", batches[0][0]);
        Assert.Equal("块25", batches[2][4]);

        // 上限配成 0/负数时按 1 处理：切出空批次会在循环里打转（那不是配置错误该有的表现）
        Assert.Single(OpenAiCompatibleEmbeddingModel.Batch(["只有一条"], 0));
        Assert.Empty(OpenAiCompatibleEmbeddingModel.Batch([], 10));
    }

    // ---- 响应解析 ----

    [Fact]
    public void Parse_Orders_By_Index_Even_If_Vendor_Reorders()
    {
        // 厂商把 1 号放在前面：顺序不校正就会把第二块的向量配到第一块的内容上
        var json = Response((1, VectorJson(4, "1", "1", "1", "1")), (0, VectorJson(4, "0", "0", "0", "0")));

        var vectors = OpenAiCompatibleEmbeddingModel.Parse(json, expectedCount: 2, expectedDimension: 4);

        Assert.Equal(2, vectors.Count);
        Assert.All(vectors[0], value => Assert.Equal(0f, value));
        Assert.All(vectors[1], value => Assert.Equal(1f, value));
    }

    [Fact]
    public void Parse_Rejects_Dimension_Mismatch_With_Both_Numbers()
    {
        var json = Response((0, VectorJson(3)));

        var error = Assert.Throws<AssistantModelException>(
            () => OpenAiCompatibleEmbeddingModel.Parse(json, expectedCount: 1, expectedDimension: 1024));

        // 两个数字都要在话里：只说"维度不符"，读的人不知道是模型行写错了还是厂商没照做
        Assert.Contains("1024", error.Message, StringComparison.Ordinal);
        Assert.Contains("3", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_Rejects_Count_Mismatch()
    {
        var json = Response((0, VectorJson(4)));

        var error = Assert.Throws<AssistantModelException>(
            () => OpenAiCompatibleEmbeddingModel.Parse(json, expectedCount: 3, expectedDimension: 4));

        Assert.Contains("条数", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("不是 JSON")]
    [InlineData("{}")]
    [InlineData("{\"data\":{}}")]
    public void Parse_Rejects_What_It_Cannot_Read(string json)
    {
        Assert.Throws<AssistantModelException>(
            () => OpenAiCompatibleEmbeddingModel.Parse(json, expectedCount: 1, expectedDimension: 4));
    }

    // ---- 出网那一层：请求长什么样 ----

    [Fact]
    public async Task Sends_To_Embeddings_With_Bearer_And_Dimension()
    {
        var handler = new RecordingHandler(_ => JsonResponse(Response((0, VectorJson(1024)))));
        var model = new OpenAiCompatibleEmbeddingModel(
            new StubHttpClientFactory(handler), Config(), NullLogger<OpenAiCompatibleEmbeddingModel>.Instance);

        var vector = await model.EmbedAsync("甲", CancellationToken.None);

        Assert.Equal(1024, vector.Length);
        Assert.Equal("https://dashscope.aliyuncs.com/compatible-mode/v1/embeddings",
            handler.LastRequest!.RequestUri!.ToString());
        Assert.Equal("Bearer", handler.LastRequest.Headers.Authorization!.Scheme);
        Assert.Equal("sk-test-key", handler.LastRequest.Headers.Authorization.Parameter);
        using var body = JsonDocument.Parse(handler.LastBody!);
        Assert.Equal(1024, body.RootElement.GetProperty("dimensions").GetInt32());
        Assert.Equal("甲", body.RootElement.GetProperty("input")[0].GetString());
    }

    [Fact]
    public async Task Uses_Provider_Auth_Style_And_Skips_Header_When_No_Credential()
    {
        // 认证头样式来自预设数据：写死 Bearer 会让用 api-key 头的那家永远 401
        var headerHandler = new RecordingHandler(_ => JsonResponse(Response((0, VectorJson(4)))));
        await new OpenAiCompatibleEmbeddingModel(new StubHttpClientFactory(headerHandler),
                Config(dimension: 4, authStyle: AssistantAuthStyle.ApiKeyHeader),
                NullLogger<OpenAiCompatibleEmbeddingModel>.Instance)
            .EmbedAsync("甲", CancellationToken.None);
        Assert.Equal("sk-test-key", headerHandler.LastRequest!.Headers.GetValues("api-key").Single());
        Assert.Null(headerHandler.LastRequest.Headers.Authorization);

        // 无凭据端点（自机/内网）：不带任何认证头，也不能硬塞一个空 Bearer
        var openHandler = new RecordingHandler(_ => JsonResponse(Response((0, VectorJson(4)))));
        await new OpenAiCompatibleEmbeddingModel(new StubHttpClientFactory(openHandler),
                Config(dimension: 4, apiKey: null), NullLogger<OpenAiCompatibleEmbeddingModel>.Instance)
            .EmbedAsync("甲", CancellationToken.None);
        Assert.Null(openHandler.LastRequest!.Headers.Authorization);
    }

    [Fact]
    public async Task Batches_Requests_And_Keeps_Order()
    {
        var handler = new RecordingHandler(request =>
        {
            using var body = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            var inputs = body.RootElement.GetProperty("input").EnumerateArray()
                .Select(item => item.GetString()!).ToList();
            // 每条文本回一个可辨认的向量：第 n 条的第 0 位就是 n，用来验顺序没乱
            var items = inputs.Select((text, index) => (index, VectorJson(4, $"{text.Length}", "0", "0", "0")));
            return JsonResponse(Response([.. items]));
        });
        var model = new OpenAiCompatibleEmbeddingModel(
            new StubHttpClientFactory(handler), Config(dimension: 4, batchMax: 10),
            NullLogger<OpenAiCompatibleEmbeddingModel>.Instance);

        var vectors = await model.EmbedManyAsync(
            [.. Enumerable.Range(1, 25).Select(index => new string('x', index))], CancellationToken.None);

        Assert.Equal(25, vectors.Count);
        Assert.Equal(3, handler.CallCount);
        // 第 12 条文本长 12 → 它的向量首值就该是 12（批次边界上最容易错位）
        Assert.Equal(12f, vectors[11][0]);
        Assert.Equal(25f, vectors[24][0]);
    }

    [Theory]
    [InlineData(401, AssistantModelErrorKind.Unauthorized, "AI_MODEL_UNAUTHORIZED")]
    [InlineData(402, AssistantModelErrorKind.InsufficientBalance, "AI_MODEL_INSUFFICIENT_BALANCE")]
    [InlineData(429, AssistantModelErrorKind.RateLimited, "AI_RATE_LIMITED")]
    [InlineData(503, AssistantModelErrorKind.ProviderUnavailable, "AI_MODEL_UNAVAILABLE")]
    public async Task Maps_Vendor_Failures_With_The_Same_Table_As_Chat(
        int status, AssistantModelErrorKind kind, string code)
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage((HttpStatusCode)status)
        {
            Content = new StringContent("{\"error\":{\"message\":\"upstream\"}}"),
        });
        var model = new OpenAiCompatibleEmbeddingModel(
            new StubHttpClientFactory(handler), Config(dimension: 4),
            NullLogger<OpenAiCompatibleEmbeddingModel>.Instance);

        var error = await Assert.ThrowsAsync<AssistantModelException>(
            () => model.EmbedAsync("甲", CancellationToken.None));

        // 与对话**同一张映射表**：同一个 402 在两处叫不同的名字，排障就要看两个地方
        Assert.Equal(kind, error.Kind);
        Assert.Equal(code, error.Code);
    }

    private static HttpResponseMessage JsonResponse(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        public string? LastBody { get; private set; }

        public int CallCount { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            LastRequest = request;
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return responder(request);
        }
    }
}
