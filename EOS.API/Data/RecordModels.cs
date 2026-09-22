namespace EOS.API.Data;

public sealed record SaveRecordRequest(
    IReadOnlyDictionary<string, string?> Values,
    IReadOnlyList<IReadOnlyDictionary<string, string?>>? Details = null,
    IReadOnlyDictionary<string, string?>? Original = null,
    IReadOnlyList<PrepayOffsetRequest>? PrepayOffsets = null,
    string? IdempotencyKey = null,
    // 本次提交里「用户在界面上亲手选过的来源」：主表按字段键、明细按行下标。
    // 只携带本会话新选中的来源——未重选的字段不下发，服务端保留既有记忆；
    // 服务端会校验字段与来源序号确属该表单定义，并拒绝记录单来源字段（取首个即唯一）。
    IReadOnlyDictionary<string, int>? ChooserSources = null,
    IReadOnlyList<IReadOnlyDictionary<string, int>?>? DetailChooserSources = null,
    // 明细各行**原有的项次**（按行下标对齐 submitted details；新行给 null）。
    // 项次是明细行的身份：下游单据按"单号 + 项次"引用明细，服务端每行重赋 1..n 会让
    // 删掉中间行后的其余行静默改号、下游引用随之错位；回传既有项次即可保持身份不变。
    IReadOnlyList<string?>? DetailSerials = null);

/// <summary>
/// 收款/付款单的预收/预付冲抵行（对应旧 COP_RECEIPT_PREPAY / PUR_PAY_PREPAY 关联表）。
/// 只允许引用已批核的预收/预付单，金额由保存后的 AfterSave/P_WF_* 汇总校验。
/// </summary>
public sealed record PrepayOffsetRequest(string Type, string No, decimal? Amount, decimal PrepayAmount);

public sealed record ApproveWorkflowRequest(string Key, string? IdempotencyKey = null, string? Message = null);

/// <summary>字段级校验错误：RowIndex 仅明细行错误携带（0 起），主表/整单级为 null。</summary>
public sealed record FieldError(string Field, string Message, string Code, int? RowIndex = null);

/// <summary>保存成功但存在后续异常时的非阻断告警。</summary>
public sealed record SaveWarning(string Code, string Message);

public sealed record RecordBundle(IReadOnlyDictionary<string, object?> Master, IReadOnlyList<IReadOnlyDictionary<string, object?>> Details);

/// <summary>
/// 单据在途流程状态（WF_MONITOR.WF_STATE 的受控投影），供表单工具栏按状态切换批核/撤回/禁用：
/// None=无流程实例（直接批核模型）；InProgress=审批中（禁编辑/删，批核改显示撤回）；
/// Completed=流程已完成（正常批核语义）；Withdrawn=已撤回（可编辑后重新送审）。
/// </summary>
public enum FlowState
{
    None = 0,
    InProgress = 1,
    Completed = 2,
    Withdrawn = 3,
}

public enum RecordAccessStatus
{
    Ok,
    NotFound,
    OutOfScope,
    FilterUnsupported,
    KeyMismatch,
    ValidationFailed,
    ConcurrentModified,
}

public sealed record RecordReadResult(RecordAccessStatus Status, RecordBundle? Bundle, FlowState FlowState = FlowState.None);

public sealed record RecordSaveResult(
    RecordAccessStatus Status,
    string? ErrorCode,
    string? ErrorMessage,
    IReadOnlyList<FieldError>? FieldErrors,
    IReadOnlyList<string>? Key,
    bool FlowStarted = false,
    IReadOnlyList<SaveWarning>? Warnings = null)
{
    public static RecordSaveResult Success(IReadOnlyList<string> key, IReadOnlyList<SaveWarning>? warnings = null) =>
        new(RecordAccessStatus.Ok, null, null, null, key, Warnings: warnings);

    /// <summary>流程已启动（单据进入审批链，未确认）。</summary>
    public static RecordSaveResult SuccessFlowStarted(IReadOnlyList<string> key) =>
        new(RecordAccessStatus.Ok, null, null, null, key, FlowStarted: true);

    public static RecordSaveResult Failed(
        RecordAccessStatus status,
        string code,
        string message,
        IReadOnlyList<FieldError>? fieldErrors = null) =>
        new(status, code, message, fieldErrors, null);
}
