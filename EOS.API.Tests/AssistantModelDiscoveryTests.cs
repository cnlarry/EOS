using EOS.API.Data;
using EOS.API.Features.Assistant.ModelAccess;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 型号拉取的**解析与失败映射**（离线，不起 HTTP）。
///
/// <para>
/// 为什么单独测这两件事：出网那一段（连不上、超时、5xx）各家的表现都不一样，没法在单测里穷举；
/// 但"厂商的 JSON 怎么读"与"状态码翻成什么原因"是纯逻辑，而且是**管理员会直接看到的两句话**——
/// 把它测住，出网部分的失败就只剩"如实转达"。
/// </para>
/// </summary>
public sealed class AssistantModelDiscoveryTests
{
    private static AssistantProviderRow Provider(string code = "deepseek") => new(
        ProviderId: 1,
        Code: code,
        DisplayName: "DeepSeek 开放平台",
        BaseUrl: "https://api.deepseek.com",
        ApiKeyEnvVar: "EOS_ASSISTANT_KEY_DEEPSEEK",
        TimeoutSeconds: 300,
        Enabled: true,
        SortIdx: 0,
        Remark: null,
        CreatedAt: DateTimeOffset.UnixEpoch,
        UpdatedAt: DateTimeOffset.UnixEpoch);

    [Fact]
    public void Parse_Reads_The_OpenAi_Compatible_Minimum()
    {
        // 多数厂商只给标识（OpenAI 兼容的最小子集）
        var result = AssistantModelDiscovery.Parse(
            """{"object":"list","data":[{"id":"b-model"},{"id":"a-model"}]}""");

        Assert.True(result.Ok);
        Assert.Equal(["a-model", "b-model"], result.Models.Select(model => model.ModelCode));
        // 厂商没给的字段保持 null——留空由管理员补，不在这里编一个默认值
        Assert.All(result.Models, model =>
        {
            Assert.Null(model.ContextWindow);
            Assert.Null(model.MaxOutputTokens);
        });
    }

    [Fact]
    public void Parse_Uses_Rich_Vendor_Fields_When_The_Vendor_Gives_Them()
    {
        // 实测 DeepSeek 的列出模型接口会带窗口、最大输出与显示名：能用的就用上，省一次手工填
        var result = AssistantModelDiscovery.Parse("""
            {"data":[
              {"id":"deepseek-flash","name":"DeepSeek-V4.1-Flash","context_window":1048576,"max_output_tokens":393216},
              {"id":"deepseek-v4-pro","name":"DeepSeek-V4-Pro","context_window":1048576,"max_output_tokens":393216}
            ]}
            """);

        Assert.True(result.Ok);
        var flash = result.Models[0];
        Assert.Equal("deepseek-flash", flash.ModelCode);
        Assert.Equal("DeepSeek-V4.1-Flash", flash.DisplayName);
        Assert.Equal(1048576, flash.ContextWindow);
        Assert.Equal(393216, flash.MaxOutputTokens);
    }

    [Fact]
    public void Parse_Skips_Entries_Without_Id_And_Duplicates()
    {
        // 没有标识的条目落库就是一个永远打不通的行；重复标识会让同一个型号出现两次
        var result = AssistantModelDiscovery.Parse("""
            {"data":[{"id":"same"},{"id":"  "},{"name":"没有标识"},{"id":"same"},{"id":"other"}]}
            """);

        Assert.True(result.Ok);
        Assert.Equal(["other", "same"], result.Models.Select(model => model.ModelCode));
    }

    [Theory]
    [InlineData("""{"models":[{"id":"a"}]}""")]  // 形状不对：没有 data 数组
    [InlineData("not json at all")]
    [InlineData("")]
    public void Parse_Reports_Unreadable_Responses_Instead_Of_An_Empty_List(string json)
    {
        var result = AssistantModelDiscovery.Parse(json);

        Assert.False(result.Ok);
        Assert.Equal(AssistantModelDiscovery.InvalidResponseCode, result.Code);
        // 关键：失败**不用空清单冒充**——"读不懂"与"这家没有模型"给管理员的下一步完全不同
        Assert.Empty(result.Models);
        Assert.Contains("模型清单", result.Message!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(401, AssistantModelDiscovery.KeyRejectedCode)]
    [InlineData(403, AssistantModelDiscovery.KeyRejectedCode)]
    [InlineData(404, AssistantModelDiscovery.NotSupportedCode)]
    [InlineData(405, AssistantModelDiscovery.NotSupportedCode)]
    [InlineData(500, AssistantModelDiscovery.FailedCode)]
    [InlineData(429, AssistantModelDiscovery.FailedCode)]
    public void FailureForStatus_Separates_Actionable_Causes(int status, string expectedCode)
    {
        var result = AssistantModelDiscovery.FailureForStatus(Provider(), status, """{"error":"nope"}""");

        Assert.False(result.Ok);
        Assert.Equal(expectedCode, result.Code);
        Assert.Empty(result.Models);
        // 厂商的原话要带上：只说"失败了"，管理员没法判断是密钥还是端点
        Assert.Contains("厂商返回", result.Message!, StringComparison.Ordinal);
    }

    [Fact]
    public void FailureForStatus_Points_At_The_Right_Next_Step()
    {
        var rejected = AssistantModelDiscovery.FailureForStatus(Provider(), 401, null);
        Assert.Contains("密钥", rejected.Message!, StringComparison.Ordinal);

        // 没有该端点时要直接说"手工填标识"——否则管理员会一直在密钥上找原因
        var notSupported = AssistantModelDiscovery.FailureForStatus(Provider(), 404, null);
        Assert.Contains("手工填模型标识", notSupported.Message!, StringComparison.Ordinal);

        // 没有响应体时不编造"厂商返回："
        Assert.DoesNotContain("厂商返回", notSupported.Message!, StringComparison.Ordinal);
    }
}
