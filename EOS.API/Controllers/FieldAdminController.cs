using EOS.API.Data;
using EOS.API.Models;
using EOS.API.Security;
using Microsoft.AspNetCore.Mvc;

namespace EOS.API.Controllers;

public sealed record ValidateExpressionRequest(string Kind, string Table, string Field, string? Expression);
public sealed record PreviewExpressionRequest(string Kind, string Table, string Field, string? Expression);
public sealed record PublishExpressionRequest(string Kind, string Table, string Field, string? Expression, string? Original);

[ApiController]
[Route("api/admin")]
public sealed class FieldAdminController(
    FieldAdminRepository repository,
    LegacyRightsRepository rightsRepository,
    CurrentUserContext userContext,
    RestrictedExpressionService expressionService) : ControllerBase
{
    private const int AdminModuleId = 2302;

    [HttpGet("tables")]
    public async Task<IActionResult> Tables([FromQuery] string? kind = null, CancellationToken token = default)
    {
        if (!await CanBrowse(token)) return Forbid();
        return Ok(await repository.GetTablesAsync(kind, token));
    }

    [HttpGet("tables/{table}")]
    public async Task<IActionResult> Table(string table, CancellationToken token)
    {
        if (!await CanBrowse(token)) return Forbid();
        var detail = await repository.GetTableAsync(table, token);
        return detail is null ? NotFound() : Ok(detail);
    }

    [HttpPost("tables")]
    public async Task<IActionResult> CreateTable(CreateFieldAdminTableRequest request, CancellationToken token)
    {
        if (!await CanSetup(token)) return Forbid();
        await repository.CreateTableAsync(request, userContext.EmployeeName, token);
        return NoContent();
    }

    [HttpPut("tables/{table}")]
    public async Task<IActionResult> UpdateTable(string table, UpdateFieldAdminTableRequest request, CancellationToken token)
    {
        if (!await CanSetup(token)) return Forbid();
        await repository.UpdateTableAsync(table, request.Table, request.Original, userContext.EmployeeName, token);
        return NoContent();
    }

    [HttpDelete("tables/{table}")]
    public async Task<IActionResult> DeleteTable(string table, CancellationToken token)
    {
        if (!await CanSetup(token)) return Forbid();
        await repository.DeleteTableAsync(table, token);
        return NoContent();
    }

    [HttpGet("lookups/modules")]
    public async Task<IActionResult> LookupModules(CancellationToken token)
    {
        if (!await CanBrowse(token)) return Forbid();
        return Ok(await repository.GetModulesAsync(token));
    }

    [HttpGet("tables/{table}/fields")]
    public async Task<IActionResult> Fields(
        string table,
        [FromQuery] string? keyword = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken token = default)
    {
        if (!await CanBrowse(token)) return Forbid();
        return Ok(await repository.GetFieldsAsync(table, keyword, page, pageSize, token));
    }

    [HttpGet("tables/{table}/fields/unmanaged")]
    public async Task<IActionResult> UnmanagedFields(string table, CancellationToken token)
    {
        if (!await CanBrowse(token)) return Forbid();
        return Ok(await repository.GetUnmanagedFieldsAsync(table, token));
    }

    [HttpPost("tables/{table}/fields/batch")]
    public async Task<IActionResult> CreateUnmanagedFields(string table, CreateUnmanagedFieldsRequest request, CancellationToken token)
    {
        if (!await CanSetup(token)) return Forbid();
        if (!string.Equals(request.TableId, table, StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { code = "TABLE_MISMATCH", message = "路径表名与请求体表名不一致。" });
        return Ok(await repository.CreateUnmanagedFieldsAsync(request, userContext.EmployeeName, token));
    }

    [HttpGet("fields/{table}/{field}")]
    public async Task<IActionResult> Field(string table, string field, CancellationToken token)
    {
        if (!await CanBrowse(token)) return Forbid();
        var metadata = await repository.GetMetadataAsync(table, field, token);
        return metadata is null ? NotFound() : Ok(metadata);
    }

    [HttpPost("fields")]
    public async Task<IActionResult> Create(CreateFieldAdminRequest request, CancellationToken token)
    {
        if (!await CanSetup(token)) return Forbid();
        await repository.CreateAsync(request, userContext.EmployeeName, token);
        return NoContent();
    }

    [HttpPut("fields/{table}/{field}")]
    public async Task<IActionResult> Update(string table, string field, UpdateFieldAdminRequest request, CancellationToken token)
    {
        if (!await CanSetup(token)) return Forbid();
        await repository.UpdateAsync(table, field, request.Field, request.Original, userContext.EmployeeName, token);
        return NoContent();
    }

    /// <summary>受控表达式校验（P1）：语法 + 白名单 + 物理存在性，失败返回精确错误。</summary>
    [HttpPost("fields/expressions/validate")]
    public async Task<IActionResult> ValidateExpression(ValidateExpressionRequest request, CancellationToken token)
    {
        if (!await CanSetup(token)) return Forbid();
        if (!TryParseKind(request.Kind, out var kind))
            return BadRequest(new { code = "INVALID_EXPRESSION_KIND", message = "kind 仅支持 virtual_exp / convert_function / datasource_sql。" });
        return Ok(await expressionService.ValidateAsync(kind, request.Table, request.Field, request.Expression, token));
    }

    /// <summary>受控表达式只读预览（P2）：绑定模块/主表，TOP 20 抽样。</summary>
    [HttpPost("fields/expressions/preview")]
    public async Task<IActionResult> PreviewExpression(PreviewExpressionRequest request, CancellationToken token)
    {
        if (!await CanSetup(token)) return Forbid();
        if (!TryParseKind(request.Kind, out var kind))
            return BadRequest(new { code = "INVALID_EXPRESSION_KIND", message = "kind 仅支持 virtual_exp / convert_function / datasource_sql。" });
        return Ok(await expressionService.PreviewAsync(kind, request.Table, request.Field, request.Expression, token));
    }

    /// <summary>受控表达式发布（P2）：事务写 FIELDS + SYSDF 审计，乐观锁 + 幂等。</summary>
    [HttpPost("fields/expressions/publish")]
    public async Task<IActionResult> PublishExpression(PublishExpressionRequest request, CancellationToken token)
    {
        if (!await CanSetup(token)) return Forbid();
        if (!TryParseKind(request.Kind, out var kind))
            return BadRequest(new { code = "INVALID_EXPRESSION_KIND", message = "kind 仅支持 virtual_exp / convert_function / datasource_sql。" });
        var outcome = await expressionService.PublishAsync(
            kind, request.Table, request.Field, request.Expression, request.Original,
            userContext.EmployeeName, userContext.UserId, token);
        return outcome.Status switch
        {
            PublishExpressionStatus.Published => Ok(new { status = "published" }),
            PublishExpressionStatus.NoChange => NoContent(),
            PublishExpressionStatus.Invalid => BadRequest(new { code = "EXPRESSION_INVALID", message = string.Join("；", outcome.Errors) }),
            PublishExpressionStatus.NotFound => NotFound(new { code = "FIELD_NOT_FOUND", message = "字段元数据不存在。" }),
            _ => Conflict(new { code = "CONCURRENT_MODIFIED", message = "字段内容已被他人修改，请刷新后重试。" }),
        };
    }

    private static bool TryParseKind(string kind, out RestrictedExpressionKind parsed)
    {
        parsed = kind.Trim().ToLowerInvariant() switch
        {
            "virtual_exp" => RestrictedExpressionKind.VirtualExp,
            "convert_function" => RestrictedExpressionKind.ConvertFunction,
            "datasource_sql" => RestrictedExpressionKind.DataSourceSql,
            _ => (RestrictedExpressionKind)(-1),
        };
        return parsed != (RestrictedExpressionKind)(-1);
    }

    [HttpDelete("fields/{table}/{field}")]
    public async Task<IActionResult> Delete(string table, string field, CancellationToken token)
    {
        if (!await CanSetup(token)) return Forbid();
        await repository.DeleteAsync(table, field, token);
        return NoContent();
    }

    private async Task<bool> CanBrowse(CancellationToken token) =>
        (await rightsRepository.GetAsync(userContext.UserId, AdminModuleId, token)).CanBrowse;

    private async Task<bool> CanSetup(CancellationToken token) =>
        (await rightsRepository.GetAsync(userContext.UserId, AdminModuleId, token)).CanSetup;
}
