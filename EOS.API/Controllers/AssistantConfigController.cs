using System.Text.Json;
using EOS.API.Errors;
using EOS.API.Features.Assistant.Actions;
using EOS.API.Features.Assistant.Config;
using EOS.API.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EOS.API.Controllers;

/// <summary>
/// 配置改动对照卡的端点：用户勾完项后**就地**应用，或重算一次预演/自检，全程不离开助手。
///
/// <para>
/// 与操作卡同一套设计：
/// <list type="number">
/// <item>它**不是第二条写路径**——规划、应用与预演都走 <see cref="ConfigWriteService"/>，
/// 与模型调用工具时是同一段代码、同一份参数契约；</item>
/// <item>**执行主体是这次点击**：幂等键由界面本次确认给出（人不会自动重试）；</item>
/// <item>**权限不由这里决定**：每次调用都按当前用户重新授权、fail-closed，
/// 且逐类开关未放开时整类不可写。</item>
/// </list>
/// </para>
/// </summary>
[ApiController, Authorize, Route("api/v1/assistant/config-changes")]
public sealed class AssistantConfigController(
    ConfigWriteService writes,
    CurrentUserContext userContext) : ControllerBase
{
    private static IActionResult Invalid(string message)
        => new BadRequestObjectResult(ApiProblem.Create(StatusCodes.Status400BadRequest, "INVALID_ARGUMENTS", message));

    /// <summary>重算对照（含预演/自检）：返回与工具草稿同一份形状。</summary>
    [HttpPost("preview")]
    public async Task<IActionResult> Preview([FromBody] JsonElement arguments, CancellationToken token)
    {
        if (!TryParse(arguments, out var request, out var failure))
        {
            return failure!;
        }
        var plan = await writes.PlanAsync(userContext.UserId, request!, token);
        var dryRun = plan.BlockedCode is null
            ? await writes.DryRunAsync(
                plan, request!, AssistantConfigArguments.ReadItems(arguments), userContext.UserId, token)
            : null;
        return Ok(ConfigWriteDtos.ToDiff(plan, request!, dryRun));
    }

    /// <summary>
    /// 应用被勾选的改动项。幂等键走 <c>X-Idempotency-Key</c>（同一张卡重复点确认不会写两次）。
    /// </summary>
    [HttpPost("apply")]
    public async Task<IActionResult> Apply(
        [FromBody] JsonElement arguments,
        [FromHeader(Name = "X-Idempotency-Key")] string? confirmKey,
        CancellationToken token)
    {
        if (!TryParse(arguments, out var request, out var failure))
        {
            return failure!;
        }
        var items = AssistantConfigArguments.ReadItems(arguments);
        if (items is not { Count: > 0 })
        {
            return Invalid("参数 items 不能为空：请给出要应用的改动项 id（来自对照卡）。");
        }
        if (string.IsNullOrWhiteSpace(confirmKey))
        {
            return BadRequest(ApiProblem.Create(StatusCodes.Status400BadRequest, "IDEMPOTENCY_KEY_REQUIRED",
                "缺少本次确认的幂等键（X-Idempotency-Key）。"));
        }

        var result = await writes.ApplyAsync(
            userContext.UserId, userContext.EmployeeName, request!, items,
            AssistantActionKeySeed.FromUserConfirm(confirmKey.Trim()), token);
        return Ok(ConfigWriteDtos.ToApply(result));
    }

    private static bool TryParse(JsonElement arguments, out ConfigCloneRequest? request, out IActionResult? failure)
    {
        failure = null;
        request = AssistantConfigArguments.TryParse(arguments, out var error);
        if (request is not null)
        {
            return true;
        }
        failure = Invalid(error);
        return false;
    }
}
