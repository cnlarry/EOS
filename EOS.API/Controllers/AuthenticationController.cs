using System.Security.Claims;
using EOS.API.Data;
using EOS.API.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EOS.API.Controllers;

[ApiController]
[Route("api/auth")]
public sealed class AuthenticationController(AuthenticationRepository repository) : ControllerBase
{
    [AllowAnonymous]
    [HttpPost("login")]
    public async Task<IActionResult> Login(LoginRequest request, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(request.UserId) || string.IsNullOrEmpty(request.Password))
            return BadRequest(new ProblemDetails { Title = "请输入用户名和密码" });
        var result = await repository.AuthenticateAsync(request.UserId, request.Password, token);
        if (result.Failure != LoginFailure.None)
            return Unauthorized(new ProblemDetails { Title = result.Failure switch
            {
                LoginFailure.UserNotFound => "用户名不存在",
                LoginFailure.InvalidPassword => "密码不正确",
                LoginFailure.Disabled => "账户已被禁用",
                _ => "登录失败"
            }});
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

    [Authorize]
    [HttpPost("logout")]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return NoContent();
    }
}
