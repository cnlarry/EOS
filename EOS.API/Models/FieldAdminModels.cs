namespace EOS.API.Models;

public sealed record FieldAdminTable(
    string TableId,
    string Description,
    string? Kind,
    string? Type,
    int FieldCount,
    int UnmanagedCount,
    int OrphanCount);

public sealed record FieldAdminModule(int Id, string Label);

/// <summary>
/// 未登记进 TABLES 的物理表/视图候选：数据表维护新增走「选取物理对象」而不是手敲表名。
/// ObjectType 取 sys.objects.type（U=表、V=视图），Description 取表说明（MS_Description）。
/// </summary>
public sealed record FieldAdminPhysicalObject(
    string TableId,
    string ObjectType,
    string Description,
    int ColumnCount);

/// <summary>从物理表/视图登记表元数据（同时按物理列生成字段元数据）。</summary>
public sealed record RegisterPhysicalTableRequest(string TableId);

/// <summary>
/// 从物理表/视图登记的结果：表描述/类型由物理对象推导，字段元数据按物理列生成，
/// 未能生成的逐个给出原因（不做「部分成功却不说」的静默）。
/// </summary>
public sealed record RegisterPhysicalTableResult(
    string TableId,
    string Description,
    string Kind,
    string Type,
    int FieldCreated,
    int FieldSkipped,
    IReadOnlyList<string> SkippedReasons);

public sealed record FieldAdminFieldSummary(
    string TableId,
    string FieldId,
    string Description,
    string DataType,
    bool IsVirtual,
    bool IsVisible,
    bool IsDefault,
    bool IsQueryable,
    bool IsReadonly,
    bool IsCost,
    bool IsSecrecy,
    bool IsPrimaryKey,
    bool PhysicalExists,
    bool IsSystemColumn = false);

/// <summary>数据表完整元数据（编辑弹窗用；高风险表达式列仅展示不参与写入）。</summary>
public sealed record FieldAdminTableDetail(
    string TableId,
    string Description,
    string? Kind,
    string? Type,
    string? Remark,
    string? FkTable1,
    string? FkTable2,
    string? FkTable3,
    string? FkTable4,
    string? FkTable5,
    string? QueryRelation,
    string? DefaultCondition,
    string? DefaultVerify,
    bool CanImport,
    string? LastUpdatedBy,
    DateTime? LastUpdatedAt);

/// <summary>表元数据可编辑的低风险字段子集；FK_T_ID*/QUERY_RELATION/DF_* 只读展示。</summary>
public sealed record FieldAdminTableInput(
    string Description,
    string? Kind,
    string? Type,
    string? Remark);

public sealed record CreateFieldAdminTableRequest(string TableId, FieldAdminTableInput Table);

public sealed record UpdateFieldAdminTableRequest(string TableId, FieldAdminTableInput Table, FieldAdminTableInput? Original);

public sealed record FieldAdminUnmanagedField(string FieldId, string DataType);

public sealed record CreateUnmanagedFieldsRequest(string TableId, IReadOnlyList<string> FieldIds);

public sealed record CreateUnmanagedFieldsResult(int Created, int Skipped, IReadOnlyList<string> SkippedReasons);

/// <summary>
/// 幽灵字段：FIELDS 有元数据、物理表已无同名列。虚拟字段结构上就没有物理列，
/// 不属幽灵字段，故不在本清单内。
/// </summary>
public sealed record FieldAdminGhostField(string FieldId, string Description, string DataType);

public sealed record CleanupGhostFieldsRequest(string TableId, IReadOnlyList<string> FieldIds);

public sealed record CleanupGhostFieldsResult(int Removed, int Skipped, IReadOnlyList<string> SkippedReasons);

public sealed record FieldAdminChooser(
    bool Active,
    string? Table,
    string? Description,
    int? ModuleId,
    string? Filter,
    string? ReturnMapping,
    int? SerialNo = null);

public sealed record FieldAdminInput(
    string Label,
    string DataType,
    int Width,
    string? Align,
    string HeaderAlign,
    string? Format,
    bool IsVisible,
    bool IsDefault,
    bool IsQueryable,
    bool IsReadonly,
    bool IsRequired,
    bool IsCost,
    bool IsSecrecy,
    string? DefaultValue,
    int? VerifyIndex,
    string? Regex,
    string? Remark,
    string? BrowseUrl,
    int? BrowseModuleId,
    bool OnlyChoose,
    bool ChooseMultiple,
    string? ChoosePage,
    IReadOnlyList<FieldAdminChooser> Choosers,
    bool CanCopy,
    string? Options = null,
    /// <summary>虚拟表达式：null = 本次不改，空串 = 清空，非空 = 设为该值（须字段为虚拟字段）。</summary>
    string? VirtualExpression = null,
    /// <summary>受控转换函数名：null = 本次不改，空串 = 清空，非空 = 须在注册表内。</summary>
    string? ConvertFunction = null);

public sealed record FieldAdminMetadata(
    string TableId,
    string FieldId,
    FieldAdminInput Field,
    bool IsVirtual,
    string? VirtualExpression,
    bool IsAutoIncrement,
    string? ConvertFunction,
    string? LastUpdatedBy,
    DateTime? LastUpdatedAt,
    bool IsPrimaryKey,
    bool PhysicalExists,
    string? PhysicalType,
    bool? TypeMatches,
    bool IsSystemColumn = false);

public sealed record CreateFieldAdminRequest(string TableId, string FieldId, FieldAdminInput Field);

public sealed record UpdateFieldAdminRequest(string TableId, string FieldId, FieldAdminInput Field, FieldAdminInput? Original);

public sealed record FieldAdminPageResult(IReadOnlyList<FieldAdminFieldSummary> Items, int Total, int Page, int PageSize);

/// <summary>字段变更历史中的单条字段级明细（AUDIT_FIELD_CHANGE）。</summary>
public sealed record FieldHistoryChange(string Name, string? OldValue, string? NewValue);

/// <summary>字段变更历史事件（AUDIT_EVENT，RESOURCE_TYPE=FIELD_ADMIN）。</summary>
public sealed record FieldHistoryEvent(
    DateTime OccurredAt,
    string ActorUserId,
    string Action,
    string? Summary,
    IReadOnlyList<FieldHistoryChange> Changes,
    /// <summary>操作人姓名（SYSDN.EMP_NAME 对照解析；无对应员工时回退 ActorUserId）。</summary>
    string ActorName);

/// <summary>
/// 表列（物理列 sys.columns + 来源表内受控虚拟列 FIELDS.IS_VIRTUAL，均带 FIELDS 描述）：
/// 字段设置数据来源/回填构建器下拉选项。虚拟来源列合法，过滤条件仍只用物理列。
/// </summary>
public sealed record FieldAdminColumn(string Name, string DataType, string Description, bool IsVirtual = false);

/// <summary>QUERY_RELATION 白名单中的一条关联（别名可不同于物理表名，如 PRODUCT_J）。</summary>
public sealed record FieldAdminRelation(string Table, string Alias, IReadOnlyList<string> Conditions);

/// <summary>
/// 表关联白名单（TABLES.QUERY_RELATION 解析结果）：虚拟表达式构建器的跨表引用候选。
/// Ok=false 时 Error 给出不可解析原因（关系不可用时跨表引用一律拒绝，前端只放本表）。
/// </summary>
public sealed record FieldAdminRelations(
    string TableId,
    bool Ok,
    string? Error,
    IReadOnlyList<FieldAdminRelation> Items);
