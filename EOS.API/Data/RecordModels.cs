namespace EOS.API.Data;

public sealed record SaveRecordRequest(
    IReadOnlyDictionary<string, string?> Values,
    IReadOnlyList<IReadOnlyDictionary<string, string?>>? Details = null,
    IReadOnlyDictionary<string, string?>? Original = null);

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
    IReadOnlyList<string>? Key)
{
    public static RecordSaveResult Success(IReadOnlyList<string> key) =>
        new(RecordAccessStatus.Ok, null, null, null, key);

    public static RecordSaveResult Failed(
        RecordAccessStatus status,
        string code,
        string message,
        IReadOnlyList<FieldError>? fieldErrors = null) =>
        new(status, code, message, fieldErrors, null);
}
