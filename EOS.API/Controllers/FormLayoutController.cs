using EOS.API.Data;
using EOS.API.Data.Forms;
using EOS.API.Errors;
using EOS.API.Models;
using EOS.API.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EOS.API.Controllers;

/// <summary>
/// 模块级表单版式的设计态端点。
///
/// 权限门是<b>版式设计权</b>（FORM_DESIGN_TAG），与模块配置权平级——服务端独立鉴权，
/// 前端是否渲染入口只影响体验；版式只做减法与排布，不参与授权（权限过滤仍在字段选择器）。
/// 写端点满足配置类写端点准入门槛：幂等键 + 审计与业务同事务（审计写在同一事务内）。
/// </summary>
[ApiController]
[Authorize]
[Route("api/v1/admin/form-layout")]
public sealed class FormLayoutController(
    FormLayoutRepository repository,
    IPermissionService permissions,
    CurrentUserContext userContext) : ControllerBase
{
    /// <summary>设计态读取：当前版式 + 字段池（字段池带"当前用户不可见"标记供锁图标）。</summary>
    [HttpGet("{moduleId:int}")]
    public async Task<IActionResult> Get(int moduleId, CancellationToken token)
    {
        if (!await repository.CanDesignAsync(userContext.UserId, moduleId, token))
        {
            return Forbid();
        }
        var state = await LoadStateAsync(moduleId, token);
        return state is null
            ? NotFound(ApiProblem.NotFound($"模块 {moduleId} 没有可设计的统一表单。", "FORM_LAYOUT_MODULE_NOT_FOUND"))
            : Ok(state);
    }

    /// <summary>可套用来源：只返回主表相同的模块（供"套用其它模块版式"）。</summary>
    [HttpGet("{moduleId:int}/templates")]
    public async Task<IActionResult> Templates(int moduleId, CancellationToken token)
    {
        if (!await repository.CanDesignAsync(userContext.UserId, moduleId, token))
        {
            return Forbid();
        }
        return Ok(await repository.ReadTemplatesAsync(moduleId, token));
    }

    /// <summary>保存整份版式（全量替换）：校验 → 写表 → 标脏 → 重发布；任一步失败整笔回滚。</summary>
    [HttpPut("{moduleId:int}")]
    public async Task<IActionResult> Save(
        int moduleId, FormLayoutSaveRequest request, CancellationToken token)
    {
        if (!await repository.CanDesignAsync(userContext.UserId, moduleId, token))
        {
            return Forbid();
        }
        var (key, problem) = ResolveIdempotencyKey(request.IdempotencyKey);
        if (problem is not null)
        {
            return problem;
        }
        var outcome = await repository.SaveAsync(
            moduleId, request with { IdempotencyKey = key }, userContext.UserId, userContext.EmployeeName, token);
        return await RespondAsync(moduleId, outcome, token);
    }

    /// <summary>重置为默认版式：删除该模块两表全部行，回到按字段级配置推导。</summary>
    [HttpPost("{moduleId:int}/reset")]
    public async Task<IActionResult> Reset(
        int moduleId, [FromBody] FormLayoutResetRequest? request, CancellationToken token)
    {
        if (!await repository.CanDesignAsync(userContext.UserId, moduleId, token))
        {
            return Forbid();
        }
        var (key, problem) = ResolveIdempotencyKey(request?.IdempotencyKey);
        if (problem is not null)
        {
            return problem;
        }
        var outcome = await repository.ResetAsync(
            moduleId, key, request?.BaseUpdatedAt, userContext.UserId, userContext.EmployeeName, token);
        return await RespondAsync(moduleId, outcome, token);
    }

    private async Task<FormLayoutDesignState?> LoadStateAsync(int moduleId, CancellationToken token)
    {
        var rights = (await permissions.GetAsync(userContext.UserId, moduleId, token)).Rights;
        return await repository.ReadDesignStateAsync(moduleId, rights, token);
    }

    private async Task<IActionResult> RespondAsync(
        int moduleId, FormLayoutSaveOutcome outcome, CancellationToken token)
    {
        switch (outcome.Status)
        {
            case FormLayoutSaveStatus.Saved:
            case FormLayoutSaveStatus.Replayed:
            {
                var state = await LoadStateAsync(moduleId, token);
                return Ok(new FormLayoutSaveResponse(
                    outcome.Status == FormLayoutSaveStatus.Saved ? "saved" : "replayed",
                    outcome.Message,
                    outcome.DefinitionVersion,
                    state));
            }
            case FormLayoutSaveStatus.ModuleNotFound:
                return NotFound(ApiProblem.NotFound(
                    outcome.Message ?? "模块不存在。", "FORM_LAYOUT_MODULE_NOT_FOUND"));
            case FormLayoutSaveStatus.Conflict:
                return Conflict(ApiProblem.Create(
                    StatusCodes.Status409Conflict, "FORM_LAYOUT_CONFLICT",
                    outcome.Message ?? "版式已被他人修改，请刷新后重试。"));
            case FormLayoutSaveStatus.LayoutInvalid:
                return BadRequest(ApiProblem.Create(
                    StatusCodes.Status400BadRequest, "FORM_LAYOUT_INVALID",
                    outcome.Message ?? "版式不合规，未保存。",
                    outcome.LayoutIssues?
                        .GroupBy(issue => issue.Key ?? string.Empty)
                        .ToDictionary(group => group.Key, group => group.Select(issue => issue.Message).ToArray())));
            default:
                return BadRequest(ApiProblem.Create(
                    StatusCodes.Status400BadRequest, "FORM_LAYOUT_PUBLISH_FAILED",
                    outcome.Message ?? "定义重发布被拦截，改动已回滚。",
                    outcome.PublishChecks?
                        .Where(check => !check.Passed)
                        .GroupBy(_ => "definition")
                        .ToDictionary(
                            group => group.Key,
                            group => group.Select(check => $"{check.Code}: {check.Message}").ToArray())));
        }
    }

    /// <summary>幂等键：请求体或 X-Idempotency-Key 请求头，≤128 字符，缺失即拒绝。</summary>
    private (string? Key, IActionResult? Problem) ResolveIdempotencyKey(string? bodyKey)
    {
        var header = Request.Headers.TryGetValue("X-Idempotency-Key", out var values)
            ? values.ToString()
            : null;
        var key = string.IsNullOrWhiteSpace(header) ? bodyKey?.Trim() : header.Trim();
        if (string.IsNullOrWhiteSpace(key) || key.Length > 128)
        {
            return (null, BadRequest(ApiProblem.Create(
                StatusCodes.Status400BadRequest, "IDEMPOTENCY_KEY_REQUIRED",
                "写操作缺少有效幂等键（请求体 idempotencyKey 或 X-Idempotency-Key 请求头，≤128 字符）。")));
        }
        return (key, null);
    }
}
