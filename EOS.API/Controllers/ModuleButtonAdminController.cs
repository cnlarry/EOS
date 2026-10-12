using EOS.API.Data;
using EOS.API.Data.DocumentActions;
using EOS.API.Errors;
using EOS.API.Models;
using EOS.API.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EOS.API.Controllers;

/// <summary>
/// 自定义按钮（230103「自定义按钮」，定制承载页 `/admin/module-buttons`）：为**其它**统一工作台模块
/// 维护 `MODULE_BUSINESS_ACTION` 里 `EVENT_CODE='MANUAL'` 的行（处理器键、按钮标题、参数、确认提示）。
///
/// 权限门挂在 230103 上：读要 CanBrowse，写要 CanSetup（与 230101 模块管理、230102 模块分组同一批
/// 配置维护者，授权行按 2301 镜像）。目标模块的形态由仓库把关——只有统一工作台模块有按钮消费方。
///
/// <para>
/// **保存走显式作用域 `manual`**（[ADR-033](../../docs/decisions/ADR-033-系统功能目录与模块配置面拆分.md) §6）：
/// 删除只落在按钮行上，该模块的行为动作与校验规则一行不动——两个配置面互不覆盖，这是"拆页面"的
/// 前置机制；"两面互删"的防线由 <c>ModuleBusinessConfigScopeLiveTests</c> 在真库上钉住。
/// 作用域与请求体不一致（按钮面里塞动作行）直接拒，不做"以请求体为准"的推导，也不静默丢弃。
/// </para>
/// </summary>
[ApiController, Authorize, Route("api/v1/admin/module-buttons")]
public sealed class ModuleButtonAdminController(
    ModuleBusinessConfigRepository repository,
    ModuleRightsRepository rightsRepository,
    DocumentActionRegistry documentActions,
    DocumentActionAuthorization documentActionAuthorization,
    WorkbenchDefinitionSnapshotService snapshotService,
    WorkbenchAuditWriter auditWriter,
    CurrentUserContext userContext) : ControllerBase
{
    // 权限锚点：230103「自定义按钮」（ADR-033 §10 批 2 登记）
    private const int ModuleButtonsModuleId = 230103;

    /// <summary>
    /// 可配置的按钮处理器（键 + 默认文案 + 落位）：与配置目录里那份**同源**（同一本注册表），
    /// 页面从这里挑键——挑不到的键保存时也会被拒（注册表是封闭目录）。
    /// </summary>
    [HttpGet("meta")]
    public async Task<IActionResult> Meta(CancellationToken token)
    {
        if (!await CanBrowse(token)) return Forbid();
        var buttons = documentActions.Keys
            .OrderBy(key => key, StringComparer.OrdinalIgnoreCase)
            .Select(key => new DocumentActionCatalogEntryDto(
                key, documentActions.LabelOf(key), documentActions.PlacementOf(key)))
            .ToList();
        return Ok(new { buttons });
    }

    /// <summary>
    /// 系统级总览：全库按钮行（`MANUAL`）+ 所属模块 + 每行的授权镜子。跨模块的扁平列表用它——
    /// 只支持"先选模块"会让打开页面变成一片空态。
    /// </summary>
    [HttpGet("overview")]
    public async Task<IActionResult> Overview(CancellationToken token)
    {
        if (!await CanBrowse(token)) return Forbid();
        var overview = await repository.ReadOverviewAsync(BusinessConfigScope.Manual, token);
        return Ok(overview with
        {
            CanWrite = await CanSetup(token),
            // 授权镜子随总览一起回：扁平列表要在行上回答"配了有没有人能用"，逐模块再查一次就变成 N 个请求。
            ButtonAuthorization = await ReadButtonAuthorizationAsync(overview.Actions, token),
        });
    }

    /// <summary>
    /// 全库按钮行的授权计数，按模块分组（授权粒度是「模块 + 按钮键」，跨模块不能合并）。
    /// 只统计**启用**的行：停用的按钮点不了，它的授权数在列表上没有意义。
    /// </summary>
    private async Task<IReadOnlyList<ModuleButtonAuthorizationMirrorDto>> ReadButtonAuthorizationAsync(
        IReadOnlyList<ModuleBusinessActionOverviewDto> actions,
        CancellationToken token)
    {
        var mirrors = new List<ModuleButtonAuthorizationMirrorDto>();
        foreach (var module in actions.Where(action => action.Enabled).GroupBy(action => action.ModuleId))
        {
            var keys = module
                .Select(action => action.EffectKey.Trim())
                .Where(key => key.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var counts = await documentActionAuthorization.CountsAsync(module.Key, keys, token);
            mirrors.Add(new ModuleButtonAuthorizationMirrorDto(
                module.Key,
                module.Select(action => new DocumentActionAuthorizationEntryDto(
                    action.Seq,
                    action.EffectKey.Trim(),
                    string.IsNullOrWhiteSpace(action.Label) ? documentActions.LabelOf(action.EffectKey) : action.Label!,
                    counts.TryGetValue(action.EffectKey.Trim(), out var users) ? users.Users : 0,
                    counts.TryGetValue(action.EffectKey.Trim(), out var groups) ? groups.Groups : 0))
                    .ToList()));
        }
        return mirrors;
    }

    /// <summary>该模块的按钮行（只回 `MANUAL`；行为动作与校验规则归 230104 那一面）。</summary>
    [HttpGet("{moduleId:int}")]
    public async Task<IActionResult> Buttons(int moduleId, CancellationToken token)
    {
        if (!await CanBrowse(token)) return Forbid();
        var config = await repository.GetAsync(moduleId, token);
        if (config is null)
        {
            return NotFound(ApiProblem.Create(
                StatusCodes.Status404NotFound, ApiErrorCodes.NotFound, $"模块 {moduleId} 不存在。"));
        }
        var buttons = config.Actions
            .Where(action => BusinessActionCatalog.IsManualEvent(action.EventCode))
            .OrderBy(action => action.Seq)
            .ToList();
        return Ok(new { moduleId, buttons });
    }

    /// <summary>
    /// 按钮的授权镜子：每个按钮当前有几个用户 / 几个组可用。
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
            moduleId,
            buttons.Select(action => action.EffectKey.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            token);
        return Ok(new DocumentActionAuthorizationMirrorDto(
            buttons.Select(action => new DocumentActionAuthorizationEntryDto(
                action.Seq,
                action.EffectKey.Trim(),
                string.IsNullOrWhiteSpace(action.Label) ? documentActions.LabelOf(action.EffectKey) : action.Label!,
                counts.TryGetValue(action.EffectKey.Trim(), out var count) ? count.Users : 0,
                counts.TryGetValue(action.EffectKey.Trim(), out var groupCount) ? groupCount.Groups : 0))
                .ToList()));
    }

    /// <summary>
    /// 整面替换该模块的按钮行（作用域 `manual`）：只删按钮行、只插请求体里的行，
    /// 该模块的行为动作与校验规则一行不动。
    ///
    /// <para>
    /// **保存即发布**（ADR-033 §7.3 方案 B）：按钮进快照，生效必须过一次发布门；把这层做成用户看不见的
    /// 一步——按钮就叫「保存」，服务端在保存成功后自动发布一次。校验不通过时语义是"已保存，但未生效"，
    /// 逐条原因随 `checks` 回去（前端照 publishResult 展示位渲染）。响应体因此带发布结果，不再只是 204。
    /// </para>
    /// </summary>
    [HttpPut("{moduleId:int}")]
    public async Task<IActionResult> Save(
        int moduleId,
        SaveModuleBusinessConfigRequest? request,
        CancellationToken token)
    {
        if (!await CanSetup(token)) return Forbid();
        var buttons = request?.Actions ?? [];
        await repository.SaveAsync(
            moduleId,
            new SaveModuleBusinessConfigRequest(buttons, request?.ValidationRules ?? []),
            userContext.EmployeeName, token, BusinessConfigScope.Manual);
        await auditWriter.WriteBestEffortAsync(
            moduleId, "module-buttons", "MODULE_BUTTON_SAVE", $"保存自定义按钮 {buttons.Count} 个",
            userContext.EmployeeName, "MODULE_BUTTON",
            // 审计的 result 是 byte：行数可能超过 255，取上限而不是溢出。
            result: (byte)Math.Min(buttons.Count, byte.MaxValue), fieldChanges: null, token);
        // 保存与发布各留一条审计（"改了"与"生效了"是两件事），故发布的留痕由发布服务自己写。
        var results = await snapshotService.PublishAsync(new[] { moduleId }, userContext.EmployeeName, token);
        return Ok(new { saved = true, publish = results.FirstOrDefault() });
    }

    private async Task<bool> CanBrowse(CancellationToken token) =>
        (await rightsRepository.GetAsync(userContext.UserId, ModuleButtonsModuleId, token)).CanBrowse;

    private async Task<bool> CanSetup(CancellationToken token) =>
        (await rightsRepository.GetAsync(userContext.UserId, ModuleButtonsModuleId, token)).CanSetup;
}
