namespace EOS.API.Data;

public sealed record SaveRecordRequest(
    IReadOnlyDictionary<string, string?> Values,
    IReadOnlyList<IReadOnlyDictionary<string, string?>>? Details = null,
    IReadOnlyDictionary<string, string?>? Original = null,
    IReadOnlyList<PrepayOffsetRequest>? PrepayOffsets = null,
    string? IdempotencyKey = null);

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
