namespace EOS.API.Models;

public sealed record CardBatchItem(string EmpId, string CardId);

public sealed record CardBatchRequest(DateTime StartDate, DateTime? EndDate, IReadOnlyList<CardBatchItem> Cards);

public sealed record AttendanceGenerateRequest(
    DateTime StartDate,
    DateTime EndDate,
    string Mode,
    string? DeptId,
    IReadOnlyList<string>? EmpIds);

public sealed record AttendanceAdjustWageRequest(string? Month);
