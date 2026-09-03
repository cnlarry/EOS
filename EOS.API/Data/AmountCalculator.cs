namespace EOS.API.Data;

/// <summary>
/// Document detail line amount calculation:
/// - TAX_TYPE='I' (tax-inclusive): amount tax sum = qty × price × rebate; amount = tax sum / (1 + rate); tax = tax sum - amount;
/// - TAX_TYPE='O' (tax-exclusive): amount = qty × price × rebate; tax = amount × rate; tax sum = amount + tax;
/// - TAX_TYPE='N'/other: amount = tax sum = qty × price × rebate; tax = 0.
/// Amounts are rounded to 2 decimal places (MidpointRounding.AwayFromZero).
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
