namespace EOS.API.Models;

/// <summary>
/// 发布校验单项：
/// error=阻断发布；warning=质量提示不拦闸（界面质量类：F_DESC 超长、
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

/// <summary>
/// 单个模块的「快照 vs 重建定义」比对结果。
///
/// 判据与发布时的版本复用**同一口径**：把定义按当前代码 + 当前元数据重建，与已发布快照的
/// <c>DEFINITION_JSON</c> 逐字比较，不同即 <see cref="Stale"/>。
///
/// <see cref="Dirty"/> 是库内人工/迁移维护的"已编辑待发布"标记。两者组合决定信号强度：
/// <c>Stale &amp;&amp; !Dirty</c> 才是**静默落后**——配置已经变了，但没有任何标记提示需要重发布，
/// 运行期还在按旧定义跑（发布后两分钟被直写改配置的模块就是这样躺了很久）。
/// </summary>
public sealed record WorkbenchSnapshotStaleness(
    int ModuleId,
    string Title,
    bool Enabled,
    int Version,
    bool Dirty,
    bool Stale,
    string? Reason = null,
    int StoredLength = 0,
    int RebuiltLength = 0,
    string? FirstDifference = null);
