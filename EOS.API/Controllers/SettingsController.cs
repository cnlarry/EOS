using EOS.API.Data;
using EOS.API.Errors;
using EOS.API.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EOS.API.Controllers;

/// <summary>
/// System parameter settings. The scope segment is the page scope (<c>system</c> / <c>hr-setup</c> /
/// <c>hrm-setup</c>) and resolves to the module that owns the parameters; the legacy table names
/// still resolve to the same modules so stored configuration needs no change.
/// Permission gates follow the module configuration: browse requires EXEC_TAG&lt;&gt;A and save
/// requires EDIT_TAG (resolved through the rights repository). Reading, type conversion,
/// fail-closed validation and the audit trail live in SystemParameterService; this controller only
/// authorizes and shapes the response.
/// </summary>
[ApiController]
[Authorize]
[Route("api/v1/settings")]
public sealed class SettingsController(
    SystemParameterService parameters,
    ModuleRightsRepository rightsRepository,
    CurrentUserContext userContext) : ControllerBase
{
    private const int MaxSettingsFields = 100;

    /// <summary>Parameter list of one scope, grouped for the settings page tabs.</summary>
    [HttpGet("{scope}")]
    public async Task<IActionResult> GetSettings(string scope, CancellationToken token)
    {
        var moduleId = SystemParameterService.ModuleForScope(scope);
        if (moduleId is null) return NotFound();
        if (!await CanBrowseAsync(moduleId.Value, token)) return Forbid();
        return Ok(await parameters.ListAsync(moduleId.Value, token));
    }

    /// <summary>
    /// Saves parameters of one scope. The body is <c>{ parameterKey: value }</c>; keys that belong to
    /// another scope, unknown keys and values that do not match the declared type reject the whole
    /// submission, and every accepted change is audited with its before/after values.
    /// </summary>
    [HttpPut("{scope}")]
    public async Task<IActionResult> UpdateSettings(string scope, [FromBody] Dictionary<string, string?> values, CancellationToken token)
    {
        var moduleId = SystemParameterService.ModuleForScope(scope);
        if (moduleId is null) return NotFound();
        if (!await CanEditAsync(moduleId.Value, token)) return Forbid();
        if (values.Count > MaxSettingsFields)
            return BadRequest(ApiProblem.Create(StatusCodes.Status400BadRequest, "TOO_MANY_FIELDS", "参数数量超出限制。"));

        var result = await parameters.SaveAsync(moduleId.Value, values, userContext.UserId, token);
        if (result.Errors.Count > 0)
            return BadRequest(ApiProblem.Create(StatusCodes.Status400BadRequest, "INVALID_PARAMETERS", string.Join("；", result.Errors)));
        return NoContent();
    }

    private async Task<bool> CanBrowseAsync(int moduleId, CancellationToken token) =>
        (await rightsRepository.GetAsync(userContext.UserId, moduleId, token)).CanBrowse;

    private async Task<bool> CanEditAsync(int moduleId, CancellationToken token) =>
        (await rightsRepository.GetAsync(userContext.UserId, moduleId, token)).CanEdit;
}
