using EOS.API.Data;
using EOS.API.Errors;
using EOS.API.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EOS.API.Controllers;

/// <summary>
/// 字段元数据审计（模块 2303 的受控只读端点）。数据访问在
/// <see cref="FieldAdminRepository.FieldAuditAsync"/>：控制器只做参数校验与授权（ADR-036 批 1 第 5 项）。
/// </summary>
[ApiController]
[Authorize]
[Route("api/v1/table-data")]
public sealed class TableDataController(
    FieldAdminRepository fieldAdmin,
    ModuleRightsRepository rightsRepository,
    CurrentUserContext userContext) : ControllerBase
{
    private const int FieldAuditModuleId = 2303;
    private const int FieldAuditLimit = 2000;

    /// <summary>
    /// 物理列与 FIELDS 元数据的差集。kind=unmanaged：物理表存在但 FIELDS 无元数据的列；
    /// kind=orphan：FIELDS 有元数据但物理表不存在的列。只读，不开放写；权限门：模块 2303 可浏览。
    /// 说明：原「数据表数据维护」（2310/2312 受控只读版 tables/data 端点）已随模块下线移除——
    /// 该页是全系统唯一绕过成本/保密/禁止字段过滤与数据范围的原始数据窗口；表结构巡检由 2302/2303 承担，
    /// 业务数据查看走各模块工作台，清库属 DBA 操作。
    /// </summary>
    [HttpGet("field-audit/{kind}")]
    public async Task<IActionResult> FieldAudit(string kind, CancellationToken token)
    {
        if (!await CanBrowseFieldAuditAsync(token)) return Forbid();
        var normalized = kind.Trim().ToLowerInvariant();
        if (normalized is not ("unmanaged" or "orphan"))
            return BadRequest(ApiProblem.Create(StatusCodes.Status400BadRequest, "INVALID_AUDIT_KIND", "kind 仅支持 unmanaged 或 orphan。"));
        var page = await fieldAdmin.FieldAuditAsync(normalized, FieldAuditLimit, token);
        return Ok(new { Kind = normalized, page.Rows, page.Total, page.Limited });
    }

    private async Task<bool> CanBrowseFieldAuditAsync(CancellationToken token) =>
        (await rightsRepository.GetAsync(userContext.UserId, FieldAuditModuleId, token)).CanBrowse;
}
