using EOS.API.Data;
using EOS.API.Features.Assistant.ModelAccess;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 嵌入解析器的**纯函数**部分：三种"没配好"的拒答与配置合成。
///
/// <para>
/// 这一批最该钉的就是 fail-closed：没配嵌入模型时必须是**拒答**（并指路到 3102），
/// 不能退化成空向量、更不能说"知识库里没有相关内容"——后者会让用户以为系统查过了。
/// 三种原因（没配模型 / 模型没维度 / 密钥没配）的处置完全不同，所以文案也要分开。
/// </para>
/// </summary>
public sealed class AssistantEmbeddingResolverTests
{
    private static AssistantProviderRow Provider(
        string code = "dashscope",
        string? apiKeyEnvVar = "EOS_ASSISTANT_KEY_DASHSCOPE",
        int timeoutSeconds = 300,
        string displayName = "阿里云百炼（通义千问）") => new(
            ProviderId: 1,
            Code: code,
            DisplayName: displayName,
            BaseUrl: "https://dashscope.aliyuncs.com/compatible-mode/v1",
            ApiKeyEnvVar: apiKeyEnvVar,
            TimeoutSeconds: timeoutSeconds,
            Enabled: true,
            SortIdx: 0,
            Remark: null,
            CreatedAt: DateTimeOffset.UnixEpoch,
            UpdatedAt: DateTimeOffset.UnixEpoch);

    private static AssistantModelRow Model(
        int? dimension = 1024,
        int? timeoutSeconds = null,
        string modelCode = "text-embedding-v4",
        string displayName = "通义 text-embedding-v4") => new(
            ModelId: 11,
            ProviderId: 1,
            ModelCode: modelCode,
            DisplayName: displayName,
            ContextWindow: null,
            MaxOutputTokens: null,
            DefaultTemperature: null,
            TimeoutSeconds: timeoutSeconds,
            InputPerMillionYuan: null,
            OutputPerMillionYuan: null,
            SupportsTools: false,
            IsActive: true,
            Enabled: true,
            SortIdx: 0,
            Remark: null,
            CreatedAt: DateTimeOffset.UnixEpoch,
            UpdatedAt: DateTimeOffset.UnixEpoch,
            Kind: AssistantModelKind.Embedding,
            Dimension: dimension);

    private static AssistantActiveModel Active(
        AssistantModelRow? model = null, AssistantProviderRow? provider = null) =>
        new(model ?? Model(), provider ?? Provider());

    [Fact]
    public void NotConfigured_Tells_The_Admin_Where_To_Fix_It()
    {
        var error = AssistantEmbeddingResolver.NotConfigured();

        // 用户看到的是"知识库不可用"，管理员需要的是**去哪儿配**——两个信息缺一不可
        Assert.Contains("KB_EMBEDDING_NOT_CONFIGURED", error.Message, StringComparison.Ordinal);
        Assert.Contains("模型与用量", error.Message, StringComparison.Ordinal);
        Assert.Contains("嵌入", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Composes_Endpoint_Model_Dimension_And_Vendor_Limits()
    {
        var config = AssistantEmbeddingResolver.Compose(
            Active(model: Model(timeoutSeconds: 120)), apiKey: "sk-test-key");

        Assert.Equal("text-embedding-v4", config.ModelId);
        Assert.Equal("text-embedding-v4", config.ModelCode);
        Assert.Equal(1024, config.Dimension);
        Assert.Equal("sk-test-key", config.ApiKey);
        Assert.Equal(AssistantAuthStyle.Bearer, config.AuthStyle);
        // 模型行的超时覆盖供应商的默认超时（与对话同一口径）
        Assert.Equal(120, config.TimeoutSeconds);
        // 单请求条数上限来自预设数据：百炼核过的是 10 条
        Assert.Equal(10, config.BatchMax);
    }

    [Fact]
    public void Timeout_Falls_Back_To_The_Provider_Default()
    {
        var config = AssistantEmbeddingResolver.Compose(
            Active(model: Model(timeoutSeconds: null), provider: Provider(timeoutSeconds: 300)),
            apiKey: "sk-test-key");

        Assert.Equal(300, config.TimeoutSeconds);
    }

    [Theory]
    [InlineData("zhipu", 64)]
    [InlineData("dashscope", 10)]
    [InlineData("自定义或不认识的 CODE", 10)]
    public void Batch_Max_Comes_From_The_Preset_With_A_Safe_Fallback(string code, int expected)
    {
        Assert.Equal(expected, AssistantProviderCatalog.EmbeddingBatchMaxOf(code));
    }

    [Fact]
    public void Embedding_Without_Dimension_Is_Refused()
    {
        var error = Assert.Throws<EmbeddingNotConfiguredException>(() =>
            AssistantEmbeddingResolver.Compose(
                Active(model: Model(dimension: null, displayName: "本地 bge-m3")), apiKey: "sk-test-key"));

        // 说清"缺的是什么、去哪儿补"：维度是向量的宽度，缺了写不进集合
        Assert.Contains("本地 bge-m3", error.Message, StringComparison.Ordinal);
        Assert.Contains("维度", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_Key_Names_The_Environment_Variable()
    {
        var error = Assert.Throws<EmbeddingNotConfiguredException>(() =>
            AssistantEmbeddingResolver.Compose(Active(), apiKey: null));

        // 变量名必须在话里：不然管理员只能去猜是哪一个环境变量没生效
        Assert.Contains("EOS_ASSISTANT_KEY_DASHSCOPE", error.Message, StringComparison.Ordinal);
        Assert.Contains("密钥", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Provider_Without_Key_Variable_Is_Treated_As_No_Credential()
    {
        var provider = Provider(code: "custom", apiKeyEnvVar: null, displayName: "本机嵌入服务");

        // 变量名留空 = 这个端点不需要凭据（迁移 307 放宽的那一格）：
        // 把它当成"密钥没配"，无凭据端点就永远用不起来
        Assert.False(AssistantEmbeddingResolver.RequiresKey(provider));
        var config = AssistantEmbeddingResolver.Compose(Active(provider: provider), apiKey: null);
        Assert.Null(config.ApiKey);

        // 反过来：有变量名的供应商就该要密钥（否则"没配"会被静默放行，第一次调用才 401）
        Assert.True(AssistantEmbeddingResolver.RequiresKey(Provider()));
    }
}
