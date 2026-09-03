namespace EOS.API.Models;

/// <summary>
/// 发布校验单项：
/// error=阻断发布；warning=质量提示不拦闸（界面质量类：F_DESC 超长、FORM_ORDER 缺失、
/// datetime 建议格式等），由批量报告与豁免清单承接。
/// </summary>
public sealed record WorkbenchDefinitionValidationCheck(string Code, bool Passed, string Message, string Severity = "error");

/// <summary>模块 Definition 校验报告（dry-run 与发布共用）。</summary>
public sealed record WorkbenchDefinitionValidationReport(
    int ModuleId,
    string Title,
    bool Passed,
    string DefinitionVersion,
    IReadOnlyList<WorkbenchDefinitionValidationCheck> Checks,
    string? DefinitionJson = null);

/// <summary>管理端「已编辑但未发布」状态（模块 + 脏标记 + 当前快照）。</summary>
public sealed record WorkbenchModuleSnapshotStatus(
    int ModuleId,
    string Title,
    bool Enabled,
    bool Dirty,
    string? LastModifiedBy,
    DateTime? LastModifiedAt,
    int? PublishedVersion,
    DateTime? PublishedAt,
    string? PublishedBy,
    string ValidationStatus,
    string? DefinitionVersion);

/// <summary>发布结果（逐模块）。</summary>
public sealed record WorkbenchPublishResult(
    int ModuleId,
    string Title,
    bool Published,
    int? Version,
    string? DefinitionVersion,
    bool Passed,
    IReadOnlyList<WorkbenchDefinitionValidationCheck> Checks,
    string? Error = null);

public sealed record WorkbenchDefinitionValidateRequest(int ModuleId);

public sealed record WorkbenchDefinitionPublishRequest(IReadOnlyList<int> ModuleIds);
