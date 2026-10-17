namespace EOS.API.Data;

/// <summary>
/// Document detail line amount calculation:
/// - TAX_TYPE='I' (tax-inclusive): amount tax sum = qty × price × rebate; amount = tax sum / (1 + rate); tax = tax sum - amount;
/// - TAX_TYPE='O' (tax-exclusive): amount = qty × price × rebate; tax = amount × rate; tax sum = amount + tax;
/// - TAX_TYPE='N'/other: amount = tax sum = qty × price × rebate; tax = 0.
/// Amounts are rounded to the currency's decimal places (ADR-037)，默认 2 位、远离零舍入。
/// 调用方应从 <see cref="CurrencyPrecision.ReadAsync"/> 取币别精度后传入，不要在此硬编码位数。
/// </summary>
public static class AmountCalculator
{
    public sealed record AmountRow(decimal Amount, decimal TaxSum, decimal AmountTax);

    public static AmountRow Calculate(
        decimal? qty,
        decimal? price,
        decimal? taxRatePercent,
        string? taxType,
        decimal? rebatePercent,
        int decimals = CurrencyPrecision.DefaultDecimals,
        MidpointRounding rounding = MidpointRounding.AwayFromZero)
    {
        var quantity = qty ?? 0m;
        var unitPrice = price ?? 0m;
        var taxRate = (taxRatePercent ?? 0m) / 100m;
        var rebate = (rebatePercent ?? 100m) / 100m;
        var raw = quantity * unitPrice * rebate;
        switch ((taxType ?? string.Empty).Trim().ToUpperInvariant())
        {
            case "I":
                var amountTaxIncluded = Round(raw, decimals, rounding);
                var amountIncluded = Round(amountTaxIncluded / (1m + taxRate), decimals, rounding);
                return new AmountRow(amountIncluded, Round(amountTaxIncluded - amountIncluded, decimals, rounding), amountTaxIncluded);
            case "O":
                var amountOutside = Round(raw, decimals, rounding);
                var taxSumOutside = Round(amountOutside * taxRate, decimals, rounding);
                return new AmountRow(amountOutside, taxSumOutside, Round(amountOutside + taxSumOutside, decimals, rounding));
            default:
                var amountNone = Round(raw, decimals, rounding);
                return new AmountRow(amountNone, 0m, amountNone);
        }
    }

    private static decimal Round(decimal value, int decimals, MidpointRounding rounding)
        => Math.Round(value, decimals, rounding);
}
