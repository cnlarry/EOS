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

public sealed record ApproveWorkflowRequest(string Key, string? IdempotencyKey = null);

public sealed record FieldError(string Field, string Message, string Code);

public sealed record RecordBundle(IReadOnlyDictionary<string, object?> Master, IReadOnlyList<IReadOnlyDictionary<string, object?>> Details);

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

public sealed record RecordReadResult(RecordAccessStatus Status, RecordBundle? Bundle);

public sealed record RecordSaveResult(
    RecordAccessStatus Status,
    string? ErrorCode,
    string? ErrorMessage,
    IReadOnlyList<FieldError>? FieldErrors,
    IReadOnlyList<string>? Key,
    bool FlowStarted = false)
{
    public static RecordSaveResult Success(IReadOnlyList<string> key) =>
        new(RecordAccessStatus.Ok, null, null, null, key);

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
