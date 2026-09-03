using EOS.API.Data;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 样例数据场景扩展单测：
/// rows 控制明细行数、variant 控制长文本/空明细，主表与字段白名单不变。
/// </summary>
public class SamplePrintDataFactoryTests
{
    private const string SampleJson = """
        {
          "moduleId": 1405,
          "title": "客户订单",
          "headerCompany": "测试公司",
          "headerCompanyEn": null,
          "headerText": null,
          "footerText": "谢谢惠顾",
          "tailText": "以下空白",
          "logoPath": null,
          "masterFields": [ { "key": "ORDER_NO", "label": "订单号", "displayFormat": null } ],
          "detailFields": [ { "key": "PRO_NO", "label": "料号", "displayFormat": null },
                            { "key": "QTY", "label": "数量", "displayFormat": null } ],
          "master": { "ORDER_NO": "DD13010001" },
          "details": [
            { "PRO_NO": "HTP6-0001", "PRO_NAME": "电子元件", "PRO_SPEC": "10KΩ",
              "QTY": 100, "UNIT_ID": "PCS", "PRICE": 0.5, "REBATE": 100, "AMOUNT_TAX": 50.00 }
          ],
          "clientProfile": null
        }
        """;

    [Fact]
    public void Rows_ExpandsDetails_WithSequentialNumbers()
    {
        var data = SamplePrintDataFactory.Build("1405", SampleJson);
        var expanded = SamplePrintDataFactory.Expand(data, 10, null);
        Assert.Equal(10, expanded.Details.Count);
        Assert.Equal("HTP6-0001", expanded.Details[0]["PRO_NO"]);
        Assert.Equal("HTP6-0010", expanded.Details[9]["PRO_NO"]);
    }

    [Fact]
    public void LongText_OverwritesProductFields()
    {
        var data = SamplePrintDataFactory.Build("1405", SampleJson);
        var expanded = SamplePrintDataFactory.Expand(data, 2, "longText");
        Assert.Equal(2, expanded.Details.Count);
        Assert.Contains("超长", expanded.Details[0]["PRO_NAME"] as string);
        Assert.Contains("超长", expanded.Details[0]["PRO_SPEC"] as string);
    }

    [Fact]
    public void Empty_ClearsDetails()
    {
        var data = SamplePrintDataFactory.Build("1405", SampleJson);
        var expanded = SamplePrintDataFactory.Expand(data, null, "empty");
        Assert.Empty(expanded.Details);
    }

    [Fact]
    public void ZeroRows_ProducesEmptyDetails()
    {
        var data = SamplePrintDataFactory.Build("1405", SampleJson);
        var expanded = SamplePrintDataFactory.Expand(data, 0, null);
        Assert.Empty(expanded.Details);
    }

    [Fact]
    public void DefaultRows_KeepsSampleCount()
    {
        var data = SamplePrintDataFactory.Build("1405", SampleJson);
        var expanded = SamplePrintDataFactory.Expand(data, null, null);
        Assert.Equal(data.Details.Count, expanded.Details.Count);
        Assert.Equal(data.Master, expanded.Master);
    }
}
