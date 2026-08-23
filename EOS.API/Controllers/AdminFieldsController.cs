using EOS.API.Data;
using EOS.API.Models;
using EOS.API.Security;
using Microsoft.AspNetCore.Mvc;

namespace EOS.API.Controllers;

[ApiController]
[Route("api/v1/modules/1204/admin/fields")]
public sealed class AdminFieldsController(
    AdminFieldRepository repository,
    LegacyRightsRepository rightsRepository,
    CurrentUserContext userContext) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<AdminFieldDefinition>>> Get(
        [FromQuery] string table = "BOM_STRU_M",
        CancellationToken cancellationToken = default)
    {
        if (!await CanSetup(cancellationToken)) return Forbid();
        return Ok(await repository.GetFieldsAsync(table, cancellationToken));
    }

    [HttpPut]
    public async Task<IActionResult> Update(
        AdminFieldUpdateRequest request,
        CancellationToken cancellationToken)
    {
        if (!await CanSetup(cancellationToken)) return Forbid();
        await repository.UpdateAsync(request, userContext.UserId, cancellationToken);
        return NoContent();
    }

    [HttpPut("defaults")]
    public async Task<IActionResult> SaveDefaults(
        DefaultColumnRequest request,
        CancellationToken cancellationToken)
    {
        if (!await CanSetup(cancellationToken)) return Forbid();
        await repository.SaveDefaultsAsync(request, cancellationToken);
        return NoContent();
    }

    private async Task<bool> CanSetup(CancellationToken cancellationToken) =>
        (await rightsRepository.GetAsync(userContext.UserId, 1204, cancellationToken)).CanSetup;
}

