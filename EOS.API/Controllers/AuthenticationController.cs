using System.Security.Claims;
using EOS.API.Data;
using EOS.API.Errors;
using EOS.API.Models;
using EOS.API.Security;
using EOS.API.Telemetry;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EOS.API.Controllers;

[ApiController]
[Route("api/v1/auth")]
public sealed class AuthenticationController(
    AuthenticationRepository repository,
    LoginThrottleService throttle,
    CurrentUserContext userContext,
    ILogger<AuthenticationController> logger) : ControllerBase
{
    [AllowAnonymous]
    [HttpPost("login")]
    public async Task<IActionResult> Login(LoginRequest request, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(request.UserId) || string.IsNullOrEmpty(request.Password))
            return BadRequest(ApiProblem.Create(
                StatusCodes.Status400BadRequest,
                ApiErrorCodes.LoginInvalidInput,
                "请输入用户名和密码"));
        var throttleKey = BuildThrottleKey(request.UserId);
        if (throttle.TryGetLockoutRemaining(throttleKey, out var remaining))
        {
            var minutes = Math.Max(1, (int)Math.Ceiling(remaining.TotalMinutes));
            logger.LogWarning(
                "登录被限流 userId={UserId} ip={ClientIp} remainingMinutes={RemainingMinutes} correlation={CorrelationId}",
                request.UserId.Trim(), HttpContext.Connection.RemoteIpAddress, minutes, RequestContext.GetCorrelationId(HttpContext));
            var problem = ApiProblem.Create(
                StatusCodes.Status429TooManyRequests,
                ApiErrorCodes.LoginLocked,
                $"登录尝试次数过多，请 {minutes} 分钟后再试。");
            ApiProblem.AttachTraceId(problem, HttpContext);
            return StatusCode(StatusCodes.Status429TooManyRequests, problem);
        }
        var result = await repository.AuthenticateAsync(request.UserId, request.Password, token);
        if (result.Failure != LoginFailure.None)
        {
            if (throttle.RecordFailure(throttleKey))
            {
                logger.LogWarning(
                    "登录失败次数达阈值，账号临时锁定 userId={UserId} ip={ClientIp} correlation={CorrelationId}",
                    request.UserId.Trim(), HttpContext.Connection.RemoteIpAddress, RequestContext.GetCorrelationId(HttpContext));
            }
            var (code, message) = result.Failure switch
            {
                LoginFailure.UserNotFound => (ApiErrorCodes.LoginUserNotFound, "用户名不存在"),
                LoginFailure.InvalidPassword => (ApiErrorCodes.LoginInvalidPassword, "密码不正确"),
                LoginFailure.Disabled => (ApiErrorCodes.LoginDisabled, "账户已被禁用"),
                _ => (ApiErrorCodes.InternalError, "登录失败")
            };
            var problem = ApiProblem.Create(StatusCodes.Status401Unauthorized, code, message);
            ApiProblem.AttachTraceId(problem, HttpContext);
            return Unauthorized(problem);
        }
        throttle.Reset(throttleKey);
        var user = result.User!;
        var claims = new[] {
            new Claim(ClaimTypes.NameIdentifier, user.UserId), new Claim(ClaimTypes.Name, user.EmployeeName),
            new Claim("employee_id", user.EmployeeId), new Claim("department_id", user.DepartmentId),
            new Claim("department_name", user.DepartmentName), new Claim("company_id", user.CompanyId),
            new Claim("default_group_id", user.DefaultGroupId)
        };
        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme)),
            new AuthenticationProperties { IsPersistent = request.RememberMe,
                ExpiresUtc = request.RememberMe ? DateTimeOffset.UtcNow.AddDays(30) : null,
                AllowRefresh = true });
        return Ok(new { user.UserId, user.EmployeeName });
    }

    private string BuildThrottleKey(string userId)
    {
        var ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return $"{userId.Trim().ToLowerInvariant()}|{ip}";
    }

    [Authorize]
    [HttpPut("password")]
    public async Task<IActionResult> ChangePassword(ChangePasswordRequest request, CancellationToken token)
    {
        if (string.IsNullOrEmpty(request.CurrentPassword) || string.IsNullOrEmpty(request.NewPassword))
            return BadRequest(ApiProblem.Create(
                StatusCodes.Status400BadRequest,
                ApiErrorCodes.InvalidArgument,
                "请输入当前密码和新密码。"));

        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var throttleKey = $"pwd|{userId.Trim().ToLowerInvariant()}|{HttpContext.Connection.RemoteIpAddress}";
        if (throttle.TryGetLockoutRemaining(throttleKey, out var remaining))
        {
            var minutes = Math.Max(1, (int)Math.Ceiling(remaining.TotalMinutes));
            logger.LogWarning(
                "修改密码被限流 userId={UserId} ip={ClientIp} remainingMinutes={RemainingMinutes} correlation={CorrelationId}",
                userId.Trim(), HttpContext.Connection.RemoteIpAddress, minutes, RequestContext.GetCorrelationId(HttpContext));
            var problem = ApiProblem.Create(
                StatusCodes.Status429TooManyRequests,
                ApiErrorCodes.LoginLocked,
                $"尝试次数过多，请 {minutes} 分钟后再试。");
            ApiProblem.AttachTraceId(problem, HttpContext);
            return StatusCode(StatusCodes.Status429TooManyRequests, problem);
        }

        var result = await repository.ChangePasswordAsync(
            userId, request.CurrentPassword, request.NewPassword, userContext.EmployeeName, token);
        switch (result.Failure)
        {
            case PasswordChangeFailure.None:
                throttle.Reset(throttleKey);
                logger.LogInformation(
                    "用户修改密码成功 userId={UserId} ip={ClientIp} correlation={CorrelationId}",
                    userId.Trim(), HttpContext.Connection.RemoteIpAddress, RequestContext.GetCorrelationId(HttpContext));
                return NoContent();
            case PasswordChangeFailure.UserNotFound:
                return NotFound(ApiProblem.Create(StatusCodes.Status404NotFound, ApiErrorCodes.NotFound, "用户不存在。"));
            case PasswordChangeFailure.NoPasswordSet:
                return BadRequest(ApiProblem.Create(
                    StatusCodes.Status400BadRequest,
                    ApiErrorCodes.InvalidArgument,
                    "当前账号尚未设置密码，请联系管理员分配初始密码。"));
            case PasswordChangeFailure.WrongCurrentPassword:
                if (throttle.RecordFailure(throttleKey))
                {
                    logger.LogWarning(
                        "修改密码失败次数达阈值，账号临时锁定 userId={UserId} ip={ClientIp} correlation={CorrelationId}",
                        userId.Trim(), HttpContext.Connection.RemoteIpAddress, RequestContext.GetCorrelationId(HttpContext));
                }
                return BadRequest(ApiProblem.Create(
                    StatusCodes.Status400BadRequest,
                    ApiErrorCodes.InvalidArgument,
                    "当前密码不正确。"));
            default:
                return BadRequest(ApiProblem.Create(
                    StatusCodes.Status400BadRequest,
                    ApiErrorCodes.InvalidArgument,
                    "新密码不符合要求：长度 8-64 个字符，且不能以空格开头或结尾。"));
        }
    }

    [Authorize]
    [HttpPost("logout")]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return NoContent();
    }
}
