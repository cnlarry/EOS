using EOS.API.Data;
using EOS.API.Errors;
using EOS.API.Models;
using EOS.API.Security;
using EOS.API.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EOS.API.Controllers;

/// <summary>
/// 邮件待办端点（EOS.Mail 独立库）。与 2102"我的任务"（单据待批核）在同一展示面并列，
/// 但数据与 ERP 完全隔离。归属用户由服务端从登录会话强制读取，不接受客户端提交；
/// 所有接口只能操作当前登录用户自己的任务。
/// </summary>
[ApiController]
[Authorize]
[Route("api/v1/mail-tasks")]
public sealed class MailTasksController(
    MailTaskRepository tasks,
    CurrentUserContext userContext) : ControllerBase
{
    private const int MaxLimit = 200;

    /// <summary>列出我的邮件待办。status=open|done|cancelled|all，默认 open。</summary>
    [HttpGet]
    public async Task<IActionResult> ListMine(
        [FromQuery] string? status,
        [FromQuery] int limit = 100,
        [FromQuery] int offset = 0,
        CancellationToken token = default)
    {
        var normalized = NormalizeStatus(status);
        if (normalized is null)
        {
            return BadRequest(ApiProblem.Create(
                StatusCodes.Status400BadRequest,
                ApiErrorCodes.InvalidArgument,
                "status 必须是 open、done、cancelled 或 all"));
        }

        limit = Math.Clamp(limit, 1, MaxLimit);
        offset = Math.Max(0, offset);
        var result = await tasks.ListMineAsync(userContext.UserId, normalized, limit, offset, token);
        return Ok(result);
    }

    /// <summary>创建邮件待办（只接收任务元数据，不接收邮件正文）。</summary>
    [HttpPost]
    public async Task<IActionResult> Create(MailTaskCreateRequest request, CancellationToken token)
    {
        var errors = MailTaskInputValidator.ValidateCreate(request);
        if (errors.Count > 0)
        {
            return BadRequest(ApiProblem.Create(
                StatusCodes.Status400BadRequest,
                ApiErrorCodes.InvalidArgument,
                string.Join("；", errors)));
        }

        var created = await tasks.CreateAsync(userContext.UserId, request, token);
        return CreatedAtAction(nameof(GetMine), new { id = created.Id }, created);
    }

    [HttpGet("{id:long}")]
    public async Task<IActionResult> GetMine(long id, CancellationToken token)
    {
        var task = await tasks.GetMineAsync(userContext.UserId, id, token);
        return task is null
            ? NotFound(ApiProblem.Create(
                StatusCodes.Status404NotFound,
                ApiErrorCodes.NotFound,
                "任务不存在"))
            : Ok(task);
    }

    /// <summary>更新我的任务（标题/描述/截止时间/优先级，部分字段可选）。</summary>
    [HttpPatch("{id:long}")]
    public async Task<IActionResult> Update(long id, MailTaskUpdateRequest request, CancellationToken token)
    {
        var errors = MailTaskInputValidator.ValidateUpdate(request);
        if (errors.Count > 0)
        {
            return BadRequest(ApiProblem.Create(
                StatusCodes.Status400BadRequest,
                ApiErrorCodes.InvalidArgument,
                string.Join("；", errors)));
        }

        var updated = await tasks.UpdateAsync(userContext.UserId, id, request, token);
        return updated is null
            ? NotFound(ApiProblem.Create(
                StatusCodes.Status404NotFound,
                ApiErrorCodes.NotFound,
                "任务不存在"))
            : Ok(updated);
    }

    [HttpPost("{id:long}/complete")]
    public async Task<IActionResult> Complete(long id, CancellationToken token)
    {
        var result = await tasks.CompleteAsync(userContext.UserId, id, token);
        return result is null
            ? NotFound(ApiProblem.Create(
                StatusCodes.Status404NotFound,
                ApiErrorCodes.NotFound,
                "任务不存在或已完成"))
            : Ok(result);
    }

    [HttpPost("{id:long}/reopen")]
    public async Task<IActionResult> Reopen(long id, CancellationToken token)
    {
        var result = await tasks.ReopenAsync(userContext.UserId, id, token);
        return result is null
            ? NotFound(ApiProblem.Create(
                StatusCodes.Status404NotFound,
                ApiErrorCodes.NotFound,
                "任务不存在或未完成"))
            : Ok(result);
    }

    [HttpPost("{id:long}/cancel")]
    public async Task<IActionResult> Cancel(long id, CancellationToken token)
    {
        var result = await tasks.CancelAsync(userContext.UserId, id, token);
        return result is null
            ? NotFound(ApiProblem.Create(
                StatusCodes.Status404NotFound,
                ApiErrorCodes.NotFound,
                "任务不存在或已取消"))
            : Ok(result);
    }

    /// <summary>归一化 status 查询参数；null 表示不筛选（返回 null 表示参数非法）。</summary>
    private static string? NormalizeStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status) || string.Equals(status, "all", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return status.ToLowerInvariant() switch
        {
            "open" => "open",
            "done" => "done",
            "cancelled" => "cancelled",
            _ => null,
        };
    }
}
