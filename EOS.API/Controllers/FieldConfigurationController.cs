using EOS.API.Data;
using EOS.API.Errors;
using EOS.API.Models;
using EOS.API.Security;
using Microsoft.AspNetCore.Mvc;

namespace EOS.API.Controllers;

[ApiController]
[Route("api/modules/1204/fields")]
public sealed class FieldConfigurationController(
    FieldConfigurationRepository repository,
    LegacyRightsRepository rightsRepository,
    CurrentUserContext userContext) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<FieldConfigurationResult>> Get(
        [FromQuery] string scope = "master",
        CancellationToken cancellationToken = default)
    {
        var rights = await rightsRepository.GetAsync(userContext.UserId, 1204, cancellationToken);
        if (!rights.CanBrowse) return Forbid();
        var deniedFields = scope == "detail" ? rights.DeniedDetailFields : rights.DeniedMasterFields;
        return Ok(await repository.GetAsync(
            userContext.UserId,
            scope,
            rights.CanViewCost,
            rights.CanViewSecrecy,
            deniedFields,
            cancellationToken));
    }

    [HttpDelete]
    public async Task<IActionResult> RestoreDefault(
        SaveFieldConfigurationRequest request,
        CancellationToken cancellationToken)
    {
        await repository.RestoreDefaultAsync(
            userContext.UserId,
            request.Scope,
            cancellationToken);
        return NoContent();
    }

    [HttpPut]
    public async Task<IActionResult> Save(
        SaveFieldConfigurationRequest request,
        CancellationToken cancellationToken)
    {
        if (request.FieldIds.Count > 100)
        {
            return BadRequest(ApiProblem.Create(
                StatusCodes.Status400BadRequest,
                ApiErrorCodes.InvalidArgument,
                "单个表格最多允许选择 100 个字段。"));
        }

        var rights = await rightsRepository.GetAsync(userContext.UserId, 1204, cancellationToken);
        if (!rights.CanBrowse) return Forbid();
        var deniedFields = request.Scope == "detail" ? rights.DeniedDetailFields : rights.DeniedMasterFields;
        await repository.SaveAsync(
            userContext.UserId,
            request.Scope,
            request.FieldIds,
            rights.CanViewCost,
            rights.CanViewSecrecy,
            deniedFields,
            cancellationToken);
        return NoContent();
    }
}

