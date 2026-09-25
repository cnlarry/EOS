using EOS.API.Data.Forms;

namespace EOS.API.Models;

/// <summary>
/// 设计态读取结果：当前版式（无行则是推导默认）+ 该表可放回表单的字段池。
/// 值一律不下发单据数据——画布是结构占位，故设计者无需该模块的浏览权。
/// </summary>
public sealed record FormLayoutDesignState(
    int ModuleId,
    string Title,
    string MasterTable,
    string? DetailTable,
    int Columns,
    IReadOnlyList<FormTabDefinition> Tabs,
    FormLayoutTableDesign Master,
    FormLayoutTableDesign Detail,
    string? BaseUpdatedAt);

public sealed record FormLayoutTableDesign(
    string Table,
    IReadOnlyList<FormLayoutDesignRow> Layout,
    IReadOnlyList<FormLayoutPoolField> Pool);

/// <summary>
/// 版式行 + 该行的字段与锁定信息。锁定 = 该字段不允许"从表单移除"，
/// 菜单项因此不出现；服务端保存期独立拒绝（双保险）。
/// </summary>
public sealed record FormLayoutDesignRow(
    string Key,
    string Label,
    string DataType,
    int TabNo,
    int OrderNo,
    int Span,
    int RowSpan,
    bool NewLine,
    string? SectionId,
    string? CellGroup,
    int CellRole,
    bool Hidden,
    bool Locked,
    string? LockReason,
    bool UserVisible,
    bool Required,
    bool IsPrimaryKey,
    bool HasChooser,
    bool IsVirtual);

/// <summary>字段池条目：已注册但未排进本模块表单；<c>UserVisible</c> 供锁图标提示。</summary>
public sealed record FormLayoutPoolField(
    string Key,
    string Label,
    string DataType,
    bool UserVisible,
    bool Required,
    bool IsPrimaryKey,
    bool HasChooser,
    bool IsVirtual,
    bool Locked,
    string? LockReason);

/// <summary>保存请求：整份版式的全量替换（页签 + 主表行 + 明细行）。</summary>
public sealed record FormLayoutSaveRequest(
    string? BaseUpdatedAt,
    string? IdempotencyKey,
    IReadOnlyList<FormTabInput>? Tabs,
    IReadOnlyList<FormLayoutRowInput>? Master,
    IReadOnlyList<FormDetailLayoutRowInput>? Detail);

public sealed record FormTabInput(int No, string? Title);

public sealed record FormLayoutRowInput(
    string Key,
    int TabNo = 1,
    int Span = 1,
    int RowSpan = 1,
    bool NewLine = false,
    string? SectionId = null,
    string? CellGroup = null,
    int CellRole = 0,
    bool Hidden = false);

public sealed record FormDetailLayoutRowInput(string Key, bool Hidden = false);

/// <summary>重置请求：只带乐观锁基准与幂等键（重置 = 删除该模块两表全部行，回到推导）。</summary>
public sealed record FormLayoutResetRequest(string? BaseUpdatedAt, string? IdempotencyKey);

/// <summary>保存/重置响应：成功档位回带最新版式（含新的乐观锁基准）。</summary>
public sealed record FormLayoutSaveResponse(
    string Status,
    string? Message,
    string? DefinitionVersion,
    FormLayoutDesignState? State);

/// <summary>可套用来源：只列共用同一主表的模块（跨主表套用会排出业务上不该出现的字段）。</summary>
public sealed record FormLayoutTemplate(
    int ModuleId,
    string Title,
    string MasterTable,
    int Columns,
    bool HasDetail);

public enum FormLayoutSaveStatus
{
    Saved,
    Replayed,
    ModuleNotFound,
    Conflict,
    LayoutInvalid,
    PublishFailed,
}

/// <summary>保存结果：失败档位携带失败缘由与失败字段，供设计态就地提示并保留用户编辑内容。</summary>
public sealed record FormLayoutSaveOutcome(
    FormLayoutSaveStatus Status,
    string? Message = null,
    IReadOnlyList<FormLayoutValidationIssue>? LayoutIssues = null,
    IReadOnlyList<WorkbenchDefinitionValidationCheck>? PublishChecks = null,
    string? DefinitionVersion = null,
    string? ResultKey = null);
