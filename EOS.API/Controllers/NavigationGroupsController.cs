using EOS.API.Data;
using EOS.API.Errors;
using EOS.API.Models;
using EOS.API.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EOS.API.Controllers;

/// <summary>
/// 菜单分组（第 4 级）：读取模块启用的分组定义与分组值。
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

    [HttpGet("{moduleId:int}/groups/{index:int}/values")]
    public async Task<IActionResult> Values(int moduleId, int index, CancellationToken token)
    {
        if (index is < 1 or > 5)
            return BadRequest(ApiProblem.Create(StatusCodes.Status400BadRequest, "INVALID_GROUP_INDEX", "group index 必须在 1~5 之间。"));
        var rights = await RightsAsync(moduleId, token);
        if (rights is null) return Forbid();
        var values = await repository.GetGroupValuesAsync(moduleId, index, rights, token);
        return Ok(new { moduleId, index, values });
    }

    private async Task<ModuleRights?> RightsAsync(int moduleId, CancellationToken token)
    {
        var rights = await rightsRepository.GetAsync(userContext.UserId, moduleId, token);
        return rights.CanBrowse ? rights : null;
    }
}
