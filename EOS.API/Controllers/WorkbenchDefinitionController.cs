using EOS.API.Data;
using EOS.API.Errors;
using EOS.API.Models;
using EOS.API.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EOS.API.Controllers;

/// <summary>
/// Workbench Definition 快照管理：
/// 状态（已编辑但未发布）、dry-run 校验、发布、已启用模块回填。
/// 权限门：模块 2306（系统管理）CanSetup；发布校验器与流水线共用同一道闸。
/// </summary>
[ApiController, Authorize, Route("api/v1/workbench-definitions")]
public sealed class WorkbenchDefinitionController(
    WorkbenchDefinitionSnapshotService snapshots,
    IPermissionService permissions,
    EOS.API.Security.CurrentUserContext userContext) : ControllerBase
{
    /// <summary>管理端「已编辑但未发布」状态（脏标记 + 当前快照 + definitionVersion）。</summary>
    [HttpGet("status")]
    public async Task<IActionResult> Status(CancellationToken token)
    {
        await RequireSetupAsync(token);
        return Ok(await snapshots.GetStatusAsync(token));
    }

    /// <summary>快照落后检测（只读）：逐模块比对已发布快照与按当前配置重建的定义，不写快照、不动脏标记。</summary>
    [HttpGet("staleness")]
    public async Task<IActionResult> Staleness(CancellationToken token)
    {
        await RequireSetupAsync(token);
        return Ok(await snapshots.DetectStalenessAsync(token));
    }

    /// <summary>dry-run 校验（不写快照）：与发布共用同一校验器，供流水线与 CI 预检。</summary>
    [HttpPost("validate")]
    public async Task<IActionResult> Validate([FromBody] WorkbenchDefinitionValidateRequest request, CancellationToken token)
    {
        await RequireSetupAsync(token);
        return Ok(await snapshots.ValidateAsync(request.ModuleId, userContext.UserId, token));
    }

    /// <summary>发布：校验通过才写快照并清脏；失败模块保留脏标记、不发布。</summary>
    [HttpPost("publish")]
    public async Task<IActionResult> Publish([FromBody] WorkbenchDefinitionPublishRequest request, CancellationToken token)
    {
        await RequireSetupAsync(token);
        if (request.ModuleIds is null || request.ModuleIds.Count == 0 || request.ModuleIds.Count > 500)
        {
            return BadRequest(ApiProblem.Create(
                StatusCodes.Status400BadRequest, "INVALID_MODULE_IDS", "发布模块数需在 1~500 之间。"));
        }
        return Ok(await snapshots.PublishAsync(request.ModuleIds, userContext.UserId, token));
    }

    /// <summary>回填：对统一表单白名单 + 工作台承载页模块全部发布（已启用模块完成快照化）。</summary>
    [HttpPost("backfill")]
    public async Task<IActionResult> Backfill(CancellationToken token)
    {
        await RequireSetupAsync(token);
        return Ok(await snapshots.BackfillAsync(userContext.UserId, token));
    }

    private async Task RequireSetupAsync(CancellationToken token)
        => await permissions.RequireAsync(userContext.UserId, PermissionModules.SystemManagement, PermissionAction.Setup, token);
}
