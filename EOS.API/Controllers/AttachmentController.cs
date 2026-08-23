using EOS.API.Data;
using EOS.API.Errors;
using EOS.API.Models;
using EOS.API.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using System.Security.Claims;
using System.Text.Json;

namespace EOS.API.Controllers;

/// <summary>
/// 统一表单（DocumentWorkbench）单据级附件端点（表 dbo.ATTACHMENT 位于 EOS.ERP 唯一业务库 + 文件系统存储）。
/// 权限门：模块浏览权限 + FILE_VIEW/UPDA/EDIT/DELE_TAG 各自服务端强制校验
/// （修正旧系统仅控制按钮不校验的漏洞）；KeyValues 为结构化主键 JSON，不信任客户端拼接。
/// 文件二进制存 Attachment:StorageRoot，元数据 + SHA-256 入库，下载经本端点授权提供。
/// </summary>
[ApiController, Authorize, Route("api/document-workbench/{moduleId:int}/attachments")]
public sealed class AttachmentController(
    DocumentWorkbenchRepository workbench,
    IPermissionService permissions,
    AttachmentRepository attachments,
    CurrentUserContext userContext,
    IOptions<AttachmentSettings> settings,
    ILogger<AttachmentController> logger) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(int moduleId, [FromQuery] string? key, CancellationToken token = default)
    {
        var access = await AttachmentAccess(moduleId, token);
        if (access is null) return Forbid();
        if (!access.Value.Permission.Can(PermissionAction.FileView)) return Forbid();
        var keyValues = ParseKey(key);
        if (keyValues is null) return BadRequest(new { code = "INVALID_RECORD_KEY", message = "key 必须是主键值数组的 JSON 编码（如 [\"A\",\"B\"]）。" });
        var items = await attachments.ListAsync(moduleId, access.Value.Definition.MasterTable, JsonSerializer.Serialize(keyValues), token);
        return Ok(items);
    }

    [HttpPost]
    [RequestSizeLimit(55 * 1024 * 1024)]
    public async Task<IActionResult> Upload(
        int moduleId,
        [FromForm] string? key,
        [FromForm] string? remark,
        [FromForm] IFormFile file,
        CancellationToken token = default)
    {
        var access = await AttachmentAccess(moduleId, token);
        if (access is null) return Forbid();
        if (!access.Value.Permission.Can(PermissionAction.FileUpload)) return Forbid();
        var keyValues = ParseKey(key);
        if (keyValues is null) return BadRequest(new { code = "INVALID_RECORD_KEY", message = "key 必须是主键值数组的 JSON 编码（如 [\"A\",\"B\"]）。" });

        if (file.Length == 0) return BadRequest(ApiProblem.Create(StatusCodes.Status400BadRequest, ApiErrorCodes.InvalidArgument, "文件为空"));
        var config = settings.Value;
        var maxBytes = config.MaxSizeBytes > 0 ? config.MaxSizeBytes : 50L * 1024 * 1024;
        if (file.Length > maxBytes) return BadRequest(ApiProblem.Create(StatusCodes.Status400BadRequest, ApiErrorCodes.InvalidArgument, $"文件大小超过上限（{maxBytes / (1024 * 1024)}MB）。"));

        var extension = Path.GetExtension(file.FileName);
        var allowed = config.AllowedExtensions
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!allowed.Contains(extension)) return BadRequest(ApiProblem.Create(StatusCodes.Status400BadRequest, ApiErrorCodes.InvalidArgument, "不支持的文件类型。"));

        var masterTable = access.Value.Definition.MasterTable;
        var serialNo = await attachments.GetNextSerialAsync(moduleId, masterTable, JsonSerializer.Serialize(keyValues), token);
        var serverFileName = $"{serialNo}{extension}";
        var relativePath = $"{SanitizeSegment(masterTable)}/{Sha256Short(JsonSerializer.Serialize(keyValues))}/{serverFileName}";

        var root = ResolveStorageRoot(config);
        var fullPath = Path.GetFullPath(Path.Combine(root, relativePath));
        var rootFull = Path.GetFullPath(root);
        if (!fullPath.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase)) return StatusCode(StatusCodes.Status400BadRequest, ApiProblem.Create(StatusCodes.Status400BadRequest, ApiErrorCodes.InvalidArgument, "非法的存储路径。"));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

        string sha256;
        await using (var stream = file.OpenReadStream())
        await using (var target = new FileStream(fullPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            using var hasher = System.Security.Cryptography.SHA256.Create();
            var buffer = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(buffer, token)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), token);
                hasher.TransformBlock(buffer, 0, read, null, 0);
            }

            hasher.TransformFinalBlock(buffer, 0, 0);
            sha256 = Convert.ToHexString(hasher.Hash ?? []).ToLowerInvariant();
        }

        var dto = await attachments.CreateAsync(
            moduleId, masterTable, JsonSerializer.Serialize(keyValues), serialNo, serverFileName,
            file.FileName, file.ContentType, file.Length, sha256, string.IsNullOrWhiteSpace(remark) ? null : remark.Trim(),
            userContext.UserId, userContext.EmployeeName, token);
        logger.LogInformation("附件上传 module={ModuleId} table={Table} serial={Serial} size={Size}", moduleId, masterTable, serialNo, file.Length);
        return Ok(dto);
    }

    [HttpGet("{id:long}/download")]
    public async Task<IActionResult> Download(int moduleId, long id, CancellationToken token = default)
    {
        var access = await AttachmentAccess(moduleId, token);
        if (access is null) return Forbid();
        if (!access.Value.Permission.Can(PermissionAction.FileView)) return Forbid();
        var meta = await attachments.GetAsync(id, token);
        if (meta is null || meta.ModuleId != moduleId) return NotFound(ApiProblem.Create(StatusCodes.Status404NotFound, ApiErrorCodes.NotFound, "附件不存在"));
        var root = ResolveStorageRoot(settings.Value);
        var relative = attachments.ResolveRelativePath(meta);
        var fullPath = Path.GetFullPath(Path.Combine(root, relative));
        var rootFull = Path.GetFullPath(root);
        if (!fullPath.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase) || !System.IO.File.Exists(fullPath))
            return NotFound(ApiProblem.Create(StatusCodes.Status404NotFound, ApiErrorCodes.NotFound, "附件文件不存在"));
        return PhysicalFile(fullPath, meta.ContentType, meta.ClientFileName);
    }

    [HttpPut("{id:long}/remark")]
    public async Task<IActionResult> UpdateRemark(int moduleId, long id, [FromBody] UpdateAttachmentRemarkRequest request, CancellationToken token = default)
    {
        var access = await AttachmentAccess(moduleId, token);
        if (access is null) return Forbid();
        if (!access.Value.Permission.Can(PermissionAction.FileEdit)) return Forbid();
        var updated = await attachments.UpdateRemarkAsync(id, request.Remark, token);
        if (updated is null || updated.ModuleId != moduleId) return NotFound(ApiProblem.Create(StatusCodes.Status404NotFound, ApiErrorCodes.NotFound, "附件不存在"));
        return Ok(updated);
    }

    [HttpDelete("{id:long}")]
    public async Task<IActionResult> Delete(int moduleId, long id, CancellationToken token = default)
    {
        var access = await AttachmentAccess(moduleId, token);
        if (access is null) return Forbid();
        if (!access.Value.Permission.Can(PermissionAction.FileDelete)) return Forbid();
        var deleted = await attachments.DeleteAsync(id, token);
        if (deleted is null || deleted.ModuleId != moduleId) return NotFound(ApiProblem.Create(StatusCodes.Status404NotFound, ApiErrorCodes.NotFound, "附件不存在"));
        var root = ResolveStorageRoot(settings.Value);
        var relative = attachments.ResolveRelativePath(deleted);
        var fullPath = Path.GetFullPath(Path.Combine(root, relative));
        if (fullPath.StartsWith(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase) && System.IO.File.Exists(fullPath))
        {
            try
            {
                System.IO.File.Delete(fullPath);
            }
            catch (IOException ex)
            {
                logger.LogWarning(ex, "附件文件删除失败（元数据已删） path={Path}", relative);
            }
        }

        logger.LogInformation("附件删除 module={ModuleId} id={Id}", moduleId, id);
        return Ok(deleted);
    }

    /// <summary>附件访问门：模块浏览权限 + 工作台定义 + 结构化 key 归属校验（记录必须真实存在）。</summary>
    private async Task<(WorkbenchDefinition Definition, ModulePermission Permission)?> AttachmentAccess(int moduleId, CancellationToken token)
    {
        var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (userId is null) return null;
        var permission = await permissions.GetAsync(userId, moduleId, token);
        if (!permission.CanBrowse) return null;
        var definition = await workbench.GetDefinitionAsync(moduleId, userId, permission.Rights.ExecuteTag, permission.Rights.CanViewCost, permission.Rights.CanViewSecrecy, permission.Rights.DeniedMasterFields, permission.Rights.DeniedDetailFields, token);
        if (definition is null) return null;
        return (definition, permission);
    }

    private static IReadOnlyList<string>? ParseKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        try
        {
            var values = JsonSerializer.Deserialize<string[]>(key);
            return values is null ? null : (IReadOnlyList<string>)values;
        }
        catch
        {
            return null;
        }
    }

    private static string ResolveStorageRoot(AttachmentSettings config)
    {
        if (!string.IsNullOrWhiteSpace(config.StorageRoot))
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(config.StorageRoot));
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "attachments"));
    }

    private static string Sha256Short(string value)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    private static string SanitizeSegment(string value)
    {
        var cleaned = System.Text.RegularExpressions.Regex.Replace(value, "[^A-Za-z0-9_-]", "_");
        return cleaned.Length == 0 ? "_" : cleaned;
    }
}

public sealed record UpdateAttachmentRemarkRequest(string? Remark);
