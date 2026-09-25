using System.Security.Claims;
using System.Text.Json;
using EOS.API.Data;
using EOS.API.Models;
using EOS.API.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EOS.API.Controllers;

/// <summary>
/// 可视化版式设计器：
/// - definition：返回内置或客户定制布局 + dataContract 字段白名单 + 权限模式（完整/微调）；
/// - save：保存校验链（ReportFormatValidator schema + 字段白名单）+ 微调模式约束 +
/// copy-on-write（首次复制内置 → REPORT_FORM_LAYOUT + REPORT_FORM_BINDING）；
/// - preview：以格式包 sample.json 样例数据渲染当前编辑布局。
/// 权限门：CanDesign（版式设计权 FORM_DESIGN_TAG），见 /5。
/// </summary>
[ApiController]
[Authorize]
[Route("api/v1/layout-designer")]
public sealed class LayoutDesignerController(
    ReportFormLayoutRepository layouts,
    ReportFormatRepository formats,
    ReportFormatValidator validator,
    ILayoutRenderer renderer,
    DocumentWorkbenchRepository workbench,
    PrintSettingsRepository printSettings,
    ModuleRightsRepository rightsRepository,
    PrintService printService,
    IPermissionService permissions,
    ILogger<LayoutDesignerController> logger) : ControllerBase
{
    /// <summary>解释层系统值白名单（与 ReportFormatValidator 同源，前端字段下拉展示）。</summary>
    private static readonly IReadOnlyList<string> SystemFields =
    [
        "SYS.TITLE", "SYS.HEADER_COMPANY", "SYS.HEADER_COMPANY_EN", "SYS.HEADER_TEXT",
        "SYS.FOOTER_TEXT", "SYS.TAIL_TEXT", "SYS.PRINT_PERSON", "SYS.TODAY",
        "SYS.CLIENT_ADDRESS_LINE", "SYS.CLIENT_NAME", "SYS.DETAIL_COUNT",
        "SYS.PAGE_NUMBER", "SYS.TOTAL_PAGES",
    ];

    [HttpGet("{moduleId:int}/definition")]
    public async Task<IActionResult> Definition(int moduleId, string? clientId, CancellationToken token)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null) return Unauthorized();
        var mode = await layouts.GetDesignerModeAsync(userId, moduleId, token);
        if (!mode.CanDesign) return Forbid();
        var package = formats.GetDocumentFormat(moduleId);
        if (package is null) return NotFound();
        var effective = await layouts.GetEffectiveLayoutAsync(moduleId, clientId, userId, token);
        return Ok(new LayoutDesignerDefinition(
            package.Format.FormatId, package.Format.Title,
            effective.IsCustom, effective.LayoutId, mode,
            effective.LayoutJson, package.Format.DataContract, SystemFields,
            effective.HeaderId, effective.TailId, effective.PrintPrice));
    }

    [HttpPost("{moduleId:int}/save")]
    public async Task<IActionResult> Save(
        int moduleId, [FromBody] LayoutDesignerSaveRequest request, CancellationToken token)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null) return Unauthorized();
        var mode = await layouts.GetDesignerModeAsync(userId, moduleId, token);
        if (!mode.CanDesign) return Forbid();
        var package = formats.GetDocumentFormat(moduleId);
        if (package is null) return NotFound();

        var validation = validator.Validate(package.Format, request.LayoutJson);
        if (!validation.Ok)
        {
            logger.LogWarning("设计器保存被校验拦截 module={ModuleId} userId={UserId} errors={Errors}",
                moduleId, userId, validation.Messages);
            return BadRequest(new { errors = validation.Messages });
        }

        var effective = await layouts.GetEffectiveLayoutAsync(moduleId, request.ClientId, userId, token);
        if (effective.IsCustom && effective.LayoutId is { } layoutId)
            await layouts.UpdateCustomLayoutAsync(layoutId, userId, request.LayoutJson, token);
        else
            await layouts.CreateCustomLayoutAsync(moduleId, userId, request.LayoutJson, request.ClientId, token);
        return Ok(new { saved = true });
    }

    [HttpPost("{moduleId:int}/preview")]
    public async Task<IActionResult> Preview(
        int moduleId, [FromBody] LayoutDesignerPreviewRequest request, CancellationToken token)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null) return Unauthorized();
        var mode = await layouts.GetDesignerModeAsync(userId, moduleId, token);
        if (!mode.CanDesign) return Forbid();
        var package = formats.GetDocumentFormat(moduleId);
        if (package is null) return NotFound();

        PrintData? data;
        if (request.Key is not null && request.Key.Count > 0)
        {
            // 真实单据预览：与打印同权限链（CanBrowse + 报表打印权 + DATA_FILTER 数据范围）
            data = await ResolveRealPrintDataAsync(moduleId, userId, request, token);
        }
        else
        {
            if (string.IsNullOrWhiteSpace(package.RawSampleJson)) return NotFound();
            data = SamplePrintDataFactory.Expand(
                SamplePrintDataFactory.Build(
                    moduleId.ToString(System.Globalization.CultureInfo.InvariantCulture), package.RawSampleJson),
                request.Rows, request.Variant);
        }
        if (data is null) return NotFound();
        var pdf = renderer.Render(data, request.LayoutJson, new LayoutRenderContext(userId));
        return File(pdf, "application/pdf", "preview.pdf");
    }

    /// <summary>真实单据取数：权限链与 PrintController.Pdf 一致（API 是最终权限边界）。</summary>
    private async Task<PrintData?> ResolveRealPrintDataAsync(
        int moduleId, string userId, LayoutDesignerPreviewRequest request, CancellationToken token)
    {
        var rights = (await permissions.GetAsync(userId, moduleId, token)).Rights;
        if (!rights.CanBrowse) return null;
        if (request.Key is not { Count: > 0 } key) return null;
        var definition = await workbench.GetDefinitionAsync(
            moduleId, userId, rights.ExecuteTag, rights.CanViewCost, rights.CanViewSecrecy,
            rights.DeniedMasterFields, rights.DeniedDetailFields, token);
        if (definition is null) return null;

        var settings = await printSettings.GetAsync(moduleId, userId, token);
        var report = settings.Reports.FirstOrDefault(item =>
                !string.IsNullOrWhiteSpace(request.ReportId) && item.ReportId == request.ReportId.Trim())
            ?? settings.Reports.FirstOrDefault(item => item.IsDefault)
            ?? settings.Reports.FirstOrDefault();
        if (report is null) return null;
        var reportRights = await rightsRepository.GetReportAsync(userId, moduleId, report.ReportId, token);
        if (!reportRights.CanPrint) return null;

        var headerId = !string.IsNullOrWhiteSpace(request.HeaderId)
            ? request.HeaderId.Trim()
            : report.HeaderId ?? string.Empty;
        var tailId = string.IsNullOrWhiteSpace(report.TailId) ? null : report.TailId.Trim();
        return await printService.GetPrintDataAsync(
            definition, key, headerId, tailId,
            rights.CanViewCost, rights.CanViewSecrecy,
            rights.DeniedMasterFields, rights.DeniedDetailFields,
            CombineDataFilters(rights.DataFilter, reportRights.DataFilter), token);
    }

    private static string CombineDataFilters(params string?[] filters)
    {
        var parts = filters
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Select(f => $"({f!.Trim()})")
            .ToList();
        return parts.Count == 0 ? string.Empty : string.Join(" AND ", parts);
    }

    /// <summary>页头条目列表（页头字典引用）。</summary>
    [HttpGet("headers")]
    public async Task<IActionResult> Headers(CancellationToken token)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null) return Unauthorized();
        // 页头列表任何可进入设计器的用户可读（保存条目需 CanDesign）
        var headers = await layouts.GetHeadersAsync(token);
        return Ok(headers);
    }

    /// <summary>保存页头条目（完整设计权限 CanDesign，角色②）。</summary>
    [HttpPost("headers")]
    public async Task<IActionResult> SaveHeader(
        [FromBody] LayoutHeaderSaveRequest request, CancellationToken token)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null) return Unauthorized();
        if (string.IsNullOrWhiteSpace(request.HeaderId) || request.HeaderId.Trim().Length > 100)
            return BadRequest(new { errors = new[] { "页头 ID 必填且不超过 100 字符。" } });
        // 权限：任一模块的 CanDesign 即可维护页头字典（页头是全局资产）
        if (!await layouts.HasAnyDesignPermissionAsync(userId, token)) return Forbid();
        await layouts.SaveHeaderAsync(request, userId, token);
        return Ok(new { saved = true });
    }

    /// <summary>保存版式绑定（HEADER_ID/TAIL_ID/PRINT_PRICE 随绑定）。</summary>
    [HttpPost("{moduleId:int}/binding")]
    public async Task<IActionResult> SaveBinding(
        int moduleId, [FromBody] LayoutBindingSaveRequest request, CancellationToken token)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null) return Unauthorized();
        var mode = await layouts.GetDesignerModeAsync(userId, moduleId, token);
        if (!mode.CanDesign) return Forbid();
        await layouts.SaveBindingAsync(moduleId, userId, request, token);
        return Ok(new { saved = true });
    }

    /// <summary>内置格式包模板清单（模板库）。</summary>
    [HttpGet("templates")]
    public IActionResult Templates()
    {
        return Ok(formats.ListTemplates());
    }

    /// <summary>模板 layout.json（套用模板 = 用其布局覆盖当前画布）。</summary>
    [HttpGet("templates/{formatId}/layout")]
    public IActionResult TemplateLayout(string formatId)
    {
        var package = formats.GetPackage(formatId);
        if (package is null) return NotFound();
        return Ok(new { layoutJson = package.RawLayoutJson });
    }

    /// <summary>当前定制版式的版本历史。</summary>
    [HttpGet("{moduleId:int}/versions")]
    public async Task<IActionResult> Versions(int moduleId, string? clientId, CancellationToken token)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null) return Unauthorized();
        var mode = await layouts.GetDesignerModeAsync(userId, moduleId, token);
        if (!mode.CanDesign) return Forbid();
        var effective = await layouts.GetEffectiveLayoutAsync(moduleId, clientId, userId, token);
        if (effective.LayoutId is not { } layoutId) return Ok(Array.Empty<LayoutVersionInfo>());
        return Ok(await layouts.GetVersionsAsync(layoutId, token));
    }

    /// <summary>回滚定制版式到指定历史版本。</summary>
    [HttpPost("{moduleId:int}/versions/{version:int}/restore")]
    public async Task<IActionResult> RestoreVersion(
        int moduleId, int version, string? clientId, CancellationToken token)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null) return Unauthorized();
        var mode = await layouts.GetDesignerModeAsync(userId, moduleId, token);
        if (!mode.CanDesign) return Forbid();
        var effective = await layouts.GetEffectiveLayoutAsync(moduleId, clientId, userId, token);
        if (effective.LayoutId is not { } layoutId) return NotFound();
        await layouts.RestoreVersionAsync(layoutId, version, userId, token);
        return Ok(new { restored = true });
    }
}
