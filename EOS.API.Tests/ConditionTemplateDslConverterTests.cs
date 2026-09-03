using EOS.API.Tests.Tools;
using Xunit;

namespace EOS.API.Tests;

public class ConditionTemplateDslConverterTests
{
    [Fact]
    public void RangeCondition_GeneratesGeTemplate()
    {
        var result = ConditionTemplateDslConverter.Convert(1, "", "", "COP_ORDER_M.ORDER_DATE", "DATE_FROM");
        Assert.NotNull(result.Template);
        Assert.Contains("\"logic\":\"AND\"", result.Template);
        Assert.Contains("\"field\":\"COP_ORDER_M.ORDER_DATE\"", result.Template);
        Assert.Contains("\"op\":\"GE\"", result.Template);
        Assert.Contains("{p.DATE_FROM}", result.Template);
        Assert.Null(result.Error);
    }

    [Fact]
    public void RangeCondition_WithDefaultGeneratesTo()
    {
        var result = ConditionTemplateDslConverter.Convert(1, "", "2026-01-01", "COP_ORDER_M.ORDER_DATE", "");
        Assert.Contains("{p.ORDER_DATE_TO}", result.Template);
    }

    [Fact]
    public void FixedSelect_ParsesOptionsAndField()
    {
        var expr = "批核:{MOC_PRODUCE_M.CONFIRM_TAG}=true;未批核:{MOC_PRODUCE_M.CONFIRM_TAG}=false";
        var result = ConditionTemplateDslConverter.Convert(2, expr, "", "MOC_PRODUCE_M.CONFIRM_TAG", "CONFIRM");
        Assert.NotNull(result.Template);
        Assert.Contains("MOC_PRODUCE_M.CONFIRM_TAG", result.Template);
        Assert.Contains("批核", result.Template);
        Assert.Contains("{p.CONFIRM}", result.Template);
        Assert.Null(result.Error);
    }

    [Fact]
    public void DataSelect_ParsesSelectSource()
    {
        var expr = "SELECT PRO_NO C_ID, PRO_DESC C_VALUE FROM PRODUCT";
        var result = ConditionTemplateDslConverter.Convert(3, expr, "", "PRODUCT.PRO_NO", "PRO_NO");
        Assert.NotNull(result.Template);
        Assert.Contains("\"selectSource\"", result.Template);
        Assert.Contains("\"table\":\"PRODUCT\"", result.Template);
        Assert.Null(result.Error);
    }

    [Fact]
    public void FixedMultiSelect_ParsesOptions()
    {
        var expr = "A:1;B:2;C:3";
        var result = ConditionTemplateDslConverter.Convert(4, expr, "", "PRODUCT.PRO_TYPE", "TYPES");
        Assert.NotNull(result.Template);
        Assert.Contains("\"op\":\"IN\"", result.Template);
        Assert.Contains("A", result.Template);
        Assert.Null(result.Error);
    }

    [Fact]
    public void DataMultiSelect_ParsesSelectSource()
    {
        var expr = "SELECT DEPOT_ID C_ID, DEPOT_NAME C_VALUE FROM DEPOT";
        var result = ConditionTemplateDslConverter.Convert(5, expr, "", "COP_ORDER_M.DEPOT_ID", "DEPOT");
        Assert.NotNull(result.Template);
        Assert.Contains("\"op\":\"IN\"", result.Template);
        Assert.Contains("\"table\":\"DEPOT\"", result.Template);
        Assert.Null(result.Error);
    }

    [Fact]
    public void InvalidDataSelect_ReturnsError()
    {
        var result = ConditionTemplateDslConverter.Convert(3, "NOT A SELECT", "", "PRODUCT.PRO_NO", "");
        Assert.Null(result.Template);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void UnknownType_ReturnsError()
    {
        var result = ConditionTemplateDslConverter.Convert(99, "", "", "FIELD", "");
        Assert.Null(result.Template);
        Assert.NotNull(result.Error);
    }
}
