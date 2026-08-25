namespace EOS.API.Errors;

/// <summary>
/// 统一错误契约的稳定错误码。前端依据 code 做分支，
/// message 仅用于展示，不应作为判断依据。
/// </summary>
public static class ApiErrorCodes
{
    public const string InvalidArgument = "INVALID_ARGUMENT";
    public const string InvalidModel = "INVALID_MODEL";
    public const string NotFound = "NOT_FOUND";
    public const string Unauthorized = "UNAUTHORIZED";
    public const string Forbidden = "FORBIDDEN";
    public const string InternalError = "INTERNAL_ERROR";

    // 登录
    public const string LoginInvalidInput = "LOGIN_INVALID_INPUT";
    public const string LoginUserNotFound = "LOGIN_USER_NOT_FOUND";
    public const string LoginInvalidPassword = "LOGIN_INVALID_PASSWORD";
    public const string LoginDisabled = "LOGIN_DISABLED";
    public const string LoginLocked = "LOGIN_LOCKED";
}
