namespace EOS.API.Models;

public sealed record LoginRequest(string UserId, string Password, bool RememberMe);

public sealed record LoginUser(
    string UserId,
    string EmployeeId,
    string EmployeeName,
    string DepartmentId,
    string DepartmentName,
    string CompanyId,
    string DefaultGroupId,
    // 所属用户组描述（SYS_USER_GROUP.G_DESC，多组用顿号连接）：用户组是这套权限模型里唯一的"角色"概念
    string GroupNames);

public enum LoginFailure { None, UserNotFound, InvalidPassword, Disabled }

public sealed record LoginResult(LoginFailure Failure, LoginUser? User);

public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);

public enum PasswordChangeFailure { None, UserNotFound, NoPasswordSet, WrongCurrentPassword, InvalidNewPassword }

public sealed record PasswordChangeResult(PasswordChangeFailure Failure);
