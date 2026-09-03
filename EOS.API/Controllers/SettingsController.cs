using EOS.API.Data;
using EOS.API.Errors;
using EOS.API.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EOS.API.Controllers;

/// <summary>
/// Single-row parameter table settings (SYSSS / HR_SETUP / HRM_SETUP).
/// Permission gates follow the module configuration: browse requires EXEC_TAG&lt;&gt;A and save
/// requires EDIT_TAG (resolved through the rights repository). Business logic lives in
/// SettingsRepository; this controller only authorizes and validates input.
/// </summary>
[ApiController]
[Authorize]
[Route("api/v1/settings")]
public sealed class SettingsController(
    SettingsRepository repository,
    ModuleRightsRepository rightsRepository,
    CurrentUserContext userContext) : ControllerBase
{
    private const int MaxSettingsFields = 100;

    [HttpGet("{table}")]
    public async Task<IActionResult> GetSettings(string table, CancellationToken token)
    {
        var moduleId = SettingsRepository.PermissionModuleId(table);
        if (moduleId is null) return NotFound();
        if (!await CanBrowseAsync(moduleId.Value, token)) return Forbid();
        var (fields, values) = await repository.GetSettingsAsync(table, token);
        return Ok(new { values, fields });
    }

    [HttpPut("{table}")]
    public async Task<IActionResult> UpdateSettings(string table, [FromBody] Dictionary<string, string?> values, CancellationToken token)
    {
        var moduleId = SettingsRepository.PermissionModuleId(table);
        if (moduleId is null) return NotFound();
        if (!await CanEditAsync(moduleId.Value, token)) return Forbid();
        if (values.Count > MaxSettingsFields) return BadRequest(ApiProblem.Create(StatusCodes.Status400BadRequest, "TOO_MANY_FIELDS", "参数数量超出限制。"));

        var updated = await repository.UpdateSettingsAsync(table, values, token);
        if (updated == 0) return BadRequest(ApiProblem.Create(StatusCodes.Status400BadRequest, "NO_VALID_FIELDS", "没有可更新的参数。"));
        return NoContent();
    }

    private async Task<bool> CanBrowseAsync(int moduleId, CancellationToken token) =>
        (await rightsRepository.GetAsync(userContext.UserId, moduleId, token)).CanBrowse;

    private async Task<bool> CanEditAsync(int moduleId, CancellationToken token) =>
        (await rightsRepository.GetAsync(userContext.UserId, moduleId, token)).CanEdit;
}
