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
    /// <summary>
    /// 一行几列只看这里：**页签级**列数 1..4（库内 NOT NULL DEFAULT 4，模块级那层自迁移 319 起不存在）。
    /// 画布按**当前页签**的列数排。
    /// </summary>
    IReadOnlyList<FormTabDefinition> Tabs,
    FormLayoutTableDesign Master,
    FormLayoutTableDesign Detail,
    string? BaseUpdatedAt,
    /// <summary>打开方式（本页签 / 新页签 / 弹窗）：设计态据此把画板摆成运行态的样子。</summary>
    string OpenMode = FormOpenModes.Tab,
    /// <summary>弹窗宽高（px）：只有 <see cref="FormOpenModes.Dialog"/> 方式下才有值。</summary>
    int? DialogWidth = null,
    int? DialogHeight = null);

public sealed record FormLayoutTableDesign(
    string Table,
    IReadOnlyList<FormLayoutDesignRow> Layout,
    IReadOnlyList<FormLayoutPoolField> Pool,
    /// <summary>该表是否已有定制行（false = 当前是推导默认）。界面据此区分"已定制/未定制"。</summary>
    bool Customized = false);

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

/// <summary>
/// 保存请求：整份版式的全量替换（页签 + 主表行 + 明细行）+ **表单呈现配置**
/// （打开方式 / 弹窗宽高）。栅格列数不在呈现配置里——它是**页签级事实**，
/// 随 <see cref="FormTabInput.Columns"/> 一起提交。
///
/// 呈现配置与版式同一笔保存、同一次重发布，所以"保存即生效"——不落在模块管理的
/// "存草稿 → 人工发布"那条链上（那里是元数据编辑面，本处是呈现面）。
/// 三项全为空时不动这几列：只存版式的调用方（含历史客户端）行为不变。
/// </summary>
public sealed record FormLayoutSaveRequest(
    string? BaseUpdatedAt,
    string? IdempotencyKey,
    IReadOnlyList<FormTabInput>? Tabs,
    IReadOnlyList<FormLayoutRowInput>? Master,
    IReadOnlyList<FormDetailLayoutRowInput>? Detail,
    string? OpenMode = null,
    int? DialogWidth = null,
    int? DialogHeight = null);

/// <summary>
/// 页签输入：<paramref name="Columns"/> = 该页签的布局列数（1..4）；null = 按兜底 4 列落库
/// （库内该列 NOT NULL DEFAULT 4，写入侧不落 NULL）。
/// </summary>
public sealed record FormTabInput(int No, string? Title, int? Columns = null);

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

/// <summary>
/// 可套用来源：只列共用同一主表的模块（跨主表套用会排出业务上不该出现的字段）。
/// 不带列数：一行几列是**页签级**事实，套用时由来源页签的列数随行一起带过来、
/// 再按目标页签的列数夹取（前端 `applyRows`）。
/// </summary>
public sealed record FormLayoutTemplate(
    int ModuleId,
    string Title,
    string MasterTable,
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
