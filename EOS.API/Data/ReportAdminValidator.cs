using System.Text.RegularExpressions;

namespace EOS.API.Data;

/// <summary>
/// 报表定义维护校验（纯逻辑，便于单元测试）：
/// 编号/排序字段串格式、图片文件头白名单。
/// </summary>
internal static class ReportAdminValidator
{
    /// <summary>
    /// 报表编号：字母/数字/下划线/连字符，3–40 位。
    ///
    /// <para>
    /// 原先放行点号且允许 1–100 位，理由是"存量编号里有点号"——那 4 个带点号/空格的编号已按规范清理
    /// （见 275_report_id_normalization.sql），放行理由随之消失。宽度也一并收到 40：列宽是 `nchar(50)`，
    /// 放行 100 位意味着超长编号会被静默截断成**另一个**编号，而编号是报表的身份（7 张表按它引用），
    /// 截断即孤儿行。
    /// </para>
    /// </summary>
    private static readonly Regex ReportIdPattern = new("^[A-Za-z0-9_-]{3,40}$", RegexOptions.Compiled);
    private static readonly Regex FieldToken = new("^[A-Za-z_][A-Za-z0-9_]*\\.[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);

    public static void ValidateReportId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id) || !ReportIdPattern.IsMatch(id.Trim()))
            throw new ArgumentException("报表编号格式无效（3-40 位字母/数字/下划线/连字符）。", nameof(id));
    }

    public static void ValidateModuleId(int? moduleId)
    {
        if (moduleId is null or <= 0) throw new ArgumentException("模块号无效。", nameof(moduleId));
    }

    /// <summary>排序/分组字段串：逗号分隔的 "表.列" 白名单格式；空串允许。</summary>
    public static IReadOnlyList<string> ValidateFieldList(string? raw, string fieldName)
    {
        var tokens = (raw ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        foreach (var token in tokens)
        {
            if (!FieldToken.IsMatch(token))
                throw new ArgumentException($"{fieldName} 字段格式无效（须为 表.列，逗号分隔）。", fieldName);
        }
        return tokens;
    }

    public static void ValidateSerialNo(int serialNo)
    {
        if (serialNo is < 1 or > 32767)
            throw new ArgumentException("排序方案序号须在 1-32767 之间。", nameof(serialNo));
    }

    private static readonly byte[][] ImageMagic =
    [
        [0x89, 0x50, 0x4E, 0x47], // PNG
        [0xFF, 0xD8, 0xFF],       // JPEG
        [0x47, 0x49, 0x46, 0x38], // GIF
    ];

    public static bool IsAllowedLogo(byte[] content, string fileName, out string reason)
    {
        reason = string.Empty;
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        if (extension is not (".png" or ".jpg" or ".jpeg" or ".gif"))
        {
            reason = "仅支持 PNG/JPG/GIF 图片。";
            return false;
        }
        if (content.Length == 0 || content.Length > 2 * 1024 * 1024)
        {
            reason = "图片大小须在 2MB 以内。";
            return false;
        }
        if (!ImageMagic.Any(magic => content.Take(magic.Length).SequenceEqual(magic)))
        {
            reason = "文件内容不是有效图片。";
            return false;
        }
        return true;
    }
}
