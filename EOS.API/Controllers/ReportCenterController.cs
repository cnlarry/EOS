using EOS.API.Data.Reports;
using EOS.API.Errors;
using EOS.API.Models;
using EOS.API.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EOS.API.Controllers;

/// <summary>
/// 报表中心：目录（按归属模块分组 + 收藏/最近使用标记）与用户状态维护。
/// 数据访问在 <see cref="ReportCenterRepository"/>；本控制器只做参数校验与授权
/// （ADR-036 批 1 第 5 项）。可见性口径的单一真源是归属模块的 <c>REPORT_TAG</c>，
/// 与 <c>ModuleRightsRepository.GetModuleReportTagAsync</c> 同口径。
/// </summary>
[ApiController]
[Authorize]
[Route("api/v1/report-center")]
public sealed class ReportCenterController(
    ReportCenterRepository reportCenter,
    IPermissionService permissions) : ControllerBase
{
    /// <summary>报表目录：按业务域分组返回用户可见的全部报表 + 收藏/最近使用标记。</summary>
    [HttpGet("catalog")]
    public async Task<IActionResult> Catalog(CancellationToken token)
    {
        var userId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if (userId is null) return Unauthorized();
        return Ok(await reportCenter.ListCatalogAsync(userId, token));
    }

    /// <summary>
    /// 收藏开关与收藏排序：写 REPORT_USER_STATE（upsert，保持 override 收紧语义）。
    /// 只覆盖本次提交的字段——未提交的 SORT_IDX 保持原值。
    /// </summary>
    [HttpPost("favorite")]
    public async Task<IActionResult> SaveFavorite(
        [FromBody] ReportFavoriteRequest request, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(request.ReportId))
            throw new ArgumentException("报表编号不能为空。", nameof(request));
        var userId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if (userId is null) return Unauthorized();

        // 权限门：目标模块报表可见性（REPORT_TAG）——与目录查询同口径
        var permission = await permissions.GetAsync(userId, request.ModuleId, token);
        if (!permission.Rights.CanBrowse) return Forbid();

        await reportCenter.SaveFavoriteAsync(userId, request.ModuleId, request.ReportId,
            request.Favorite, request.SortIndex, token);
        return NoContent();
    }

    /// <summary>
    /// 记录「最近使用」：只写 LAST_RUN_AT，不触碰收藏开关与收藏顺序。
    /// 时间戳以服务端时钟为准（不受调用方时区/时钟影响）。
    /// </summary>
    [HttpPost("touch")]
    public async Task<IActionResult> Touch(
        [FromBody] ReportTouchRequest request, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(request.ReportId))
            throw new ArgumentException("报表编号不能为空。", nameof(request));
        var userId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if (userId is null) return Unauthorized();

        // 权限门：目标模块报表可见性（REPORT_TAG）——与目录查询同口径
        var permission = await permissions.GetAsync(userId, request.ModuleId, token);
        if (!permission.Rights.CanBrowse) return Forbid();

        await reportCenter.TouchAsync(userId, request.ModuleId, request.ReportId, token);
        return NoContent();
    }

    /// <summary>
    /// 收藏重排（P5）：一次性写入完整收藏顺序，SORT_IDX=1..N 顺次落库。
    /// 权限逐项**先校验完**再落库：任一不可见即整体拒绝，不会写到一半才发现越权。
    /// </summary>
    [HttpPost("reorder")]
    public async Task<IActionResult> Reorder(
        [FromBody] ReportReorderRequest request, CancellationToken token)
    {
        var userId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if (userId is null) return Unauthorized();
        if (request.Items is null || request.Items.Count == 0)
            return BadRequest(ApiProblem.Create(StatusCodes.Status400BadRequest, "INVALID_ORDER", "收藏顺序不能为空。"));

        var items = new List<(int ModuleId, string ReportId)>(request.Items.Count);
        foreach (var item in request.Items)
        {
            if (string.IsNullOrWhiteSpace(item.ReportId)) continue;
            // 权限门：目标模块报表可见性（REPORT_TAG）
            var permission = await permissions.GetAsync(userId, item.ModuleId, token);
            if (!permission.Rights.CanBrowse) return Forbid();
            items.Add((item.ModuleId, item.ReportId.Trim()));
        }
        if (items.Count == 0)
            return BadRequest(ApiProblem.Create(StatusCodes.Status400BadRequest, "INVALID_ORDER", "收藏顺序不能为空。"));

        await reportCenter.ReorderAsync(userId, items, token);
        return NoContent();
    }
}
