using System.Text.Json;
using EOS.API.Services;
using Xunit;

namespace EOS.API.Tests;

public sealed class ImCardSnapshotTests
{
    private static readonly Dictionary<string, string> Labels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["CGD_NO"] = "采购单号",
        ["STATUS"] = "状态",
        ["AMOUNT"] = "金额",
        ["DATE"] = "日期",
        ["REMARK"] = "备注",
    };

    [Fact]
    public void Build_ProducesMinimalSnapshotWithPreferredFields()
    {
        var row = new Dictionary<string, object?>
        {
            ["CGD_NO"] = "CGD2026-0001",
            ["STATUS"] = "已审核",
            ["AMOUNT"] = 12345.67m,
            ["DATE"] = "2026-08-01",
            ["REMARK"] = "不进入快照",
        };

        var json = ImCardSnapshot.Build("purchase-order", "采购单", "CGD2026-0001", Labels, row);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("purchase-order", root.GetProperty("cardType").GetString());
        Assert.Equal("采购单 CGD2026-0001", root.GetProperty("title").GetString());
        var fields = root.GetProperty("fields").EnumerateArray().ToList();
        Assert.Equal(4, fields.Count);
        Assert.Equal("采购单号", fields[0].GetProperty("label").GetString());
        Assert.Equal("CGD2026-0001", fields[0].GetProperty("value").GetString());
        Assert.Equal("状态", fields[1].GetProperty("label").GetString());
        Assert.Equal("已审核", fields[1].GetProperty("value").GetString());
        Assert.Equal("金额", fields[2].GetProperty("label").GetString());
        Assert.Equal("12345.67", fields[2].GetProperty("value").GetString());
        Assert.Equal("打开单据", root.GetProperty("actions")[0].GetProperty("label").GetString());
    }

    [Fact]
    public void Build_SkipsNullAndEmptyValues()
    {
        var row = new Dictionary<string, object?>
        {
            ["CGD_NO"] = "PO-1",
            ["STATUS"] = null,
            ["AMOUNT"] = " ",
        };

        var json = ImCardSnapshot.Build("purchase-order", "采购单", "PO-1", Labels, row);
        using var doc = JsonDocument.Parse(json);
        var fields = doc.RootElement.GetProperty("fields").EnumerateArray().ToList();

        Assert.Single(fields);
        Assert.Equal("采购单号", fields[0].GetProperty("label").GetString());
    }

    [Fact]
    public void Build_NoPreferredMatch_ProducesFieldsOnlyCard()
    {
        var row = new Dictionary<string, object?> { ["REMARK"] = "只有备注" };
        var json = ImCardSnapshot.Build("purchase-order", "采购单", "PO-X", Labels, row);
        using var doc = JsonDocument.Parse(json);

        Assert.Empty(doc.RootElement.GetProperty("fields").EnumerateArray());
        Assert.Equal("PO-X", doc.RootElement.GetProperty("entityId").GetString());
    }
}
