using System.Security.Claims;
using EOS.API.Data;
using EOS.API.Errors;
using EOS.API.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EOS.API.Controllers;

/// <summary>
/// 页头/表尾/页脚写入维护（旧 2202 页头设置 / 2203 页尾设置 / 2204 表尾设置）：
/// 固定表与固定列白名单、标识符正则校验、值全部参数化；删除前校验 REPORT 引用。
/// 模块浏览权分别对应 2202 / 2203 / 2204。
/// </summary>
[ApiController]
[Authorize]
[Route("api/print-admin")]
public sealed class PrintAdminController(
    PrintAdminRepository repository,
    LegacyRightsRepository rightsRepository,
    IWebHostEnvironment environment) : ControllerBase
{
    [HttpGet("headers")]
    public async Task<IActionResult> Headers(CancellationToken token)
    {
        if (!await CanAccessAsync(2202, token)) return Forbid();
        return Ok(await repository.ListHeadersAsync(token));
    }

    [HttpPost("headers")]
    public async Task<IActionResult> CreateHeader([FromBody] PrintHeaderDraft draft, CancellationToken token)
    {
        if (!await CanAccessAsync(2202, token)) return Forbid();
        ValidateId(draft.HeaderId, "页头");
        var existing = await repository.ListHeadersAsync(token);
        if (existing.Any(item => item.HeaderId == draft.HeaderId.Trim())) return Conflict("页头编号已存在。");
        await repository.CreateHeaderAsync(draft, User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty, token);
        return NoContent();
    }

    [HttpPut("headers/{id}")]
    public async Task<IActionResult> UpdateHeader(string id, [FromBody] PrintHeaderDraft draft, CancellationToken token)
    {
        if (!await CanAccessAsync(2202, token)) return Forbid();
        ValidateId(id, "页头");
        var updated = await repository.UpdateHeaderAsync(id, draft, User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty, token);
        return updated ? NoContent() : NotFound();
    }

    [HttpDelete("headers/{id}")]
    public async Task<IActionResult> DeleteHeader(string id, CancellationToken token)
    {
        if (!await CanAccessAsync(2202, token)) return Forbid();
        ValidateId(id, "页头");
        var result = await repository.DeleteHeaderAsync(id, token);
        return result switch
        {
            0 => NoContent(),
            1 => NotFound(),
            _ => Conflict("该页头正被报表引用，不能删除。"),
        };
    }

    [HttpGet("tails")]
    public async Task<IActionResult> Tails(CancellationToken token)
    {
        if (!await CanAccessAsync(2204, token)) return Forbid();
        return Ok(await repository.ListTailsAsync(token));
    }

    [HttpPost("tails")]
    public async Task<IActionResult> CreateTail([FromBody] PrintTailDraft draft, CancellationToken token)
    {
        if (!await CanAccessAsync(2204, token)) return Forbid();
        ValidateId(draft.TailId, "表尾");
        var existing = await repository.ListTailsAsync(token);
        if (existing.Any(item => item.TailId == draft.TailId.Trim())) return Conflict("表尾编号已存在。");
        await repository.CreateTailAsync(draft, User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty, token);
        return NoContent();
    }

    [HttpPut("tails/{id}")]
    public async Task<IActionResult> UpdateTail(string id, [FromBody] PrintTailDraft draft, CancellationToken token)
    {
        if (!await CanAccessAsync(2204, token)) return Forbid();
        ValidateId(id, "表尾");
        var updated = await repository.UpdateTailAsync(id, draft, User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty, token);
        return updated ? NoContent() : NotFound();
    }

    [HttpDelete("tails/{id}")]
    public async Task<IActionResult> DeleteTail(string id, CancellationToken token)
    {
        if (!await CanAccessAsync(2204, token)) return Forbid();
        ValidateId(id, "表尾");
        var result = await repository.DeleteTailAsync(id, token);
        return result switch
        {
            0 => NoContent(),
            1 => NotFound(),
            _ => Conflict("该表尾正被报表引用，不能删除。"),
        };
    }

    [HttpGet("footers")]
    public async Task<IActionResult> Footers(CancellationToken token)
    {
        if (!await CanAccessAsync(2203, token)) return Forbid();
        return Ok(await repository.ListFootersAsync(token));
    }

    [HttpPost("footers")]
    public async Task<IActionResult> CreateFooter([FromBody] PrintFooterDraft draft, CancellationToken token)
    {
        if (!await CanAccessAsync(2203, token)) return Forbid();
        ValidateId(draft.FooterId, "页尾");
        var existing = await repository.ListFootersAsync(token);
        if (existing.Any(item => item.FooterId == draft.FooterId.Trim())) return Conflict("页尾编号已存在。");
        await repository.CreateFooterAsync(draft, User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty, token);
        return NoContent();
    }

    [HttpPut("footers/{id}")]
    public async Task<IActionResult> UpdateFooter(string id, [FromBody] PrintFooterDraft draft, CancellationToken token)
    {
        if (!await CanAccessAsync(2203, token)) return Forbid();
        ValidateId(id, "页尾");
        var updated = await repository.UpdateFooterAsync(id, draft, User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty, token);
        return updated ? NoContent() : NotFound();
    }

    [HttpDelete("footers/{id}")]
    public async Task<IActionResult> DeleteFooter(string id, CancellationToken token)
    {
        if (!await CanAccessAsync(2203, token)) return Forbid();
        ValidateId(id, "页尾");
        var result = await repository.DeleteFooterAsync(id, token);
        return result switch
        {
            0 => NoContent(),
            1 => NotFound(),
            _ => Conflict("该页尾正被报表引用，不能删除。"),
        };
    }

    /// <summary>页头 LOGO 上传（2202 页头设置）：PNG/JPG/GIF ≤ 2MB，存 wwwroot/print-logo。</summary>
    [HttpPost("logo")]
    public async Task<IActionResult> UploadLogo(IFormFile file, CancellationToken token)
    {
        if (!await CanAccessAsync(2202, token)) return Forbid();
        if (file is null || file.Length == 0)
            return BadRequest(ApiProblem.Create(StatusCodes.Status400BadRequest, ApiErrorCodes.InvalidArgument, "请选择要上传的图片。"));
        using var stream = new MemoryStream();
        await file.CopyToAsync(stream, token);
        var bytes = stream.ToArray();
        if (!ReportAdminValidator.IsAllowedLogo(bytes, file.FileName, out var reason))
            return BadRequest(ApiProblem.Create(StatusCodes.Status400BadRequest, ApiErrorCodes.InvalidArgument, reason));
        var root = environment.WebRootPath ?? Path.Combine(AppContext.BaseDirectory, "wwwroot");
        var directory = Path.Combine(root, "print-logo");
        Directory.CreateDirectory(directory);
        var name = $"{Guid.NewGuid():N}{Path.GetExtension(file.FileName).ToLowerInvariant()}";
        await System.IO.File.WriteAllBytesAsync(Path.Combine(directory, name), bytes, token);
        return Ok(new { logoPath = $"/print-logo/{name}" });
    }

    private async Task<bool> CanAccessAsync(int moduleId, CancellationToken token)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null) return false;
        var rights = await rightsRepository.GetAsync(userId, moduleId, token);
        return rights.CanBrowse;
    }

    private static void ValidateId(string? id, string label)
        => PrintAdminValidator.ValidateId(id, label);
}
