using System.Security.Claims;
using EOS.API.Data;
using EOS.API.Models;
using EOS.API.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EOS.API.Controllers;

[ApiController]
[Authorize]
[Route("api/v1/print")]
public sealed class PrintController(
    PrintService service,
    PrintSettingsRepository printSettingsRepository,
    LegacyRightsRepository rightsRepository,
    IPermissionService permissions,
    DocumentWorkbenchRepository workbench,
    WorkbenchAuditWriter auditWriter,
    DocumentPdfService documentPdfService) : ControllerBase
{
    /// <summary>
    /// 单据 PDF（原 RptBill 的受控等价）：主表 + 明细 + 可选页头/表尾/打印备注，
    /// 生成前校验模块浏览权与报表级打印权；全部字段经服务端元数据过滤。
    /// </summary>
    [HttpPost("{moduleId:int}/pdf")]
    public async Task<IActionResult> Pdf(int moduleId, [FromBody] DocumentPdfRequest request, CancellationToken token)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null) return Unauthorized();
        var rights = (await permissions.GetAsync(userId, moduleId, token)).Rights;
        if (!rights.CanBrowse) return Forbid();
        var definition = await workbench.GetDefinitionAsync(moduleId, userId, rights.ExecuteTag,
            rights.CanViewCost, rights.CanViewSecrecy, rights.DeniedMasterFields, rights.DeniedDetailFields, token);
        if (definition is null) return NotFound();

        var settings = await printSettingsRepository.GetAsync(moduleId, userId, token);
        // 报表变体选择（对齐旧 RptBill 的 rblReport）：请求指定时白名单校验，
        // 否则按默认报表 → 首个可打印报表回退。
        var report = settings.Reports.FirstOrDefault(item =>
                !string.IsNullOrWhiteSpace(request.ReportId) && item.ReportId == request.ReportId.Trim())
            ?? settings.Reports.FirstOrDefault(item => item.IsDefault)
            ?? settings.Reports.FirstOrDefault();
        if (report is null) return Forbid();
        var reportRights = await rightsRepository.GetReportAsync(userId, moduleId, report.ReportId, token);
        if (!reportRights.CanPrint) return Forbid();

        var headerId = string.IsNullOrWhiteSpace(request.HeaderId) ? report.HeaderId : request.HeaderId.Trim();
        var tailId = string.IsNullOrWhiteSpace(request.TailId) ? report.TailId : request.TailId.Trim();
        var data = await service.GetPrintDataAsync(
            definition, request.Key, headerId, tailId,
            rights.CanViewCost, rights.CanViewSecrecy,
            rights.DeniedMasterFields, rights.DeniedDetailFields,
            CombineDataFilters(rights.DataFilter, reportRights.DataFilter), token);
        if (data is null) return NotFound();

        var header = settings.Headers.FirstOrDefault(item => item.HeaderId == headerId);
        var tail = settings.Tails.FirstOrDefault(item => item.TailId == tailId);
        var pdf = documentPdfService.Generate(
            data, header, tail?.TailText ?? data.TailText, request.ShowRemark, userId);
        await auditWriter.WriteBestEffortAsync(moduleId, string.Join(',', request.Key), "PRINT", $"打印 {data.Title}", userId, "PRINT", result: 1, null, token);
        return File(pdf, "application/pdf", $"{data.Title}.pdf");
    }

    /// <summary>合并多个 DATA_FILTER 表达式（交集收紧，§13.3 作用顺序：AND 组合）。</summary>
    private static string CombineDataFilters(params string?[] filters)
    {
        var parts = filters
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Select(f => $"({f!.Trim()})")
            .ToList();
        return parts.Count == 0 ? string.Empty : string.Join(" AND ", parts);
    }
}
