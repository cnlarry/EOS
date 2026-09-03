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
    bool PhysicalExists);

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
    int TabNo = 1,
    int? FormOrder = null,
    int Span = 1,
    bool NewLine = false,
    string? CellGroup = null,
    int CellRole = 0,
    string? Options = null);

public sealed record FieldAdminMetadata(
    string TableId,
    string FieldId,
    FieldAdminInput Field,
    bool IsVirtual,
    string? VirtualExpression,
    bool IsAutoIncrement,
    string? ConvertFunction,
    string? DataSourceSql,
    string? LastUpdatedBy,
    DateTime? LastUpdatedAt,
    bool IsPrimaryKey,
    bool PhysicalExists,
    string? PhysicalType,
    bool? TypeMatches);

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
