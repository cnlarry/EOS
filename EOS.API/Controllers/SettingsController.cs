using EOS.API.Data;
using EOS.API.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EOS.API.Controllers;

/// <summary>
/// Single-row parameter table settings (SYSSS / HR_SETUP / HRM_SETUP).
/// Permission gates align with the legacy pages: browse requires EXEC_TAG&lt;&gt;A and save
/// requires EDIT_TAG (resolved through the rights repository). Business logic lives in
/// SettingsRepository; this controller only authorizes and validates input.
/// </summary>
[ApiController]
[Authorize]
[Route("api/v1/settings")]
public sealed class SettingsController(
    SettingsRepository repository,
    LegacyRightsRepository rightsRepository,
    CurrentUserContext userContext) : ControllerBase
{
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
        if (values.Count > 100) return BadRequest(new { code = "TOO_MANY_FIELDS", message = "参数数量超出限制。" });

        var updated = await repository.UpdateSettingsAsync(table, values, token);
        if (updated == 0) return BadRequest(new { code = "NO_VALID_FIELDS", message = "没有可更新的参数。" });
        return NoContent();
    }

    private async Task<bool> CanBrowseAsync(int moduleId, CancellationToken token) =>
        (await rightsRepository.GetAsync(userContext.UserId, moduleId, token)).CanBrowse;

    private async Task<bool> CanEditAsync(int moduleId, CancellationToken token) =>
        (await rightsRepository.GetAsync(userContext.UserId, moduleId, token)).CanEdit;
}
