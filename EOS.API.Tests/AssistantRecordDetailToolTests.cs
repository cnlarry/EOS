using EOS.API.Data;
using EOS.API.Features.Assistant.Parameters;
using EOS.API.Features.Assistant.Tools;
using EOS.API.Models;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// get_record_detail 的输出契约：主表 + 明细 + 站内深链。
/// 权限与取数路径在集成用例里覆盖，这里钉住"明细行确实进入模型可读文本、超限如实说明"。
/// </summary>
public sealed class AssistantRecordDetailToolTests
{
    private static WorkbenchDefinition Definition() => new(
        1606, "客户订单", "COP_ORDER_M", "COP_ORDER_D",
        MasterFields:
        [
            new("ORDER_NO", "订单号", "nvarchar", 100, "left", IsPrimaryKey: true),
            new("REMARK", "备注", "nvarchar", 100, "left", IsPrimaryKey: false),
        ],
        DetailFields:
        [
            new("PRO_NO", "料号", "nvarchar", 100, "left", IsPrimaryKey: false),
            new("QTY", "数量", "decimal", 100, "right", IsPrimaryKey: false),
        ],
        DefaultSort: null, HasAdd: true, HasEdit: true, DetailNoSave: false,
        MasterPkOrder: ["ORDER_NO"], DetailNoFields: string.Empty, HasWorkflow: false);

    [Fact]
    public void Compress_Includes_Detail_Rows_And_Links()
    {
        var row = new Dictionary<string, object?>
        {
            ["ORDER_NO"] = "DD001",
            ["REMARK"] = "首单",
        };
        var details = new WorkbenchData(
            [
                new Dictionary<string, object?> { ["SERIAL_NO"] = "1", ["PRO_NO"] = "P001", ["QTY"] = 2m },
                new Dictionary<string, object?> { ["SERIAL_NO"] = "2", ["PRO_NO"] = "P002", ["QTY"] = 3m },
            ], Total: 5, Page: 1, PageSize: 50);

        var text = GetRecordDetailTool.Compress(Definition(), row, details);

        Assert.Contains("订单号=DD001", text);
        Assert.Contains("备注=首单", text);
        Assert.Contains("明细（共 5 行，显示前 2 行）", text);
        Assert.Contains("料号=P001", text);
        Assert.Contains("项次=1", text);
        Assert.Contains("数量=3", text);
        Assert.Contains("其余 3 行未展示", text);
        Assert.Contains("/workbench/1606/view/DD001", text);
        Assert.Contains("/workbench/1606/edit/DD001", text);
        Assert.Contains("/print/1606?key=", text);
    }

    [Fact]
    public void Compress_Without_Details_Keeps_Master_Only()
    {
        var text = GetRecordDetailTool.Compress(
            Definition(),
            new Dictionary<string, object?> { ["ORDER_NO"] = "DD002", ["REMARK"] = "无明细" });

        Assert.Contains("订单号=DD002", text);
        Assert.DoesNotContain("明细（", text);
        Assert.Contains("/workbench/1606/view/DD002", text);
    }

    [Fact]
    public void Compress_Truncates_Detail_Rows_By_Limit()
    {
        var details = new WorkbenchData(
            [
                new Dictionary<string, object?> { ["PRO_NO"] = "P001", ["QTY"] = 1m },
                new Dictionary<string, object?> { ["PRO_NO"] = "P002", ["QTY"] = 2m },
            ], Total: 2, Page: 1, PageSize: 50);
        var limits = new AssistantToolLimitsOptions { DetailRowMaxColumns = 0 };

        var text = GetRecordDetailTool.Compress(
            Definition(), new Dictionary<string, object?> { ["ORDER_NO"] = "DD003" }, details, limits);

        // 列为 0 时明细行仍要占位（空行），不能静默消失
        Assert.Contains("明细（共 2 行，显示前 2 行）", text);
        Assert.Contains("(空行)", text);
    }
}
