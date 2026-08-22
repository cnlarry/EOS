namespace EOS.API.Models;

/// <summary>附件上传设置（Attachment: 配置节）。</summary>
public sealed record AttachmentSettings
{
    /// <summary>文件系统存储根目录（默认 AppContext.BaseDirectory/attachments，可配置）。</summary>
    public string StorageRoot { get; init; } = string.Empty;

    /// <summary>单个附件大小上限（字节，默认 50MB，与表 CHECK 约束一致）。</summary>
    public long MaxSizeBytes { get; init; } = 50 * 1024 * 1024;

    /// <summary>允许的扩展名白名单（如 pdf;doc;docx;xls;xlsx;jpg;jpeg;png;gif;txt;zip）。</summary>
    public string AllowedExtensions { get; init; } =
        ".pdf;.doc;.docx;.xls;.xlsx;.jpg;.jpeg;.png;.gif;.txt;.csv;.zip";
}

/// <summary>附件元数据输出 DTO（不含文件二进制，时间统一 UTC）。</summary>
public sealed record AttachmentDto(
    long Id,
    int ModuleId,
    string MasterTable,
    string KeyValues,
    int SerialNo,
    string FileName,
    string ClientFileName,
    string ContentType,
    long SizeBytes,
    string Sha256,
    string? Remark,
    string UploadedBy,
    string? UploadedByDisplay,
    DateTime UploadedAt);