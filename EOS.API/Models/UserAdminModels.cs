namespace EOS.API.Models;

public sealed record UserAdminSummary(
    string UserId,
    string EmployeeId,
    string EmployeeName,
    string DepartmentId,
    string DepartmentName,
    string CompanyId,
    string GroupId,
    string Groups,
    bool IsActive,
    bool HasPassword,
    string? LastUpdatedBy,
    DateTime? LastUpdatedAt);

public sealed record UserAdminPageResult(
    IReadOnlyList<UserAdminSummary> Items,
    int Total,
    int Page,
    int PageSize);

public sealed record SetUserPasswordRequest(string NewPassword);

public sealed record SetUserStatusRequest(bool IsActive);
