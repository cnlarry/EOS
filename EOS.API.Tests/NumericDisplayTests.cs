using EOS.API.Data;

namespace EOS.API.Tests;

/// <summary>
/// 数值显示三级回落（ADR-037 批 3）：显式格式 → 语义族默认 → 类型默认。
/// 与前端 <c>fieldFormat.ts</c> 的断言口径保持一致，任一端改动都要同步这里。
/// </summary>
public sealed class NumericDisplayTests
{
    [Fact]
    public void 显式格式优先于一切推导()
    {
        Assert.Equal("1,234.5000", NumericDisplay.Format(1234.5m, "0.0000"));
        Assert.Equal("1235", NumericDisplay.Format(1234.5m, "0"));
    }

    [Fact]
    public void 未配格式时整数类型不带小数位()
    {
        Assert.Equal("1,234", NumericDisplay.Format(1234m, null, dataType: "int"));
        Assert.Equal("1,235", NumericDisplay.Format(1234.5m, null, dataType: "bigint"));
    }

    [Fact]
    public void 未配格式时小数类型最多六位且去尾零()
    {
        Assert.Equal("1,234.5", NumericDisplay.Format(1234.5m, null, dataType: "decimal"));
        Assert.Equal("1,234.567891", NumericDisplay.Format(1234.5678901m, null, dataType: "decimal"));
        Assert.Equal("1,234", NumericDisplay.Format(1234m, null, dataType: "decimal"));
    }

    [Fact]
    public void 金额族默认两位小数()
    {
        Assert.Equal("1,234.50", NumericDisplay.Format(1234.5m, null, "AMOUNT", "decimal"));
        Assert.Equal("1,234.00", NumericDisplay.Format(1234m, null, "AMOUNT", "decimal"));
    }

    [Fact]
    public void 金额位数可由币别覆盖_日元为零位()
    {
        Assert.Equal("1,235", NumericDisplay.Format(1234.5m, null, "AMOUNT", "decimal", amountDecimals: 0));
        Assert.Equal("1,234.500", NumericDisplay.Format(1234.5m, null, "AMOUNT", "decimal", amountDecimals: 3));
    }

    [Fact]
    public void 空值与非法格式都不抛异常()
    {
        Assert.Equal(string.Empty, NumericDisplay.Format(null, null));
        Assert.Equal(string.Empty, NumericDisplay.Format(DBNull.Value, "#,##0.00"));
        // 非法格式串回退到默认格式，而不是抛 FormatException
        Assert.Equal("1,234.5", NumericDisplay.Format(1234.5m, "这不是格式"));
    }

    [Fact]
    public void 非数值原样返回()
    {
        Assert.Equal("ABC", NumericDisplay.Format("ABC", "#,##0.00"));
    }
}
