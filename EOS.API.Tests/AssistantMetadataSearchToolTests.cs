using System.Text.Json;
using EOS.API.Features.Assistant.Parameters;
using EOS.API.Features.Assistant.Tools;
using Xunit;

namespace EOS.API.Tests;

/// <summary>search_metadata 的输出契约：命中按类型分行、超限如实说明、无命中给下一步。</summary>
public sealed class AssistantMetadataSearchToolTests
{
    private sealed class FakeGateway(MetadataSearchResult result) : IMetadataSearchGateway
    {
        public Task<MetadataSearchResult> SearchAsync(
            string userId, string keyword, int max, CancellationToken token) =>
            Task.FromResult(result);
    }

    [Fact]
    public async Task Search_Formats_All_Hit_Kinds()
    {
        var result = new MetadataSearchResult(
            [
                new MetadataHit("module", 1606, "采购单", "1606", "采购单", null),
                new MetadataHit("table", 1606, "采购单", "PUR_PURCHASE_M", "采购单主表", "PUR_PURCHASE_M"),
                new MetadataHit("field", 1606, "采购单", "ORDER_DATE", "采购日期", "PUR_PURCHASE_M"),
                new MetadataHit("action", 1303, "料件库存资料", "inventory-freeze", "库存冻结", null),
            ],
            Truncated: false);
        var tool = new SearchMetadataTool(new FakeGateway(result));

        var reply = await tool.ExecuteAsync("u1", JsonSerializer.SerializeToElement(new
        {
            keyword = "采购",
        }), CancellationToken.None);

        Assert.True(reply.Ok);
        Assert.Contains("模块 1606 采购单", reply.ContentForModel);
        Assert.Contains("表 PUR_PURCHASE_M", reply.ContentForModel);
        Assert.Contains("字段 ORDER_DATE 采购日期", reply.ContentForModel);
        Assert.Contains("动作 inventory-freeze 库存冻结", reply.ContentForModel);
        Assert.Contains("describe_module", reply.ContentForModel);
    }

    [Fact]
    public void Empty_Result_Suggests_Next_Step()
    {
        var text = SearchMetadataTool.Compress(
            "不存在的词", new MetadataSearchResult([], false));

        Assert.Contains("没有找到", text);
        Assert.Contains("list_my_capabilities", text);
    }

    [Fact]
    public void Truncated_Result_Says_How_Many_Are_Shown()
    {
        var result = new MetadataSearchResult(
            [new MetadataHit("field", 1606, "采购单", "ORDER_DATE", "采购日期", "PUR_PURCHASE_M")],
            Truncated: true);

        var text = SearchMetadataTool.Compress("采购", result, new AssistantToolLimitsOptions());

        Assert.Contains("仅显示前 1 条", text);
    }

    [Fact]
    public async Task Empty_Keyword_Is_Denied()
    {
        var tool = new SearchMetadataTool(new FakeGateway(new MetadataSearchResult([], false)));
        var reply = await tool.ExecuteAsync("u1", JsonSerializer.SerializeToElement(new
        {
            keyword = " ",
        }), CancellationToken.None);

        Assert.False(reply.Ok);
        Assert.Contains("keyword", reply.ContentForModel);
    }
}
