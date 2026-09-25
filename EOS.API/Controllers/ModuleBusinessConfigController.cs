using EOS.API.Data;
using EOS.API.Data.DocumentActions;
using EOS.API.Data.Effects;
using EOS.API.Data.ValidationRules;
using EOS.API.Errors;
using EOS.API.Models;
using EOS.API.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EOS.API.Controllers;

/// <summary>
/// 模块业务动作/校验配置工作区读写（2301 行为动作配置区）。
/// 本控制器的端点全部属于**配置面**：读要求 CanBrowse(2301) + 模块配置权，
/// 写要求 CanSetup(2301) + 模块配置权；
/// 模块基础属性（菜单名称、承载页、默认查询列等）走另一道门（仅 SETUP_TAG）。
/// </summary>
[ApiController, Authorize, Route("api/v1/admin/module-business-config")]
public sealed class ModuleBusinessConfigController(
    ModuleBusinessConfigRepository repository,
    ModuleRightsRepository rightsRepository,
    WorkbenchDefinitionSnapshotService snapshotService,
    DocumentActionRegistry documentActions,
    DocumentActionAuthorization documentActionAuthorization,
    WorkbenchDefinitionProvider definitions,
    EffectSimulationService simulation,
    ILogger<ModuleBusinessConfigController> logger,
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
                BusinessActionLabels.ValidationKeys,
                BusinessActionLabels.EffectKeyDescriptions,
                BusinessActionLabels.ReverseKindDescriptions),
            documentActions.Keys
                .OrderBy(key => key, StringComparer.OrdinalIgnoreCase)
                .Select(key => new DocumentActionCatalogEntryDto(key, documentActions.LabelOf(key), documentActions.PlacementOf(key)))
                .ToList());
        return Ok(catalog);
    }

    /// <summary>
    /// 自定义按钮的授权镜子：每个按钮当前有几个用户 / 几个组可用。
    /// "配了没人能用"是正常状态（fail-closed），所以它不能是无声的——N=M=0 时界面要显眼提示。
    /// </summary>
    [HttpGet("{moduleId:int}/action-authorization")]
    public async Task<IActionResult> ActionAuthorization(int moduleId, CancellationToken token)
    {
        if (!await CanBrowse(token)) return Forbid();
        var config = await repository.GetAsync(moduleId, token);
        if (config is null) return NotFound();
        var buttons = config.Actions
            .Where(action => action.Enabled && BusinessActionCatalog.IsManualEvent(action.EventCode))
            .OrderBy(action => action.Seq)
            .ToList();
        var counts = await documentActionAuthorization.CountsAsync(
            moduleId, buttons.Select(action => action.EffectKey.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList(), token);
        return Ok(new DocumentActionAuthorizationMirrorDto(
            buttons.Select(action => new DocumentActionAuthorizationEntryDto(
                action.Seq,
                action.EffectKey.Trim(),
                string.IsNullOrWhiteSpace(action.Label) ? documentActions.LabelOf(action.EffectKey) : action.Label!,
                counts.TryGetValue(action.EffectKey.Trim(), out var count) ? count.Users : 0,
                counts.TryGetValue(action.EffectKey.Trim(), out var groupCount) ? groupCount.Groups : 0))
                .ToList()));
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
                .ToList(),
            // 参数深 Schema：只下发已确证语义的键，其余仍走根键展开 + 专家模式 JSON。
            EffectParamDescriptors.DescribedKeys()
                .Select(effectKey => new EffectParamFieldsDto(
                    effectKey,
                    EffectParamDescriptors.For(effectKey)
                        .Select(field => new EffectParamFieldDto(
                            field.Name, field.Type, field.Required,
                            field.EnumValues, field.Default, field.Description, field.Example))
                        .ToList()))
                .ToList(),
            // 反向兼容矩阵：每个受约束的效果键允许哪些 kind（界面据此过滤下拉）。
            EffectReverseCompatibility.ConstrainedKeys()
                .ToDictionary(
                    effectKey => effectKey,
                    effectKey => EffectReverseCompatibility.AllowedKinds(effectKey, hasFormulaRows: false),
                    StringComparer.OrdinalIgnoreCase)));
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

    /// <summary>
    /// 效果链预演：对一张真实单据在事务内跑一遍真实的生效链（含状态翻转），随后**无条件回滚**，
    /// 报告"按当前配置会发生什么"。门按写面收口：它执行的是真实 Handler，能触发它的账号越少越好。
    /// 只接受批核生效 / 解批——保存后效果要预演就得先伪造一次完整保存，代价与风险都不在本批次内。
    /// </summary>
    [HttpPost("{moduleId:int}/simulate")]
    public async Task<IActionResult> Simulate(
        int moduleId,
        EffectSimulationRequest request,
        CancellationToken token)
    {
        if (!await CanSetup(token)) return Forbid();
        var eventCode = request.Event?.Trim().ToUpperInvariant();
        if (eventCode is not ("APPROVE_EFFECT" or "DEAPPROVE"))
        {
            return BadRequest(ApiProblem.Create(StatusCodes.Status400BadRequest, "SIMULATION_EVENT_UNSUPPORTED",
                "预演仅支持 APPROVE_EFFECT（批核生效）与 DEAPPROVE（解批）。"));
        }
        if (!definitions.TryGetBaseline(moduleId, out var definition, out _))
        {
            return NotFound(ApiProblem.Create(StatusCodes.Status404NotFound, "MODULE_DEFINITION_NOT_PUBLISHED",
                "该模块还没有已发布的定义快照，请先发布后再预演。"));
        }
        if (definition.MasterPkOrder.Count == 0)
        {
            return BadRequest(ApiProblem.Create(StatusCodes.Status400BadRequest, "MODULE_NOT_SIMULATABLE",
                "该模块没有主表，无法定位单据，不支持预演。"));
        }
        if (request.Key is null || request.Key.Count != definition.MasterPkOrder.Count)
        {
            return BadRequest(ApiProblem.Create(StatusCodes.Status400BadRequest, "INVALID_RECORD_KEY",
                $"单据主键数量与模块主键不一致（需要 {definition.MasterPkOrder.Count} 个）。"));
        }

        logger.LogInformation("效果链预演 module={ModuleId} event={Event} key={Key} operator={User}",
            moduleId, eventCode, string.Join(',', request.Key), userContext.UserId);
        try
        {
            var report = await simulation.SimulateAsync(
                definition, eventCode!, request.Key,
                approve: string.Equals(eventCode, "APPROVE_EFFECT", StringComparison.Ordinal),
                userContext.UserId, token);
            return Ok(report);
        }
        catch (EffectSimulationService.TimeoutException)
        {
            return StatusCode(StatusCodes.Status504GatewayTimeout,
                ApiProblem.Create(StatusCodes.Status504GatewayTimeout, "SIMULATION_TIMEOUT",
                    "预演超时，请缩小单据范围后重试（事务已回滚，库内数据无变化）。"));
        }
    }

    /// <summary>配置面读门：页面可见 + 该模块的配置权。</summary>
    private async Task<bool> CanBrowse(CancellationToken token)
    {
        var rights = await rightsRepository.GetAsync(userContext.UserId, MenuAdminModuleId, token);
        return rights.CanBrowse && rights.CanModuleConfig;
    }

    /// <summary>配置面写门：设置权 + 该模块的配置权。</summary>
    private async Task<bool> CanSetup(CancellationToken token)
    {
        var rights = await rightsRepository.GetAsync(userContext.UserId, MenuAdminModuleId, token);
        return rights.CanSetup && rights.CanModuleConfig;
    }
}
