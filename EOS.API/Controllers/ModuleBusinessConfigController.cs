using EOS.API.Data;
using EOS.API.Models;
using EOS.API.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EOS.API.Controllers;

/// <summary>
/// 模块业务动作/校验配置工作区读写（2301 行为动作配置区）。
/// 读要求 CanBrowse(2301)，写要求 CanSetup(2301)。
/// </summary>
[ApiController, Authorize, Route("api/v1/admin/module-business-config")]
public sealed class ModuleBusinessConfigController(
    ModuleBusinessConfigRepository repository,
    ModuleRightsRepository rightsRepository,
    CurrentUserContext userContext) : ControllerBase
{
    private const int MenuAdminModuleId = 2301;

    [HttpGet("{moduleId:int}")]
    public async Task<IActionResult> Get(int moduleId, CancellationToken token)
    {
        if (!await CanBrowse(token)) return Forbid();
        var result = await repository.GetAsync(moduleId, token);
        return result is null ? NotFound() : Ok(result);
    }

    [HttpPut("{moduleId:int}")]
    public async Task<IActionResult> Save(
        int moduleId,
        SaveModuleBusinessConfigRequest request,
        CancellationToken token)
    {
        if (!await CanSetup(token)) return Forbid();
        await repository.SaveAsync(moduleId, request, userContext.EmployeeName, token);
        return NoContent();
    }

    private async Task<bool> CanBrowse(CancellationToken token) =>
        (await rightsRepository.GetAsync(userContext.UserId, MenuAdminModuleId, token)).CanBrowse;

    private async Task<bool> CanSetup(CancellationToken token) =>
        (await rightsRepository.GetAsync(userContext.UserId, MenuAdminModuleId, token)).CanSetup;
}
