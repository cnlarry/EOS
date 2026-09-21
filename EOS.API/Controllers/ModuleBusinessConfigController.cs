using EOS.API.Data;
using EOS.API.Data.ValidationRules;
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
    WorkbenchDefinitionSnapshotService snapshotService,
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
            BusinessActionCatalog.ValidationKeys.OrderBy(value => value).ToList(),
            new BusinessConfigLabelsDto(
                BusinessActionLabels.Events,
                BusinessActionLabels.FailModes,
                BusinessActionLabels.EffectKeys,
                BusinessActionLabels.OpCodes,
                BusinessActionLabels.SourceScopes,
                BusinessActionLabels.SourceAggregates,
                BusinessActionLabels.ValidationStages,
                BusinessActionLabels.ValidationKeys));
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
        return Ok(new BusinessConfigSchemasDto(
            effects,
            EffectStructSchemas.AllReverseKinds(),
            BusinessActionLabels.ReverseKinds,
            BusinessActionCatalog.ValidationKeys
                .Select(validationKey => new ValidationParamSchemaDto(
                    validationKey,
                    ValidationRuleRegistry.ParamRootKeys(validationKey).ToList()))
                .OrderBy(item => item.ValidationKey, StringComparer.OrdinalIgnoreCase)
                .ToList()));
    }

    /// <summary>
    /// 本模块涉及的表/字段中文名（渲染人话用）；缺元数据的键由前端回落显示列名本身。
    /// </summary>
    [HttpGet("{moduleId:int}/field-labels")]
    public async Task<IActionResult> FieldLabels(int moduleId, CancellationToken token)
    {
        if (!await CanBrowse(token)) return Forbid();
        return Ok(await repository.GetFieldLabelsAsync(moduleId, token));
    }

    /// <summary>
    /// 定位键候选：本模块在该目标表上已登记的效果关系边组列表。
    /// 界面按边组一键生成 MATCH_STRUCT，选出来的键必然通过保存期定位键校验。
    /// </summary>
    [HttpGet("{moduleId:int}/relations")]
    public async Task<IActionResult> Relations(
        int moduleId,
        [FromQuery] string targetTable,
        [FromQuery] string? contextTable,
        CancellationToken token)
    {
        if (!await CanBrowse(token)) return Forbid();
        var groups = await repository.GetMatchRelationsAsync(moduleId, targetTable ?? string.Empty, contextTable, token);
        return Ok(groups);
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

    /// <summary>
    /// 发布当前模块 Definition 快照（校验通过才写快照并清脏，失败保留脏标记）。
    /// 与工作台快照发布共用同一校验器与写入服务；2301 配置区提供页面内发布入口。
    /// </summary>
    [HttpPost("{moduleId:int}/publish")]
    public async Task<IActionResult> Publish(int moduleId, CancellationToken token)
    {
        if (!await CanSetup(token)) return Forbid();
        var results = await snapshotService.PublishAsync(new[] { moduleId }, userContext.EmployeeName, token);
        return Ok(results);
    }

    private async Task<bool> CanBrowse(CancellationToken token) =>
        (await rightsRepository.GetAsync(userContext.UserId, MenuAdminModuleId, token)).CanBrowse;

    private async Task<bool> CanSetup(CancellationToken token) =>
        (await rightsRepository.GetAsync(userContext.UserId, MenuAdminModuleId, token)).CanSetup;
}
