using EOS.API.Controllers;
using EOS.API.Data;
using EOS.API.Features.Assistant.ModelAccess;
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
        int? maxOutputTokens = null, int? contextWindow = 1_048_576,
        string? kind = null, int? dimension = null) => new(
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
            Remark: null,
            Kind: kind,
            Dimension: dimension);

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

    /// <summary>
    /// 嵌入模型**必须带维度**：没有它，集合写入与检索都无从判断"这个向量能不能存"。
    /// 而对话模型带维度则是另一种错——那会让一条对话模型看起来像嵌入模型被登记了维度。
    /// </summary>
    [Fact]
    public void Embedding_Requires_Dimension_And_Chat_Forbids_It()
    {
        Assert.Null(AssistantAdminController.ValidateModel(Request(kind: "EMBEDDING", dimension: 1024)));
        // 大小写不敏感：HTTP 上来的字符串不该要求调用方记住大小写
        Assert.Null(AssistantAdminController.ValidateModel(Request(kind: "embedding", dimension: 1024)));
        // 库里的检查约束是 1–20000，这里必须同源
        Assert.Null(AssistantAdminController.ValidateModel(Request(kind: "EMBEDDING", dimension: 20_000)));
        Assert.NotNull(AssistantAdminController.ValidateModel(Request(kind: "EMBEDDING", dimension: 0)));
        Assert.NotNull(AssistantAdminController.ValidateModel(Request(kind: "EMBEDDING", dimension: 20_001)));
        // 没给维度就是错，不能悄悄按"厂商默认"放过去
        Assert.NotNull(AssistantAdminController.ValidateModel(Request(kind: "EMBEDDING")));

        Assert.Null(AssistantAdminController.ValidateModel(Request(kind: "CHAT")));
        Assert.NotNull(AssistantAdminController.ValidateModel(Request(kind: "CHAT", dimension: 1024)));
        // 未知用途要报错，而不是当成对话（当成对话会让一次笔误静默改掉用途）
        Assert.NotNull(AssistantAdminController.ValidateModel(Request(kind: "EMBEDDINGS")));
    }

    /// <summary>
    /// **编辑时不传用途 = 沿用原值**。缺省成对话那版实现下，一次"只改单价"的编辑会把嵌入模型
    /// 变成对话模型；而这条错不会报错——它只会让知识库某天突然说"没有嵌入模型"。
    /// </summary>
    [Fact]
    public void Update_Without_Kind_Keeps_The_Original_Kind()
    {
        // 原行是嵌入模型：不传用途，按原用途校验，因此维度仍必须给
        Assert.NotNull(AssistantAdminController.ValidateModel(
            Request(), AssistantModelKind.Embedding));
        Assert.Null(AssistantAdminController.ValidateModel(
            Request(dimension: 1024), AssistantModelKind.Embedding));

        // 原行是对话模型：不传用途就当对话，带维度反而是错
        Assert.Null(AssistantAdminController.ValidateModel(Request(), AssistantModelKind.Chat));
        Assert.NotNull(AssistantAdminController.ValidateModel(
            Request(dimension: 1024), AssistantModelKind.Chat));
    }

    /// <summary>
    /// 用途的**线上字符串**只有一份：`'CHAT'` / `'EMBEDDING'`——库里、接口下发、前端比对全用它。
    ///
    /// <para>
    /// 这条用例是为一个真实故障写的：接口曾用枚举默认的 `ToString()` 下发（"Chat"/"Embedding"），
    /// 而库与前端都是全大写。前端按 `'CHAT'` 过滤，于是**列表里所有模型都被静默过滤掉**——
    /// 界面显示"这家还没有模型"，库里却明明有；从预设加型号时又因为服务端解析不区分大小写而
    /// 一切正常，所以两边各自测都是绿的。跨边界写两遍字面量，错的那天不会有编译错误。
    /// </para>
    /// </summary>
    [Fact]
    public void Kind_Token_Is_The_Single_Wire_And_Database_Spelling()
    {
        Assert.Equal("CHAT", AssistantModelCatalog.KindCode(AssistantModelKind.Chat));
        Assert.Equal("EMBEDDING", AssistantModelCatalog.KindCode(AssistantModelKind.Embedding));

        foreach (var kind in new[] { AssistantModelKind.Chat, AssistantModelKind.Embedding })
        {
            var token = AssistantModelCatalog.KindCode(kind);

            // 下发的 token 必须能被自己的解析读回来（大小写不敏感，容错 HTTP 上来的写法）
            Assert.Equal(kind, AssistantAdminController.ParseModelKind(token));
            Assert.Equal(kind, AssistantAdminController.ParseModelKind(token.ToLowerInvariant()));

            // 并且**不能**等于枚举默认序列化出来的那种写法——否则"改回 ToString()"这件事
            // 又会悄悄发生一次，而这次是靠断言在看住它
            Assert.NotEqual(kind.ToString(), token);
        }
    }
}
