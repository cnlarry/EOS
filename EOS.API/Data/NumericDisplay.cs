using System.Globalization;

namespace EOS.API.Data;

/// <summary>
/// 数值显示格式的三级回落，与前端 <c>fieldFormat.ts</c> 同语义（ADR-037 批 3）。
///
/// 顺序：显式格式（<c>FIELDS.DISPLAY_FORMAT</c> / <c>layout.json</c> 的 format）→ 语义族默认 → 类型默认 → 原样。
/// 打印与导出统一走这里，避免"屏幕一套、PDF 一套"长期分叉。
///
/// 刻意不做的事：不按币别动态收窄金额位数——那需要把 <c>CURR.DECIMAL_PLACES</c>
/// 从打印数据管道送进渲染器，而 <c>layout.json</c> 的列格式是静态配置，属下一步。
/// 这里只保证"没有显式格式时"的默认口径与屏幕一致。
/// </summary>
public static class NumericDisplay
{
    /// <summary>金额默认位数（人民币/美元等两位小数币种）。</summary>
    public const int DefaultAmountDecimals = 2;

    /// <summary>数量与其它小数的默认格式：最多 6 位、去尾零、带千分位。</summary>
    public const string QuantityFormat = "#,##0.######";

    /// <summary>整数类型的默认格式：无小数位、带千分位。</summary>
    public const string IntegerFormat = "#,##0";

    /// <summary>整数类型集合（判断"该不该有小数位"）。</summary>
    public static bool IsIntegerType(string? dataType) =>
        dataType?.Trim().ToLowerInvariant() is "int" or "bigint" or "smallint" or "tinyint";

    /// <summary>金额格式：固定位小数（日元 0 位、第纳尔 3 位），带千分位。</summary>
    public static string AmountFormat(int decimals) =>
        decimals <= 0 ? "#,##0" : "#,##0." + new string('0', Math.Min(decimals, 6));

    /// <summary>未配置显式格式时的默认格式。</summary>
    public static string DefaultFormat(
        string? numericRole, string? dataType, int amountDecimals = DefaultAmountDecimals)
    {
        if (string.Equals(numericRole, "AMOUNT", StringComparison.OrdinalIgnoreCase))
        {
            return AmountFormat(amountDecimals);
        }
        return IsIntegerType(dataType) ? IntegerFormat : QuantityFormat;
    }

    /// <summary>解析最终使用的 .NET 格式串：显式优先，缺失时按语义族/类型推导。</summary>
    public static string Resolve(
        string? explicitFormat, string? numericRole, string? dataType, int amountDecimals = DefaultAmountDecimals) =>
        string.IsNullOrWhiteSpace(explicitFormat)
            ? DefaultFormat(numericRole, dataType, amountDecimals)
            : explicitFormat.Trim();

    /// <summary>
    /// 按三级回落格式化。
    /// 非法格式串不抛异常——回退到默认格式；再失败则返回不变文化原样文本。
    /// </summary>
    public static string Format(
        object? value,
        string? explicitFormat,
        string? numericRole = null,
        string? dataType = null,
        int amountDecimals = DefaultAmountDecimals)
    {
        if (value is null or DBNull) return string.Empty;
        if (!IsNumeric(value)) return Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;

        var format = Resolve(explicitFormat, numericRole, dataType, amountDecimals);
        try
        {
            return ((IFormattable)value).ToString(format, CultureInfo.InvariantCulture);
        }
        catch (FormatException)
        {
            var fallback = DefaultFormat(numericRole, dataType, amountDecimals);
            try
            {
                return ((IFormattable)value).ToString(fallback, CultureInfo.InvariantCulture);
            }
            catch (FormatException)
            {
                return Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;
            }
        }
    }

    public static bool IsNumeric(object value) =>
        value is sbyte or byte or short or ushort or int or uint or long or ulong
            or float or double or decimal;
}
