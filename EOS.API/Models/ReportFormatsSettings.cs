namespace EOS.API.Models;

/// <summary>
/// ReportFormats 配置节（ADR-009 §12 格式包 / ADR-010 决策 3）：
/// 内置版式为开发态 Git 资产，部署时复制到 StorageRoot（只读）；
/// 客户定制存库（REPORT_FORM_LAYOUT / REPORT_FORM_BINDING，迁移 031）。
/// StorageRoot 为空时回退到 ContentRoot/ReportFormats（dev 与发布目录均随 csproj 复制）。
/// </summary>
public sealed class ReportFormatsSettings
{
    public const string SectionName = "ReportFormats";

    /// <summary>内置版式资产根目录；空 = 内容根目录下的 ReportFormats。</summary>
    public string StorageRoot { get; init; } = string.Empty;
}
