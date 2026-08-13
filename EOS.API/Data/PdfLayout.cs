using System.Globalization;
using System.Text.RegularExpressions;
using EOS.API.Models;
using QuestPDF.Helpers;

namespace EOS.API.Data;

/// <summary>QuestPDF 通用布局常量与工具（字体 / 纸张 / LOGO / 值格式化）。</summary>
internal static class PdfLayout
{
    /// <summary>启动时经 FontManager.RegisterFontWithCustomName 注册的中文字体。</summary>
    public const string FontFamily = "Noto Sans CJK SC";

    public static PageSize PageSizeFor(string? paper, bool landscape = false)
    {
        var size = (paper ?? string.Empty).Trim().ToUpperInvariant() switch
        {
            "A3" => PageSizes.A3,
            "A5" => PageSizes.A5,
            "LETTER" => PageSizes.Letter,
            "LEGAL" => PageSizes.Legal,
            _ => PageSizes.A4,
        };
        return landscape ? size.Landscape() : size;
    }

    /// <summary>把旧 LOGO_PATH（~/... 相对 wwwroot）解析为 EOS.API 静态目录下的图片字节；不存在返回 null。</summary>
    public static byte[]? TryLoadLogo(IWebHostEnvironment environment, string? logoPath)
    {
        if (string.IsNullOrWhiteSpace(logoPath) || environment.WebRootPath is null) return null;
        var relative = logoPath.Trim().Replace('\\', '/').TrimStart('~', '/');
        var full = Path.Combine(environment.WebRootPath, relative);
        return File.Exists(full) ? File.ReadAllBytes(full) : null;
    }

    public static string FormatValue(object? value)
    {
        if (value is null || value is DBNull) return string.Empty;
        if (value is DateTime dateTime) return dateTime.ToString("yyyy-MM-dd");
        if (value is bool boolean) return boolean ? "是" : "否";
        if (value is decimal or double or float)
            return Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        return Convert.ToString(value)?.Trim() ?? string.Empty;
    }

    /// <summary>数字格式白名单（仅 # 0 , .）；日期格式白名单（y M d H h m s 及分隔符）。</summary>
    private static readonly Regex NumericFormatPattern = new("^[#0,.]{1,20}$", RegexOptions.Compiled);
    private static readonly Regex DateFormatPattern = new("^[yMdHhms:/\\- ]{1,30}$", RegexOptions.Compiled);

    /// <summary>
    /// 按 FIELDS.DISPLAY_FORMAT 格式化（旧 Crystal 掩码均为 .NET 兼容子集）：
    /// 数字列套用 #/0/逗号/小数点格式，日期列套用 y/M/d/H/m/s 格式；
    /// 非法/越界格式回退默认显示，避免异常。
    /// </summary>
    public static string FormatValue(object? value, string? displayFormat)
    {
        if (string.IsNullOrWhiteSpace(displayFormat)) return FormatValue(value);
        var format = displayFormat.Trim();
        if (value is DateTime dateTime && DateFormatPattern.IsMatch(format))
        {
            try { return dateTime.ToString(format, CultureInfo.InvariantCulture); } catch (FormatException) { }
        }
        if (value is not null && value is not DBNull && IsNumeric(value) && NumericFormatPattern.IsMatch(format))
        {
            try { return ((IFormattable)value).ToString(format, CultureInfo.InvariantCulture); } catch (FormatException) { }
        }
        return FormatValue(value);
    }

    private static bool IsNumeric(object value) =>
        value is sbyte or byte or short or ushort or int or uint or long or ulong
            or float or double or decimal;

    /// <summary>金额/数量类列用于合计行判断（与旧前端 isAmountColumn 一致）。</summary>
    public static bool IsAmountColumn(string key) =>
        key.Contains("QTY", StringComparison.OrdinalIgnoreCase)
        || key.Contains("AMOUNT", StringComparison.OrdinalIgnoreCase)
        || key.Contains("PRICE", StringComparison.OrdinalIgnoreCase)
        || key.Contains("SUM", StringComparison.OrdinalIgnoreCase);

    /// <summary>备注类列（REMARK/NOTE）用于"打印备注"开关。</summary>
    public static bool IsRemarkField(string key) =>
        key.Contains("REMARK", StringComparison.OrdinalIgnoreCase)
        || key.Contains("NOTE", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 分组小计列判定：数值类型且非主键、非编号/日期/类型/状态等标识列。
    /// 覆盖 QTY/AMOUNT/PRICE/WEIGHT/COST 等业务量列；每报表级汇总定义留待后续。
    /// </summary>
    public static bool IsSubtotalColumn(ReportColumn column, IReadOnlyCollection<string> pkOrder)
    {
        if (pkOrder.Contains(column.Key, StringComparer.OrdinalIgnoreCase)) return false;
        var type = column.DataType.ToLowerInvariant();
        var numeric = type.Contains("decimal") || type.Contains("float") || type.Contains("double")
            || type.Contains("money") || type is "int" or "bigint" or "smallint" or "tinyint";
        if (!numeric) return false;
        var key = column.Key.ToUpperInvariant();
        return !key.Contains("NO") && !key.Contains("ID") && !key.Contains("DATE") && !key.Contains("TIME")
            && !key.Contains("TYPE") && !key.Contains("STATE") && !key.Contains("TAG")
            && !key.Contains("SERIAL") && !key.Contains("VERSION") && !key.Contains("BATCH")
            && !key.Contains("SORT");
    }
}
