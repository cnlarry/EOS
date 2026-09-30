using System.Text.Json;
using EOS.API.Controllers;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 历史消息的工具摘要投影。库内 <c>TOOL_CALLS_JSON</c> 是数据层的形状（含工具参数、字段名与序列化
/// 策略绑定），端点必须把它变成与 SSE 的 done 事件**同形**的 <c>{name, digest}</c> 再下发：
/// 否则前端要同时懂两种形状，而且切会话/刷新后工具卡会因为读不出摘要而消失（摘要只在流式那一瞬可见）。
/// </summary>
public sealed class AssistantMessageHistoryTests
{
    [Fact]
    public void 工具摘要投影成与done事件同形的名字与摘要且不带工具参数()
    {
        const string json =
            """[{"Name":"search_records","ArgumentsJson":"{\"moduleId\":1606}","ResultDigest":"module=1606(采购单) total=3 shown=3"}]""";

        var digests = AssistantController.ParseToolCallDigests(json);

        var only = Assert.Single(digests!);
        // 用 JsonDocument 断"值"：默认编码器会把中文转义成 \uXXXX，按字符串拼断言会假失败
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(only));
        Assert.Equal("search_records", document.RootElement.GetProperty("name").GetString());
        Assert.Equal("module=1606(采购单) total=3 shown=3", document.RootElement.GetProperty("digest").GetString());
        // 形状恰好两项：工具参数不下发给前端
        Assert.Equal(2, document.RootElement.EnumerateObject().Count());
    }

    [Fact]
    public void 库内命名大小写不同也读得出来()
    {
        // 落库用的是默认序列化策略（PascalCase），但读取不该绑死在这一点上
        const string camel =
            """[{"name":"kb_search","argumentsJson":"{}","resultDigest":"hit=2"}]""";

        var digests = AssistantController.ParseToolCallDigests(camel);

        Assert.Contains("kb_search", JsonSerializer.Serialize(Assert.Single(digests!)), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("[]")]
    [InlineData("不是 JSON")]
    [InlineData("{\"name\":\"search_records\"}")] // 形状不对：对象而非数组
    public void 没有摘要或摘要坏了都按没有处理且不抛(string? json)
    {
        Assert.Null(AssistantController.ParseToolCallDigests(json));
    }
}
