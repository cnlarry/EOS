using EOS.API.Data;
using EOS.API.Models;
using EOS.API.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EOS.API.Controllers;

/// <summary>
/// 权限管理端点（§5， 2306 用户权限设定 / 2305 用户组管理）：
/// 个人/组模块权限矩阵、个人/组报表权限矩阵、用户-组与组成员关系、
/// 字段级拒绝元数据与单模块生效值预览。
/// 所有端点要求 CanSetup(2306)（权限管理员）；写入走固定列白名单 + 受控校验 + 审计。
/// </summary>
[ApiController]
[Authorize]
[Route("api/v1/admin")]
public sealed class RightsAdminController(
    RightsAdminRepository repository,
    ModuleRightsRepository rightsRepository,
    CurrentUserContext userContext) : ControllerBase
{
    [HttpGet("users/{userId}/rights")]
    public async Task<IActionResult> UserRights(string userId, CancellationToken token)
    {
        if (!await CanSetup(token)) return Forbid();
        return Ok(await repository.GetUserModuleMatrixAsync(userContext.UserId, userId, token));
    }

    [HttpPut("users/{userId}/rights")]
    public async Task<IActionResult> SaveUserRights(string userId, SaveModuleRightsRequest request, CancellationToken token)
    {
        if (!await CanSetup(token)) return Forbid();
        await repository.SaveUserModuleRightsAsync(
            userId, request.Items, userContext.UserId, userContext.EmployeeName, token);
        return NoContent();
    }

    [HttpGet("users/{userId}/report-rights")]
    public async Task<IActionResult> UserReportRights(string userId, CancellationToken token)
    {
        if (!await CanSetup(token)) return Forbid();
        return Ok(await repository.GetUserReportMatrixAsync(userContext.UserId, userId, token));
    }

    [HttpPut("users/{userId}/report-rights")]
    public async Task<IActionResult> SaveUserReportRights(string userId, SaveReportRightsRequest request, CancellationToken token)
    {
        if (!await CanSetup(token)) return Forbid();
        await repository.SaveUserReportRightsAsync(
            userId, request.Items, userContext.UserId, userContext.EmployeeName, token);
        return NoContent();
    }

    [HttpGet("users/{userId}/groups")]
    public async Task<IActionResult> UserGroups(string userId, CancellationToken token)
    {
        if (!await CanSetup(token)) return Forbid();
        return Ok(await repository.GetUserGroupsAsync(userId, token));
    }

    [HttpPut("users/{userId}/groups")]
    public async Task<IActionResult> SaveUserGroups(string userId, SaveMembersRequest request, CancellationToken token)
    {
        if (!await CanSetup(token)) return Forbid();
        await repository.SaveUserGroupsAsync(userId, request.Ids, userContext.EmployeeName, token);
        return NoContent();
    }

    [HttpGet("groups")]
    public async Task<IActionResult> Groups(CancellationToken token)
    {
        if (!await CanSetup(token)) return Forbid();
        return Ok(await repository.GetGroupsAsync(token));
    }

    [HttpPost("groups")]
    public async Task<IActionResult> CreateGroup(CreateGroupRequest request, CancellationToken token)
    {
        if (!await CanSetup(token)) return Forbid();
        await repository.CreateGroupAsync(
            request.GroupId, request.GroupDescription, request.Remark, userContext.EmployeeName, token);
        return NoContent();
    }

    [HttpPut("groups/{groupId}")]
    public async Task<IActionResult> UpdateGroup(string groupId, UpdateGroupRequest request, CancellationToken token)
    {
        if (!await CanSetup(token)) return Forbid();
        await repository.UpdateGroupAsync(
            groupId, request.GroupDescription, request.Remark, userContext.EmployeeName, token);
        return NoContent();
    }

    [HttpDelete("groups/{groupId}")]
    public async Task<IActionResult> DeleteGroup(string groupId, CancellationToken token)
    {
        if (!await CanSetup(token)) return Forbid();
        await repository.DeleteGroupAsync(groupId, userContext.EmployeeName, token);
        return NoContent();
    }

    [HttpGet("groups/{groupId}/rights")]
    public async Task<IActionResult> GroupRights(string groupId, CancellationToken token)
    {
        if (!await CanSetup(token)) return Forbid();
        return Ok(await repository.GetGroupModuleMatrixAsync(userContext.UserId, groupId, token));
    }

    [HttpPut("groups/{groupId}/rights")]
    public async Task<IActionResult> SaveGroupRights(string groupId, SaveModuleRightsRequest request, CancellationToken token)
    {
        if (!await CanSetup(token)) return Forbid();
        await repository.SaveGroupModuleRightsAsync(
            groupId, request.Items, userContext.UserId, userContext.EmployeeName, token);
        return NoContent();
    }

    [HttpGet("groups/{groupId}/report-rights")]
    public async Task<IActionResult> GroupReportRights(string groupId, CancellationToken token)
    {
        if (!await CanSetup(token)) return Forbid();
        return Ok(await repository.GetGroupReportMatrixAsync(userContext.UserId, groupId, token));
    }

    [HttpPut("groups/{groupId}/report-rights")]
    public async Task<IActionResult> SaveGroupReportRights(string groupId, SaveReportRightsRequest request, CancellationToken token)
    {
        if (!await CanSetup(token)) return Forbid();
        await repository.SaveGroupReportRightsAsync(
            groupId, request.Items, userContext.UserId, userContext.EmployeeName, token);
        return NoContent();
    }

    [HttpGet("groups/{groupId}/members")]
    public async Task<IActionResult> GroupMembers(string groupId, CancellationToken token)
    {
        if (!await CanSetup(token)) return Forbid();
        return Ok(await repository.GetGroupMembersAsync(groupId, token));
    }

    [HttpPut("groups/{groupId}/members")]
    public async Task<IActionResult> SaveGroupMembers(string groupId, SaveMembersRequest request, CancellationToken token)
    {
        if (!await CanSetup(token)) return Forbid();
        await repository.SaveGroupMembersAsync(groupId, request.Ids, userContext.EmployeeName, token);
        return NoContent();
    }

    [HttpGet("rights/fields")]
    public async Task<IActionResult> ModuleFields([FromQuery] int moduleId, CancellationToken token)
    {
        if (!await CanSetup(token)) return Forbid();
        var rights = await rightsRepository.GetAsync(userContext.UserId, PermissionModules.SystemManagement, token);
        var fields = await repository.GetModuleFieldsAsync(
            moduleId, rights.CanViewCost, rights.CanViewSecrecy, token);
        return fields is null ? NotFound() : Ok(fields);
    }

    [HttpGet("users/{userId}/effective-rights")]
    public async Task<IActionResult> EffectiveRights(string userId, [FromQuery] int moduleId, CancellationToken token)
    {
        if (!await CanSetup(token)) return Forbid();
        var rights = await rightsRepository.GetAsync(userId, moduleId, token);
        var source = await repository.GetEffectiveSourceAsync(userId, moduleId, token);
        return Ok(new EffectiveRightsDetail(
            source, rights.ExecuteTag, rights.CanBrowse, rights.CanAddNew, rights.CanEdit, rights.CanDelete,
            rights.CanViewCost, rights.CanViewSecrecy, rights.CanSetup,
            rights.DeniedMasterFields.ToList(), rights.DeniedDetailFields.ToList(),
            rights.DenyNewMasterFields.ToList(), rights.DenyNewDetailFields.ToList(),
            rights.DenyModiMasterFields.ToList(), rights.DenyModiDetailFields.ToList(),
            rights.DataFilter));
    }

    private async Task<bool> CanSetup(CancellationToken token) =>
        (await rightsRepository.GetAsync(userContext.UserId, PermissionModules.SystemManagement, token)).CanSetup;
}
