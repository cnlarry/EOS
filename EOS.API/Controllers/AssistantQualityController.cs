using EOS.API.Features.Assistant.Admin;
using EOS.API.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EOS.API.Controllers;

/// <summary>
/// 工作助手质量看板：工具使用、拒绝/未知工具、工具轮数、token 与成本。
/// 只读聚合，按 3102（模型与用量）的 Setup 位授权；不返回消息正文与工具参数。
/// </summary>
[ApiController, Authorize, Route("api/v1/admin/assistant/quality")]
public sealed class AssistantQualityController(
    IAssistantQualityRepository repository,
    IPermissionService permissions,
    CurrentUserContext userContext) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] int days = 7, CancellationToken token = default)
    {
        var permission = await permissions.GetAsync(
            userContext.UserId, PermissionModules.AssistantAdmin.ModelUsage, token);
        if (permission is null || !permission.CanSetup) return Forbid();

        var window = Math.Clamp(days, 1, 90);
        var since = new DateTimeOffset(DateTime.UtcNow.Date.AddDays(-(window - 1)), TimeSpan.Zero);
        var messages = await repository.LoadMessagesAsync(since, token);
        var usage = await repository.LoadUsageAsync(since, token);
        return Ok(AssistantQualityView.Build(window, since, messages, usage));
    }
}
