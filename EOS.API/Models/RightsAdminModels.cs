namespace EOS.API.Models;

/// <summary>
/// 模块权限输入（写白名单，§6 固定列集合）。
/// 个人（SYSDD）/组（SYSDH）共用同一形状；所有列由服务端固定白名单校验，
/// 不接受任意列名；EXEC_TAG 允许 A~Z 单字符（A=禁止执行），DENY_* 逗号/分号分隔，
/// DATA_FILTER 走受控表达式校验。
/// </summary>
public sealed record ModuleRightsInput(
    int ModuleId,
    string? ExecTag,
    bool AddNew,
    bool Edit,
    bool Delete,
    bool Approve,
    bool Deapprove,
    bool Report,
    bool Cost,
    bool Setup,
    bool ModuleConfig,
    bool Secrecy,
    bool EndCase,
    bool UnEndCase,
    bool Other1,
    bool Other2,
    bool Other3,
    bool Other4,
    bool FileView,
    bool FileUpda,
    bool FileEdit,
    bool FileDele,
    string? DenyViewMaster,
    string? DenyViewDetail,
    string? DenyNewMaster,
    string? DenyNewDetail,
    string? DenyModiMaster,
    string? DenyModiDetail,
    string? DataFilter);

/// <summary>
/// 模块权限矩阵行：moduleId/title/groupPath 定位，权限位为「可编辑值」
/// （用户 = 个人 SYSDD 值或默认空；组 = 该组 SYSDH 值或默认空），
/// hasPersonal 决定是否「完全采用」，effective 为按引擎规则计算出的生效值。
/// </summary>
public sealed record ModuleRightsRow(
    int ModuleId,
    string Title,
    string GroupPath,
    string? Icon,
    int ParentId,
    int RootId,
    int SortIndex,
    string? ExecTag,
    bool AddNew,
    bool Edit,
    bool Delete,
    bool Approve,
    bool Deapprove,
    bool Report,
    bool Cost,
    bool Setup,
    bool ModuleConfig,
    bool Secrecy,
    bool EndCase,
    bool UnEndCase,
    bool Other1,
    bool Other2,
    bool Other3,
    bool Other4,
    bool FileView,
    bool FileUpda,
    bool FileEdit,
    bool FileDele,
    string DenyViewMaster,
    string DenyViewDetail,
    string DenyNewMaster,
    string DenyNewDetail,
    string DenyModiMaster,
    string DenyModiDetail,
    string DataFilter,
    bool HasPersonal,
    EffectiveModuleRights Effective);

/// <summary>
/// 生效模块权限（引擎规则：个人覆盖组；组布尔位 OR、EXEC_TAG 取最大、
/// 禁止字段交集、DATA_FILTER AND 组合；无记录全禁）。Source: personal/group/none。
/// </summary>
public sealed record EffectiveModuleRights(
    string Source,
    bool CanBrowse,
    string ExecTag,
    bool AddNew,
    bool Edit,
    bool Delete,
    bool Approve,
    bool Deapprove,
    bool Report,
    bool Cost,
    bool Setup,
    bool ModuleConfig,
    bool Secrecy,
    bool EndCase,
    bool UnEndCase,
    bool Other1,
    bool Other2,
    bool Other3,
    bool Other4,
    bool FileView,
    bool FileUpda,
    bool FileEdit,
    bool FileDele,
    IReadOnlyList<string> DenyViewMaster,
    IReadOnlyList<string> DenyViewDetail,
    IReadOnlyList<string> DenyNewMaster,
    IReadOnlyList<string> DenyNewDetail,
    IReadOnlyList<string> DenyModiMaster,
    IReadOnlyList<string> DenyModiDetail,
    string DataFilter);

/// <summary>报表权限输入（SYSDD_REPORT / SYSDH_REPORT）。</summary>
public sealed record ReportRightsInput(
    int ModuleId,
    string ReportId,
    bool Preview,
    bool Print,
    bool Export,
    string? DataFilter);

/// <summary>
/// 报表权限矩阵行：行 = REPORT_ID + 名称，列 = PREVIEW/PRINT/EXPORT 勾选；
/// hasPersonal 决定「完全采用」，effective 为生效值（个人覆盖 / 组 OR + DATA_FILTER OR 拼接）。
/// </summary>
public sealed record ReportRightsRow(
    int ModuleId,
    string ModuleTitle,
    string ReportId,
    string ReportName,
    bool Preview,
    bool Print,
    bool Export,
    string DataFilter,
    bool HasPersonal,
    EffectiveReportRights Effective);

public sealed record EffectiveReportRights(
    string Source,
    bool Preview,
    bool Print,
    bool Export,
    string DataFilter);

/// <summary>用户组列表行（SYSDG + 成员数 + 备注）。</summary>
public sealed record UserGroupSummary(string GroupId, string GroupDescription, int MemberCount, string? Remark);

/// <summary>组成员行（SYSDG_USER JOIN SYSDL/SYSDN）。</summary>
public sealed record GroupMemberSummary(string UserId, string EmployeeId, string EmployeeName);

/// <summary>用户所属组行。</summary>
public sealed record UserGroupItem(string GroupId, string GroupDescription);

/// <summary>字段级拒绝选择器用的模块主/明细物理字段（复用 2302 字段元数据思路）。</summary>
public sealed record RightsFieldInfo(string FieldId, string Description, bool IsCost, bool IsSecrecy);

public sealed record RightsModuleFields(
    string MasterTable,
    string? DetailTable,
    IReadOnlyList<RightsFieldInfo> MasterFields,
    IReadOnlyList<RightsFieldInfo> DetailFields);

/// <summary>单模块生效权限（引擎聚合结果 + 来源，供生效值预览/调试）。</summary>
public sealed record EffectiveRightsDetail(
    string Source,
    string ExecTag,
    bool CanBrowse,
    bool CanAddNew,
    bool CanEdit,
    bool CanDelete,
    bool CanViewCost,
    bool CanViewSecrecy,
    bool CanSetup,
    IReadOnlyList<string> DeniedMasterFields,
    IReadOnlyList<string> DeniedDetailFields,
    IReadOnlyList<string> DenyNewMasterFields,
    IReadOnlyList<string> DenyNewDetailFields,
    IReadOnlyList<string> DenyModiMasterFields,
    IReadOnlyList<string> DenyModiDetailFields,
    string DataFilter);

public sealed record SaveModuleRightsRequest(IReadOnlyList<ModuleRightsInput> Items);
public sealed record SaveReportRightsRequest(IReadOnlyList<ReportRightsInput> Items);
public sealed record SaveMembersRequest(IReadOnlyList<string> Ids);

/// <summary>用户组新增请求（2305 定制页主档，SYSDG）。</summary>
public sealed record CreateGroupRequest(string? GroupId, string? GroupDescription, string? Remark);

/// <summary>用户组编辑请求（G_IDX 不可修改）。</summary>
public sealed record UpdateGroupRequest(string? GroupDescription, string? Remark);
