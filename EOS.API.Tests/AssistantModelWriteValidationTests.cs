using EOS.API.Controllers;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 模型写入的服务端边界（离线）。
///
/// <para>
/// 为什么值得单独钉住：窗口与最大输出的上界**散在三处**——库里的检查约束、预设目录的初值、
/// 这里的服务端校验。三者必须同源，而漏一处的表现不是报错在明处，而是"**从预设添加模型时保存失败**"
/// （预设给的是厂商公开的真实值）。这条用例就是让三处里的这一处不能再悄悄落后。
/// </para>
/// </summary>
public sealed class AssistantModelWriteValidationTests
{
    private static AssistantAdminController.AssistantModelRequest Request(
        int? maxOutputTokens = null, int? contextWindow = 1_048_576) => new(
            ProviderId: 1,
            ModelCode: "deepseek-flash",
            DisplayName: "DeepSeek Flash",
            ContextWindow: contextWindow,
            MaxOutputTokens: maxOutputTokens,
            DefaultTemperature: null,
            TimeoutSeconds: null,
            InputPerMillionYuan: null,
            OutputPerMillionYuan: null,
            SupportsTools: true,
            Enabled: true,
            SortIdx: 0,
            Remark: null);

    [Fact]
    public void Accepts_The_Max_Output_That_Vendors_Actually_Publish()
    {
        // 393216 是厂商文档上的真实值，也正是预设里那个值——它必须能存进去
        Assert.Null(AssistantAdminController.ValidateModel(Request(maxOutputTokens: 393_216)));
        // 上界与 CK_ASSISTANT_MODEL_MAXOUT 一致
        Assert.Null(AssistantAdminController.ValidateModel(Request(maxOutputTokens: 1_000_000)));
        Assert.NotNull(AssistantAdminController.ValidateModel(Request(maxOutputTokens: 1_000_001)));
        Assert.NotNull(AssistantAdminController.ValidateModel(Request(maxOutputTokens: 0)));
        // 留空是正当取值（= 不传该参数），不是错误
        Assert.Null(AssistantAdminController.ValidateModel(Request()));
    }

    [Fact]
    public void Accepts_The_Context_Window_That_Vendors_Actually_Publish()
    {
        // 1M 上下文是当前常态（DeepSeek 与小米都是 1048576）
        Assert.Null(AssistantAdminController.ValidateModel(Request(contextWindow: 1_048_576)));
        Assert.NotNull(AssistantAdminController.ValidateModel(Request(contextWindow: 999)));
    }

    [Fact]
    public void Still_Rejects_What_Cannot_Be_Sent()
    {
        // 温度与窗口的上下界不因为"放宽输出上限"而松动
        Assert.NotNull(AssistantAdminController.ValidateModel(Request() with { DefaultTemperature = 3m }));
        Assert.NotNull(AssistantAdminController.ValidateModel(Request() with { ModelCode = "  " }));
        Assert.NotNull(AssistantAdminController.ValidateModel(Request() with { DisplayName = null }));
    }
}
