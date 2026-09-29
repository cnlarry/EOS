using System.Text.Json;
using EOS.API.Errors;
using EOS.API.Features.Assistant.Actions;
using EOS.API.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EOS.API.Controllers;

/// <summary>
/// 「操作请求卡」的两个端点：重算**只读**逐行判定、记录用户确认。
///
/// <para>
/// 三段关系必须说清：
/// <list type="number">
/// <item>**没有批量执行入口**：卡片确认之后由界面逐行调既有的批核族端点，
/// 助手侧不存在那条调用路径；</item>
/// <item>**执行主体是那次点击**：本控制器只留一条确认审计（AI 发起一条、用户确认一条），
/// 它不执行任何处置；</item>
/// <item>**权限不由这里决定**：逐行结论是给用户看的预告，服务端在真正执行时照旧独立重新授权。</item>
/// </list>
/// </para>
/// </summary>
[ApiController, Authorize, Route("api/v1/assistant/approval-requests")]
public sealed class AssistantApprovalRequestController(
    AssistantApprovalRequestService requests,
    CurrentUserContext userContext) : ControllerBase
{
    private static IActionResult Invalid(string message)
        => new BadRequestObjectResult(ApiProblem.Create(StatusCodes.Status400BadRequest, "INVALID_ARGUMENTS", message));

    /// <summary>重算逐行判定：每行给出可执行 / 不可执行及原因。只读，不产生任何写路径。</summary>
    [HttpPost("preview")]
    public async Task<IActionResult> Preview([FromBody] JsonElement arguments, CancellationToken token)
    {
        if (!TryParse(arguments, out var parsed, out var failure))
        {
            return failure!;
        }
        var preview = await requests.PreviewAsync(
            userContext.UserId, parsed!.ModuleId, parsed.Action, parsed.Rows, token);
        return Ok(AssistantApprovalDtos.ToDraft(preview));
    }

    /// <summary>记录"用户点了确认"这一件事（best-effort 审计），不执行处置。</summary>
    [HttpPost("confirm")]
    public async Task<IActionResult> Confirm([FromBody] JsonElement arguments, CancellationToken token)
    {
        if (!TryParse(arguments, out var parsed, out var failure))
        {
            return failure!;
        }
        var confirmed = await requests.ConfirmAsync(
            userContext.UserId, parsed!.ModuleId, parsed.Action, parsed.Rows, token);
        return Ok(new { confirmed });
    }

    /// <summary>
    /// 参数解析复用模型工具那一份契约：界面与模型**提交同一种形状**，两边的校验与文案不会各自漂移。
    /// 界面必须给出 <c>module_id</c>——卡片本来就知道自己在哪个模块。
    /// </summary>
    private static bool TryParse(
        JsonElement arguments, out ApprovalRequestArguments? parsed, out IActionResult? failure)
    {
        failure = null;
        parsed = AssistantApprovalRequestArguments.TryParse(arguments, out var error);
        if (parsed is null)
        {
            failure = Invalid(error);
            return false;
        }
        if (parsed.ModuleId <= 0)
        {
            failure = Invalid("缺少有效的 module_id。");
            parsed = null;
            return false;
        }
        return true;
    }
}
