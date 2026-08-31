using System.Security.Claims;
using System.Text.Json;
using EOS.API.Data;
using EOS.API.Models;
using EOS.API.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EOS.API.Controllers;

/// <summary>
/// 可视化版式设计器（ADR-010 §4/§5 S2）：
/// - definition：返回内置或客户定制布局 + dataContract 字段白名单 + 权限模式（完整/微调）；
/// - save：保存校验链（ReportFormatValidator schema + 字段白名单）+ 微调模式约束 +
///   copy-on-write（首次复制内置 → REPORT_FORM_LAYOUT + REPORT_FORM_BINDING）；
/// - preview：以格式包 sample.json 样例数据渲染当前编辑布局。
/// 权限门：CanDesign（角色② 完整设计）/ CanAdjust（角色③ 微调），见 ADR-010 决策 4/5。
/// </summary>
[ApiController]
[Authorize]
[Route("api/v1/layout-designer")]
public sealed class LayoutDesignerController(
    ReportFormLayoutRepository layouts,
    ReportFormatRepository formats,
    ReportFormatValidator validator,
    ILayoutRenderer renderer,
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
        if (!mode.CanDesign && !mode.CanAdjust) return Forbid();
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
        if (!mode.CanDesign && !mode.CanAdjust) return Forbid();
        var package = formats.GetDocumentFormat(moduleId);
        if (package is null) return NotFound();

        var errors = validator.Validate(package.Format, request.LayoutJson);
        if (errors.Count > 0)
        {
            logger.LogWarning("设计器保存被校验拦截 module={ModuleId} userId={UserId} errors={Errors}",
                moduleId, userId, errors);
            return BadRequest(new { errors });
        }

        // 微调模式（仅 CanAdjust）：元素 ID/类型集合与基线一致、table 列结构不变
        if (mode.CanAdjust && !mode.CanDesign)
        {
            var baseline = await layouts.GetEffectiveLayoutAsync(moduleId, request.ClientId, userId, token);
            var adjustErrors = ValidateAdjustOnly(baseline.LayoutJson, request.LayoutJson);
            if (adjustErrors.Count > 0)
                return BadRequest(new { errors = adjustErrors });
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
        if (!mode.CanDesign && !mode.CanAdjust) return Forbid();
        var package = formats.GetDocumentFormat(moduleId);
        if (package is null || string.IsNullOrWhiteSpace(package.RawSampleJson)) return NotFound();

        var data = SamplePrintDataFactory.Expand(
            SamplePrintDataFactory.Build(
                moduleId.ToString(System.Globalization.CultureInfo.InvariantCulture), package.RawSampleJson),
            request.Rows, request.Variant);
        var pdf = renderer.Render(data, request.LayoutJson, new LayoutRenderContext(userId));
        return File(pdf, "application/pdf", "preview.pdf");
    }

    /// <summary>页头条目列表（页头字典引用，ADR-009 §9.4.2）。</summary>
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

    /// <summary>保存版式绑定（HEADER_ID/TAIL_ID/PRINT_PRICE 随绑定，ADR-010 决策 3）。</summary>
    [HttpPost("{moduleId:int}/binding")]
    public async Task<IActionResult> SaveBinding(
        int moduleId, [FromBody] LayoutBindingSaveRequest request, CancellationToken token)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null) return Unauthorized();
        var mode = await layouts.GetDesignerModeAsync(userId, moduleId, token);
        if (!mode.CanDesign && !mode.CanAdjust) return Forbid();
        await layouts.SaveBindingAsync(moduleId, userId, request, token);
        return Ok(new { saved = true });
    }

    /// <summary>微调模式约束（ADR-010 决策 5）：只允许位置/尺寸/文本/显隐/字段映射变化。</summary>
    private static List<string> ValidateAdjustOnly(string baselineJson, string editedJson)
    {
        var errors = new List<string>();
        LayoutDocument baseline;
        LayoutDocument edited;
        try
        {
            baseline = QuestPdfLayoutRenderer.Parse(baselineJson);
            edited = QuestPdfLayoutRenderer.Parse(editedJson);
        }
        catch (LayoutInvalidException ex)
        {
            errors.Add($"布局解析失败：{ex.Message}");
            return errors;
        }

        var baselineIds = AllElements(baseline).ToDictionary(e => e.Id, StringComparer.OrdinalIgnoreCase);
        var editedIds = AllElements(edited).ToDictionary(e => e.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var (id, element) in editedIds)
        {
            if (!baselineIds.TryGetValue(id, out var baseElement))
            {
                errors.Add($"微调模式不可新增元素：{id}。");
                continue;
            }
            if (!string.Equals(baseElement.Type, element.Type, StringComparison.OrdinalIgnoreCase))
                errors.Add($"微调模式不可改变元素类型：{id}（{baseElement.Type} → {element.Type}）。");
            if (baseElement.Type == "table")
            {
                var baseFields = (baseElement.Columns ?? []).Select(c => c.Field).ToList();
                var editFields = (element.Columns ?? []).Select(c => c.Field).ToList();
                if (!baseFields.SequenceEqual(editFields, StringComparer.OrdinalIgnoreCase))
                    errors.Add($"微调模式不可改变 table 列结构：{id}。");
            }
        }
        foreach (var id in baselineIds.Keys)
        {
            if (!editedIds.ContainsKey(id))
                errors.Add($"微调模式不可删除元素：{id}。");
        }
        return errors;
    }

    private static IEnumerable<LayoutElement> AllElements(LayoutDocument layout)
    {
        return layout.Sections.Header.Elements
            .Concat(layout.Sections.Content.Elements)
            .Concat(layout.Sections.Footer.Elements);
    }
}
