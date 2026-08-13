namespace EOS.API.Models;

public sealed record LegacyModuleRights(
    bool CanBrowse,
    bool CanViewCost,
    bool CanViewSecrecy,
    bool CanSetup,
    IReadOnlySet<string> DeniedMasterFields,
    IReadOnlySet<string> DeniedDetailFields,
    bool CanAddNew,
    bool CanEdit,
    bool CanDelete,
    IReadOnlySet<string> DenyNewMasterFields,
    IReadOnlySet<string> DenyNewDetailFields,
    IReadOnlySet<string> DenyModiMasterFields,
    IReadOnlySet<string> DenyModiDetailFields,
    string DataFilter,
    string ExecuteTag);

/// <summary>
/// 报表级权限（SYSDD_REPORT / SYSDH_REPORT）：
/// 预览决定报表是否出现在打印面板，打印决定 PDF 生成，导出决定 CSV 导出。
/// </summary>
public sealed record ReportRights(
    bool CanPreview,
    bool CanPrint,
    bool CanExport,
    string DataFilter);
