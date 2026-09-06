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

    [HttpGet("meta")]
    public async Task<IActionResult> Meta(CancellationToken token)
    {
        if (!await CanBrowse(token)) return Forbid();
        var catalog = new BusinessConfigCatalogDto(
            BusinessActionCatalog.Events.OrderBy(value => value).ToList(),
            BusinessActionCatalog.FailModes.OrderBy(value => value).ToList(),
            BusinessActionCatalog.EffectKeys.OrderBy(value => value).ToList(),
            BusinessActionCatalog.OpCodes.OrderBy(value => value).ToList(),
            BusinessActionCatalog.SourceScopes.OrderBy(value => value).ToList(),
            BusinessActionCatalog.SourceAggregates.OrderBy(value => value).ToList(),
            BusinessActionCatalog.ValidationStages.OrderBy(value => value).ToList(),
            BusinessActionCatalog.ValidationKeys.OrderBy(value => value).ToList());
        return Ok(catalog);
    }

    /// <summary>效果参数根键与反向 kind 枚举（2301 Schema 化参数编辑器数据源）。</summary>
    [HttpGet("schemas")]
    public async Task<IActionResult> Schemas(CancellationToken token)
    {
        if (!await CanBrowse(token)) return Forbid();
        var effects = BusinessActionCatalog.EffectKeys
            .Where(effectKey => EffectStructSchemas.TryGetParamRootKeys(effectKey, out _))
            .Select(effectKey => new EffectParamSchemaDto(
                effectKey,
                EffectStructSchemas.TryGetParamRootKeys(effectKey, out var keys) ? keys : []))
            .OrderBy(item => item.EffectKey, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return Ok(new BusinessConfigSchemasDto(effects, EffectStructSchemas.AllReverseKinds()));
    }

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
