using EOS.API.Data;
using EOS.API.Models;
using EOS.API.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EOS.API.Controllers;

/// <summary>
/// 用户管理（ADR-004）：列用户 / 管理员设置密码 / 启用停用。
/// 权限门为旧系统「用户权限设定」模块 2306：读要求 CanBrowse，写要求 CanSetup。
/// </summary>
[ApiController]
[Authorize]
[Route("api/v1/admin/users")]
public sealed class UserAdminController(
    UserAdminRepository repository,
    LegacyRightsRepository rightsRepository,
    CurrentUserContext userContext) : ControllerBase
{
    private const int UserAdminModuleId = 2306;

    [HttpGet]
    public async Task<IActionResult> Users(
        [FromQuery] string? keyword = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken token = default)
    {
        if (!await CanBrowse(token)) return Forbid();
        return Ok(await repository.GetUsersAsync(keyword, page, pageSize, token));
    }

    [HttpPut("{userId}/password")]
    public async Task<IActionResult> SetPassword(string userId, SetUserPasswordRequest request, CancellationToken token)
    {
        if (!await CanSetup(token)) return Forbid();
        await repository.SetPasswordAsync(userId, request.NewPassword, userContext.EmployeeName, token);
        return NoContent();
    }

    /// <summary>新增用户（开户）：用户名 + 员工（选择器）+ 初始密码 + 可选所属组；要求 CanSetup(2306)。</summary>
    [HttpPost]
    public async Task<IActionResult> CreateUser(CreateUserRequest request, CancellationToken token)
    {
        if (!await CanSetup(token)) return Forbid();
        await repository.CreateUserAsync(
            request.UserId, request.EmployeeId, request.Password, request.GroupId,
            userContext.EmployeeName, token);
        return NoContent();
    }

    [HttpPut("{userId}/status")]
    public async Task<IActionResult> SetStatus(string userId, SetUserStatusRequest request, CancellationToken token)
    {
        if (!await CanSetup(token)) return Forbid();
        if (!request.IsActive && userContext.UserId.Equals(userId.Trim(), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("不能停用当前登录账号。");
        await repository.SetActiveAsync(userId, request.IsActive, userContext.EmployeeName, token);
        return NoContent();
    }

    private async Task<bool> CanBrowse(CancellationToken token) =>
        (await rightsRepository.GetAsync(userContext.UserId, UserAdminModuleId, token)).CanBrowse;

    private async Task<bool> CanSetup(CancellationToken token) =>
        (await rightsRepository.GetAsync(userContext.UserId, UserAdminModuleId, token)).CanSetup;
}
