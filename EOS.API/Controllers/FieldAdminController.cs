using EOS.API.Data;
using EOS.API.Models;
using EOS.API.Security;
using Microsoft.AspNetCore.Mvc;

namespace EOS.API.Controllers;

[ApiController]
[Route("api/admin")]
public sealed class FieldAdminController(
    FieldAdminRepository repository,
    LegacyRightsRepository rightsRepository,
    CurrentUserContext userContext) : ControllerBase
{
    private const int AdminModuleId = 2302;

    [HttpGet("tables")]
    public async Task<IActionResult> Tables(CancellationToken token)
    {
        if (!await CanBrowse(token)) return Forbid();
        return Ok(await repository.GetTablesAsync(token));
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
        try { return Ok(await repository.GetFieldsAsync(table, keyword, page, pageSize, token)); }
        catch (ArgumentException exception) { return BadRequest(Problem(exception.Message)); }
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
        try
        {
            await repository.CreateAsync(request, userContext.EmployeeName, token);
            return NoContent();
        }
        catch (ArgumentException exception) { return BadRequest(Problem(exception.Message)); }
    }

    [HttpPut("fields/{table}/{field}")]
    public async Task<IActionResult> Update(string table, string field, UpdateFieldAdminRequest request, CancellationToken token)
    {
        if (!await CanSetup(token)) return Forbid();
        try
        {
            await repository.UpdateAsync(table, field, request.Field, request.Original, userContext.EmployeeName, token);
            return NoContent();
        }
        catch (ArgumentException exception) { return BadRequest(Problem(exception.Message)); }
        catch (KeyNotFoundException exception) { return NotFound(Problem(exception.Message)); }
    }

    [HttpDelete("fields/{table}/{field}")]
    public async Task<IActionResult> Delete(string table, string field, CancellationToken token)
    {
        if (!await CanSetup(token)) return Forbid();
        try { await repository.DeleteAsync(table, field, token); return NoContent(); }
        catch (ArgumentException exception) { return BadRequest(Problem(exception.Message)); }
        catch (KeyNotFoundException exception) { return NotFound(Problem(exception.Message)); }
    }

    private async Task<bool> CanBrowse(CancellationToken token) =>
        (await rightsRepository.GetAsync(userContext.UserId, AdminModuleId, token)).CanBrowse;

    private async Task<bool> CanSetup(CancellationToken token) =>
        (await rightsRepository.GetAsync(userContext.UserId, AdminModuleId, token)).CanSetup;

    private static ProblemDetails Problem(string detail) => new()
        { Title = "字段维护失败", Detail = detail };
}
