using EOS.API.Data;
using EOS.API.Errors;
using EOS.API.Models;
using EOS.API.Security;
using EOS.API.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EOS.API.Controllers;

/// <summary>
/// IM 读接口与兜底写接口（历史、搜索、通讯录、建单聊、ACK、撤回）。
/// 实时收发走 SignalR Hub（/api/hubs/im）；本控制器全部走 EOS.API 授权边界。
/// </summary>
[ApiController, Authorize, Route("api/im")]
public sealed class ImController(
    IImConversationRepository conversations,
    IImMessageRepository messages,
    IImAttachmentRepository attachments,
    CurrentUserContext userContext) : ControllerBase
{
    [HttpGet("conversations")]
    public async Task<IActionResult> Conversations(CancellationToken token)
        => Ok(await conversations.GetMyConversationsAsync(userContext.UserId, token));

    [HttpPost("conversations")]
    public async Task<IActionResult> CreateDirect(ImCreateDirectRequest request, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(request.TargetUserId))
        {
            return BadRequest(ApiProblem.Create(StatusCodes.Status400BadRequest, ApiErrorCodes.InvalidArgument, "对方用户不能为空"));
        }

        var conversationId = await conversations.GetOrCreateDirectAsync(userContext.UserId, request.TargetUserId, token);
        return conversationId is null
            ? BadRequest(ApiProblem.Create(StatusCodes.Status400BadRequest, ApiErrorCodes.InvalidArgument, "不能与自己创建会话或目标用户无效"))
            : Ok(new { conversationId });
    }

    [HttpGet("conversations/{conversationId:long}/messages")]
    public async Task<IActionResult> Messages(
        long conversationId,
        [FromQuery] long? afterSeq,
        [FromQuery] long? beforeSeq,
        [FromQuery] int limit = 100,
        CancellationToken token = default)
    {
        if (!await conversations.IsActiveMemberAsync(conversationId, userContext.UserId, token))
        {
            return Forbid();
        }

        IReadOnlyList<ImMessageDto> result;
        if (afterSeq is not null)
        {
            result = await messages.GetMessagesAfterAsync(conversationId, afterSeq.Value, limit, token);
        }
        else
        {
            var before = beforeSeq ?? long.MaxValue;
            result = await messages.GetMessagesBeforeAsync(conversationId, before, limit, token);
        }

        return Ok(result);
    }

    [HttpGet("conversations/{conversationId:long}/members")]
    public async Task<IActionResult> Members(long conversationId, CancellationToken token)
    {
        if (!await conversations.IsActiveMemberAsync(conversationId, userContext.UserId, token))
        {
            return Forbid();
        }

        return Ok(await conversations.GetMembersAsync(conversationId, token));
    }

    [HttpPost("conversations/{conversationId:long}/ack")]
    public async Task<IActionResult> Ack(
        long conversationId, ImAckRequest request, CancellationToken token)
    {
        if (!await conversations.IsActiveMemberAsync(conversationId, userContext.UserId, token))
        {
            return Forbid();
        }

        if (request.LastReceivedSeq is not null)
        {
            await messages.AckReceivedAsync(conversationId, userContext.UserId, request.LastReceivedSeq.Value, token);
        }

        if (request.LastReadSeq is not null)
        {
            await messages.AckReadAsync(conversationId, userContext.UserId, request.LastReadSeq.Value, token);
        }

        return NoContent();
    }

    [HttpPost("conversations/{conversationId:long}/recall")]
    public async Task<IActionResult> Recall(
        long conversationId, ImRecallRequest request, CancellationToken token)
    {
        var result = await messages.RecallAsync(conversationId, request.MessageId, userContext.UserId, token);
        return result.Success
            ? NoContent()
            : BadRequest(ApiProblem.Create(StatusCodes.Status400BadRequest, ApiErrorCodes.InvalidArgument, result.ErrorMessage ?? "撤回失败"));
    }

    [HttpGet("search")]
    public async Task<IActionResult> Search(
        [FromQuery] string q,
        [FromQuery] long? conversationId = null,
        [FromQuery] int limit = 50,
        CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(q))
        {
            return BadRequest(ApiProblem.Create(StatusCodes.Status400BadRequest, ApiErrorCodes.InvalidArgument, "搜索关键字不能为空"));
        }

        if (conversationId is not null &&
            !await conversations.IsActiveMemberAsync(conversationId.Value, userContext.UserId, token))
        {
            return Forbid();
        }

        return Ok(await messages.SearchAsync(userContext.UserId, q.Trim(), conversationId, limit, token));
    }

    [HttpGet("contacts")]
    public async Task<IActionResult> Contacts([FromQuery] string? keyword, [FromQuery] int limit = 50, CancellationToken token = default)
        => Ok(await conversations.SearchUsersAsync(keyword ?? string.Empty, limit, token));

    [HttpPost("files")]
    [RequestSizeLimit(25 * 1024 * 1024)]
    public async Task<IActionResult> UploadFile(
        [FromForm] long conversationId,
        [FromForm] IFormFile file,
        CancellationToken token)
    {
        if (!await conversations.IsActiveMemberAsync(conversationId, userContext.UserId, token))
        {
            return Forbid();
        }

        if (file.Length == 0)
        {
            return BadRequest(ApiProblem.Create(StatusCodes.Status400BadRequest, ApiErrorCodes.InvalidArgument, "文件为空"));
        }

        if (!ImFileValidation.IsAllowed(file.Length, file.FileName, file.ContentType))
        {
            return BadRequest(ApiProblem.Create(
                StatusCodes.Status400BadRequest,
                ApiErrorCodes.InvalidArgument,
                "不支持的文件类型或大小（上限 20MB）"));
        }

        await using var stream = file.OpenReadStream();
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory, token);
        var bytes = memory.ToArray();
        var sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
        var result = await attachments.UploadAsync(
            conversationId, userContext.UserId, file.FileName, file.ContentType, bytes, sha256, token);
        return result is null
            ? StatusCode(StatusCodes.Status500InternalServerError)
            : Ok(result);
    }

    [HttpGet("files/{fileId:long}")]
    public async Task<IActionResult> DownloadFile(long fileId, CancellationToken token)
    {
        var attachment = await attachments.GetAsync(fileId, token);
        if (attachment is null)
        {
            return NotFound(ApiProblem.Create(StatusCodes.Status404NotFound, ApiErrorCodes.NotFound, "附件不存在"));
        }

        if (!await conversations.IsActiveMemberAsync(attachment.ConversationId, userContext.UserId, token))
        {
            return Forbid();
        }

        var file = await attachments.GetFileAsync(fileId, token);
        if (file is null)
        {
            return NotFound(ApiProblem.Create(StatusCodes.Status404NotFound, ApiErrorCodes.NotFound, "附件不存在"));
        }

        return File(file.Content, file.Meta.ContentType, file.Meta.FileName);
    }
}
