namespace EOS.API.Data;

/// <summary>
/// 单据明细行金额计算（等价旧系统 JScript calc_row_amount + Round）：
/// - TAX_TYPE='I'（内含税）：价税合计 = 数量×单价×折扣；金额 = 价税合计/(1+税率)；税额 = 价税合计-金额；
/// - TAX_TYPE='O'（外含税）：金额 = 数量×单价×折扣；税额 = 金额×税率；价税合计 = 金额+税额；
/// - TAX_TYPE='N'/其它（不含税）：金额 = 价税合计 = 数量×单价×折扣；税额 = 0。
/// 金额统一保留 2 位小数（四舍五入，AwayFromZero，对齐旧 Round）。
/// </summary>
public static class AmountCalculator
{
    public sealed record AmountRow(decimal Amount, decimal TaxSum, decimal AmountTax);

    public static AmountRow Calculate(
        decimal? qty,
        decimal? price,
        decimal? taxRatePercent,
        string? taxType,
        decimal? rebatePercent)
    {
        var quantity = qty ?? 0m;
        var unitPrice = price ?? 0m;
        var taxRate = (taxRatePercent ?? 0m) / 100m;
        var rebate = (rebatePercent ?? 100m) / 100m;
        var raw = quantity * unitPrice * rebate;
        switch ((taxType ?? string.Empty).Trim().ToUpperInvariant())
        {
            case "I":
                var amountTaxIncluded = Round2(raw);
                var amountIncluded = Round2(amountTaxIncluded / (1m + taxRate));
                return new AmountRow(amountIncluded, Round2(amountTaxIncluded - amountIncluded), amountTaxIncluded);
            case "O":
                var amountOutside = Round2(raw);
                var taxSumOutside = Round2(amountOutside * taxRate);
                return new AmountRow(amountOutside, taxSumOutside, Round2(amountOutside + taxSumOutside));
            default:
                var amountNone = Round2(raw);
                return new AmountRow(amountNone, 0m, amountNone);
        }
    }

    private static decimal Round2(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);
}
