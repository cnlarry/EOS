using EOS.API.Data;
using EOS.API.Models;
using EOS.API.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EOS.API.Controllers;

/// <summary>
/// 菜单分组（第 4 级）：读取模块的分组定义与分组值（分组配置见 2315 模块分组）。
/// 权限：与模块浏览权限一致（CanBrowse）；GROUP_EXP 不可受控解析时返回 403。
/// </summary>
[ApiController, Authorize, Route("api/v1/navigation")]
public sealed class NavigationGroupsController(
    NavigationGroupsRepository repository,
    ModuleRightsRepository rightsRepository,
    CurrentUserContext userContext) : ControllerBase
{
    [HttpGet("{moduleId:int}/groups")]
    public async Task<IActionResult> Groups(int moduleId, CancellationToken token)
    {
        var rights = await RightsAsync(moduleId, token);
        if (rights is null) return Forbid();
        var groups = await repository.GetGroupsAsync(moduleId, rights, token);
        return Ok(new { moduleId, groups });
    }

    [HttpGet("{moduleId:int}/groups/{groupId:int}/values")]
    public async Task<IActionResult> Values(int moduleId, int groupId, CancellationToken token)
    {
        var rights = await RightsAsync(moduleId, token);
        if (rights is null) return Forbid();
        var values = await repository.GetGroupValuesAsync(moduleId, groupId, rights, token);
        return Ok(new { moduleId, groupId, values });
    }

    private async Task<ModuleRights?> RightsAsync(int moduleId, CancellationToken token)
    {
        var rights = await rightsRepository.GetAsync(userContext.UserId, moduleId, token);
        return rights.CanBrowse ? rights : null;
    }
}
