namespace EOS.API.Models;

/// <summary>发布校验单项（ADR-005 §3 机器可读报告）。</summary>
public sealed record WorkbenchDefinitionValidationCheck(string Code, bool Passed, string Message);

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
