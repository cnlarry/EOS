using EOS.API.Data;
using Xunit;

namespace EOS.API.Tests;

public class ConditionTemplateParserTests
{
    [Fact]
    public void RangeTemplate_ParsesToType1()
    {
        var template = """{"logic":"AND","items":[{"field":"COP_ORDER_M.ORDER_DATE","op":"GE","value":"{p.DATE_FROM}"},{"field":"COP_ORDER_M.ORDER_DATE","op":"LE","value":"{p.DATE_FROM_TO}"}]}""";
        var result = ConditionTemplateParser.TryParse(template, null, null);
        Assert.NotNull(result);
        Assert.Equal(1, result.Type);
        Assert.Equal("COP_ORDER_M.ORDER_DATE", result.Field);
        Assert.Equal("DATE_FROM", result.ParameterName);
        Assert.Empty(result.Options);
    }

    [Fact]
    public void FixedSelectTemplate_ParsesToType2()
    {
        var template = """{"logic":"AND","items":[{"field":"MOC_PRODUCE_M.CONFIRM_TAG","op":"EQ","value":"{p.CONFIRM}"}],"options":[{"label":"批核","value":"true"},{"label":"未批核","value":"false"}],"parameterName":"CONFIRM"}""";
        var result = ConditionTemplateParser.TryParse(template, null, null);
        Assert.NotNull(result);
        Assert.Equal(2, result.Type);
        Assert.Equal("MOC_PRODUCE_M.CONFIRM_TAG", result.Field);
        Assert.Equal("CONFIRM", result.ParameterName);
        Assert.Equal(2, result.Options.Count);
        Assert.Equal("true", result.Options[0].Value);
    }

    [Fact]
    public void DataSelectTemplate_ParsesToType3()
    {
        var template = """{"logic":"AND","items":[{"field":"PRODUCT.PRO_NO","op":"EQ","value":"{p.PRO_NO}"}],"selectSource":{"table":"PRODUCT","idColumn":"PRO_NO","valueColumn":"PRO_DESC"}}""";
        var result = ConditionTemplateParser.TryParse(template, null, null);
        Assert.NotNull(result);
        Assert.Equal(3, result.Type);
        Assert.NotNull(result.SelectSource);
        Assert.Equal("PRODUCT", result.SelectSource.Table);
    }

    [Fact]
    public void FixedMultiTemplate_ParsesToType4()
    {
        var template = """{"logic":"AND","items":[{"field":"PRODUCT.PRO_TYPE","op":"IN","value":"{p.TYPES}"}],"options":[{"label":"A","value":"1"},{"label":"B","value":"2"}]}""";
        var result = ConditionTemplateParser.TryParse(template, null, null);
        Assert.NotNull(result);
        Assert.Equal(4, result.Type);
        Assert.Equal(2, result.Options.Count);
    }

    [Fact]
    public void DataMultiTemplate_ParsesToType5()
    {
        var template = """{"logic":"AND","items":[{"field":"COP_ORDER_M.DEPOT_ID","op":"IN","value":"{p.DEPOT}"}],"selectSource":{"table":"DEPOT","idColumn":"DEPOT_ID","valueColumn":"DEPOT_NAME"}}""";
        var result = ConditionTemplateParser.TryParse(template, null, null);
        Assert.NotNull(result);
        Assert.Equal(5, result.Type);
        Assert.NotNull(result.SelectSource);
    }

    [Fact]
    public void NullTemplate_ReturnsNull()
    {
        Assert.Null(ConditionTemplateParser.TryParse(null, null, null));
        Assert.Null(ConditionTemplateParser.TryParse("", null, null));
    }

    [Fact]
    public void MalformedTemplate_ReturnsNull()
    {
        Assert.Null(ConditionTemplateParser.TryParse("not-json", null, null));
    }
}
