using EOS.API.Data;
using EOS.API.Errors;
using EOS.API.Models;
using EOS.API.Security;
using Microsoft.AspNetCore.Mvc;

namespace EOS.API.Controllers;

[ApiController]
[Route("api/v1/modules/1204")]
public sealed class BomController(
    BomRepository repository,
    DynamicBomRepository dynamicRepository,
    LegacyRightsRepository rightsRepository,
    CurrentUserContext userContext) : ControllerBase
{
    [HttpGet("identity")]
    public async Task<ActionResult<object>> GetIdentity(CancellationToken cancellationToken)
    {
        var rights = await rightsRepository.GetAsync(userContext.UserId, 1204, cancellationToken);
        return Ok(new { userId = userContext.UserId, moduleId = 1204, canSetup = rights.CanSetup });
    }

    [HttpGet("grid/boms")]
    public async Task<ActionResult<DynamicGridResult>> DynamicSearch(
        [FromQuery] string field = "proNo",
        [FromQuery] string keyword = "",
        [FromQuery] int limit = 100,
        CancellationToken cancellationToken = default)
    {
        var rights = await rightsRepository.GetAsync(userContext.UserId, 1204, cancellationToken);
        if (!rights.CanBrowse) return Forbid();
        return Ok(await dynamicRepository.SearchMasterAsync(
            userContext.UserId,
            field,
            keyword ?? string.Empty,
            limit,
            rights.CanViewCost,
            rights.CanViewSecrecy,
            rights.DeniedMasterFields,
            cancellationToken));
    }

    [HttpGet("grid/boms/{proNo}/details")]
    public async Task<ActionResult<DynamicGridResult>> DynamicDetails(
        string proNo,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(proNo) || proNo.Length > 30)
            return BadRequest();
        var rights = await rightsRepository.GetAsync(userContext.UserId, 1204, cancellationToken);
        if (!rights.CanBrowse) return Forbid();
        return Ok(await dynamicRepository.GetDetailsAsync(
            userContext.UserId,
            proNo,
            rights.CanViewCost,
            rights.CanViewSecrecy,
            rights.DeniedDetailFields,
            cancellationToken));
    }

    [HttpGet("boms")]
    public async Task<ActionResult<BomSearchResult>> Search(
        [FromQuery] string field = "proNo",
        [FromQuery] string keyword = "",
        [FromQuery] int limit = 100,
        CancellationToken cancellationToken = default)
    {
        _ = userContext.UserId;
        var normalizedField = field is "proNo" or "proName" or "proSpec" or "elementProNo"
            ? field
            : "proNo";
        var normalizedLimit = Math.Clamp(limit, 1, 200);
        var rows = await repository.SearchAsync(
            normalizedField,
            keyword ?? string.Empty,
            normalizedLimit,
            cancellationToken);

        return Ok(new BomSearchResult(
            rows,
            rows.Count,
            normalizedLimit,
            normalizedField,
            keyword ?? string.Empty));
    }

    [HttpGet("boms/{proNo}/details")]
    public async Task<ActionResult<IReadOnlyList<BomDetailRow>>> Details(
        string proNo,
        CancellationToken cancellationToken)
    {
        _ = userContext.UserId;
        if (string.IsNullOrWhiteSpace(proNo) || proNo.Length > 30)
        {
            return BadRequest(ApiProblem.Create(
                StatusCodes.Status400BadRequest,
                ApiErrorCodes.InvalidArgument,
                "产品料号不能为空且长度不能超过 30。"));
        }

        return Ok(await repository.GetDetailsAsync(proNo, cancellationToken));
    }
}

