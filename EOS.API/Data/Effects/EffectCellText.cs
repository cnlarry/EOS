using System.Globalization;

namespace EOS.API.Data.Effects;

/// <summary>
/// 校验/效果提示消息里的单元格文本（ADR-037 显示口径）。
///
/// 数值必须去尾零：数值列由 <c>float</c> 迁移为 <c>decimal</c> 后，
/// <c>ToString()</c> 会保留 scale（`10.000000`），而迁移前是 `10`。
/// 直接拼接会让提示消息里出现无意义的尾零，也会让"与旧过程对拍"的测试整体失败——
/// 那些测试比对的正是这条文本。
///
/// 与 <see cref="NumericDisplay"/> 的区别：后者按语义族决定小数位（金额 2 位、数量最多 6 位），
/// 本类只做"去尾零"，因为提示消息里的值是原样展示、不承担格式化职责。
/// </summary>
internal static class EffectCellText
{
    private const string TrimmedFormat = "0.######";

    public static string Format(object? value) => value switch
    {
        null or DBNull => string.Empty,
        decimal number => number.ToString(TrimmedFormat, CultureInfo.InvariantCulture),
        double number => number.ToString(TrimmedFormat, CultureInfo.InvariantCulture),
        float number => number.ToString(TrimmedFormat, CultureInfo.InvariantCulture),
        _ => value.ToString()?.Trim() ?? string.Empty,
    };
}
