using EOS.API.Data;
using Xunit;

namespace EOS.API.Tests;

public sealed class AmountCalculatorTests
{
    [Theory]
    // O 外含税：金额=10×100=1000，税额=1000×13%=130，价税合计=1130
    [InlineData(10, 100, 13, "O", 100, 1000, 130, 1130)]
    // I 内含税：价税合计=1000，金额=1000/1.13，税额=差额
    [InlineData(10, 100, 13, "I", 100, 884.96, 115.04, 1000)]
    // N 不含税：税额 0
    [InlineData(10, 100, 13, "N", 100, 1000, 0, 1000)]
    // 折扣 90%：O 型金额=900
    [InlineData(10, 100, 0, "O", 90, 900, 0, 900)]
    public void Calculate_按税型计算金额(
        decimal qty, decimal price, decimal taxRate, string taxType, decimal rebate,
        decimal expectedAmount, decimal expectedTaxSum, decimal expectedAmountTax)
    {
        var result = AmountCalculator.Calculate(qty, price, taxRate, taxType, rebate);
        Assert.Equal(expectedAmount, result.Amount);
        Assert.Equal(expectedTaxSum, result.TaxSum);
        Assert.Equal(expectedAmountTax, result.AmountTax);
    }

    [Fact]
    public void Calculate_未填税型按不含税处理()
    {
        var result = AmountCalculator.Calculate(5, 20, null, null, null);
        Assert.Equal(100, result.Amount);
        Assert.Equal(0, result.TaxSum);
        Assert.Equal(100, result.AmountTax);
    }

    [Fact]
    public void Calculate_空数量金额为0()
    {
        var result = AmountCalculator.Calculate(null, 100, 13, "O", 100);
        Assert.Equal(0, result.Amount);
        Assert.Equal(0, result.TaxSum);
        Assert.Equal(0, result.AmountTax);
    }
}
