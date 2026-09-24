namespace EOS.API.Models;

public sealed record ModuleRights(
    bool CanBrowse,
    bool CanViewCost,
    bool CanViewSecrecy,
    bool CanSetup,
    IReadOnlySet<string> DeniedMasterFields,
    IReadOnlySet<string> DeniedDetailFields,
    bool CanAddNew,
    bool CanEdit,
    bool CanDelete,
    bool CanApprove,
    bool CanDeapprove,
    bool CanEndCase,
    bool CanUnEndCase,
    bool CanFileView,
    bool CanFileUpda,
    bool CanFileEdit,
    bool CanFileDele,
    IReadOnlySet<string> DenyNewMasterFields,
    IReadOnlySet<string> DenyNewDetailFields,
    IReadOnlySet<string> DenyModiMasterFields,
    IReadOnlySet<string> DenyModiDetailFields,
    string DataFilter,
    string ExecuteTag,
    /// <summary>模块配置权（行为动作/校验规则/自定义按钮）；默认关闭。</summary>
    bool CanModuleConfig = false);

/// <summary>
/// 报表级权限（SYSDD_REPORT / SYSDH_REPORT）：
/// 预览决定报表是否出现在打印面板，打印决定 PDF 生成，导出决定 CSV 导出。
/// </summary>
public sealed record ReportRights(
    bool CanPreview,
    bool CanPrint,
    bool CanExport,
    string DataFilter);
