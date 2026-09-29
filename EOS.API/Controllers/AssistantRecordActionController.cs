using System.Text.Json;
using EOS.API.Errors;
using EOS.API.Features.Assistant.Actions;
using EOS.API.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EOS.API.Controllers;

/// <summary>
/// 操作卡的执行端点：用户**就地**改完字段后重算预演、点确认执行，全程不离开助手。
///
/// <para>
/// 三段关系必须说清：
/// <list type="number">
/// <item>它**不是第二条写路径**——预演与执行都走 <see cref="AssistantRecordActionService"/>，
/// 与模型调用工具时是同一段代码、同一份参数契约（<see cref="AssistantRecordActionArguments"/>），
/// 最终都汇入统一表单的既有写管线；</item>
/// <item>**执行主体是这次点击**：幂等键由界面本次确认给出（人不会自动重试），
/// 模型可见的参数 schema 里至今没有键字段；</item>
/// <item>**权限不由这里决定**：每次调用都按当前用户重新授权、fail-closed，
/// 界面上的"能不能做"只是预告。</item>
/// </list>
/// </para>
/// </summary>
[ApiController, Authorize, Route("api/v1/assistant/record-actions")]
public sealed class AssistantRecordActionController(
    AssistantRecordActionService actions,
    CurrentUserContext userContext) : ControllerBase
{
    /// <summary>参数不合法时的统一拒绝：与模型回喂的原因同一句话。</summary>
    private static IActionResult Invalid(string message)
        => new BadRequestObjectResult(ApiProblem.Create(StatusCodes.Status400BadRequest, "INVALID_ARGUMENTS", message));

    /// <summary>预演：逐行给出可执行 / 不可执行及原因，删除另列级联影响面。不落库。</summary>
    [HttpPost("preview")]
    public async Task<IActionResult> Preview([FromBody] JsonElement arguments, CancellationToken token)
    {
        if (!TryParse(arguments, out var request, out var failure))
        {
            return failure!;
        }
        var preview = await actions.PreviewAsync(userContext.UserId, userContext.EmployeeName, request!, token);
        return Ok(AssistantActionDtos.ToPreview(request!, preview));
    }

    /// <summary>
    /// 执行：先留一条确认审计，再逐行预演通过后落库。
    /// 幂等键走 <c>X-Idempotency-Key</c>（与统一工作台其余写端点同一约定）——同一张卡重复点确认不会写两次。
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
        if (string.IsNullOrWhiteSpace(confirmKey))
        {
            return BadRequest(ApiProblem.Create(StatusCodes.Status400BadRequest, "IDEMPOTENCY_KEY_REQUIRED",
                "缺少本次确认的幂等键（X-Idempotency-Key）。"));
        }

        var execution = await actions.ConfirmAndExecuteAsync(
            userContext.UserId, userContext.EmployeeName, request!, confirmKey.Trim(), token);
        return Ok(AssistantActionDtos.ToResult(execution));
    }

    /// <summary>
    /// 参数解析复用模型工具那一份契约：界面与模型**提交同一种形状**，两边的校验与文案不会各自漂移。
    /// 界面必须给出 <c>module_id</c>——没有"按中文名猜模块"的需求（卡片本来就知道自己在哪个模块）。
    /// </summary>
    private static bool TryParse(JsonElement arguments, out AssistantActionRequest? request, out IActionResult? failure)
    {
        failure = null;
        request = AssistantRecordActionArguments.TryParse(arguments, out var error);
        if (request is null)
        {
            failure = Invalid(error);
            return false;
        }
        if (request.ModuleId <= 0)
        {
            failure = Invalid("缺少有效的 module_id。");
            request = null;
            return false;
        }
        return true;
    }
}
