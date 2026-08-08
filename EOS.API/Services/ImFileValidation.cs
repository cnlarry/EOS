namespace EOS.API.Services;

/// <summary>
/// 附件上传校验（纯函数，可单测）：大小上限 20MB + 扩展名/内容类型白名单。
/// 文件名仅作展示；下载时仍按白名单扩展名校验，防伪装文件。
/// </summary>
public static class ImFileValidation
{
    public const long MaxBytes = 20 * 1024 * 1024;

    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp",
        ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx",
        ".txt", ".csv", ".md", ".zip",
    };

    private static readonly HashSet<string> AllowedContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/png", "image/jpeg", "image/gif", "image/webp", "image/bmp",
        "application/pdf", "application/msword",
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        "application/vnd.ms-excel",
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        "application/vnd.ms-powerpoint",
        "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        "text/plain", "text/csv", "text/markdown", "application/zip",
    };

    public static bool IsAllowed(long sizeBytes, string? fileName, string? contentType)
    {
        if (sizeBytes <= 0 || sizeBytes > MaxBytes)
        {
            return false;
        }

        var extension = Path.GetExtension(fileName ?? string.Empty);
        return AllowedExtensions.Contains(extension) &&
               AllowedContentTypes.Contains(contentType ?? string.Empty);
    }

    public static string NormalizeExtension(string? fileName)
    {
        var extension = Path.GetExtension(fileName ?? string.Empty);
        return AllowedExtensions.Contains(extension) ? extension : string.Empty;
    }
}
