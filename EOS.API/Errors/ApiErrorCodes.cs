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

    // 业务拒绝：由专用异常类型经异常过滤器统一翻译（不要在各控制器里再写一遍字面量）
    public const string DataFilterUnsupported = "DATA_FILTER_UNSUPPORTED";
    public const string GroupExpressionUnsupported = "GROUP_EXP_UNSUPPORTED";
    public const string PdfDataTooLarge = "PDF_DATA_TOO_LARGE";
    public const string PeriodClosed = "PERIOD_CLOSED";
}
