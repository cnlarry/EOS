using System.Text.RegularExpressions;
using EOS.API.Models;

namespace EOS.API.Data;

/// <summary>
/// 版式保存校验器（ADR-010 决策 4「保存即校验」，S1 交付物）：
/// JSON Schema 级校验（格式合法、坐标在页内、元素数/文件大小上限）+
/// 字段引用白名单校验（MASTER.* / DETAILS.* 必须 ∈ format.json dataContract，
/// SYS.* 必须 ∈ 解释层系统值白名单）。渲染时由 QuestPdfLayoutRenderer 二次 fail-closed。
/// 白名单来源是服务端 format.json，非前端提交。
/// </summary>
public sealed class ReportFormatValidator
{
    public const int DefaultMaxLayoutBytes = 256 * 1024;
    public const int DefaultMaxElements = 200;

    private static readonly Regex TemplatePattern = new(
        @"\{\{([A-Za-z][A-Za-z0-9_.]*)\}\}", RegexOptions.Compiled);

    /// <summary>解释层系统值白名单（对应 QuestPdfLayoutRenderer.ResolveSystemValue + 页码/总页）。</summary>
    private static readonly HashSet<string> SystemReferences = new(StringComparer.OrdinalIgnoreCase)
    {
        "TITLE", "HEADER_COMPANY", "HEADER_COMPANY_EN", "HEADER_TEXT", "FOOTER_TEXT",
        "TAIL_TEXT", "PRINT_PERSON", "TODAY", "CLIENT_ADDRESS_LINE", "CLIENT_NAME",
        "DETAIL_COUNT", "PAGE_NUMBER", "TOTAL_PAGES",
    };

    /// <summary>渲染器内建的明细派生字段（非 dataContract 直接列）。</summary>
    private static readonly HashSet<string> BuiltinDetailReferences = new(StringComparer.OrdinalIgnoreCase)
    {
        "ROW_INDEX", "PRODUCT_TEXT",
    };

    private static readonly HashSet<string> ElementTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "text", "field", "image", "line", "rect", "table",
    };

    private static readonly HashSet<string> DataSources = new(StringComparer.OrdinalIgnoreCase)
    {
        "details", "master",
    };

    /// <summary>校验 layout.json 是否可安全保存/渲染；返回错误列表，空 = 通过。</summary>
    public IReadOnlyList<string> Validate(
        ReportFormatDefinition format,
        string layoutJson,
        int maxLayoutBytes = DefaultMaxLayoutBytes,
        int maxElements = DefaultMaxElements)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(layoutJson))
        {
            errors.Add("layout.json 为空。");
            return errors;
        }
        if (layoutJson.Length > maxLayoutBytes)
        {
            errors.Add($"layout.json 超过大小上限 {maxLayoutBytes} 字节（实际 {layoutJson.Length}）。");
            return errors;
        }

        LayoutDocument layout;
        try
        {
            layout = QuestPdfLayoutRenderer.Parse(layoutJson);
        }
        catch (LayoutInvalidException ex)
        {
            errors.Add($"layout.json 解析失败：{ex.Message}");
            return errors;
        }

        if (layout.SchemaVersion != 1)
            errors.Add($"不支持的 schemaVersion：{layout.SchemaVersion}，一期只支持 1。");
        if (!string.Equals(layout.Kind, "document", StringComparison.OrdinalIgnoreCase))
            errors.Add($"一期只支持 document 版式，收到 kind={layout.Kind}。");
        if (!string.Equals(format.Kind, "document", StringComparison.OrdinalIgnoreCase))
            errors.Add($"format.json kind={format.Kind} 与 layout kind=document 不匹配。");

        var pageSize = PageSizeMm(layout.Page.Size);
        if (pageSize is null)
        {
            errors.Add($"不支持的纸张：{layout.Page.Size}（A4/A5/LETTER）。");
            return errors;
        }

        var contentWidth = pageSize.Value.Width - layout.Page.Margin.Left - layout.Page.Margin.Right;
        var contentHeight = pageSize.Value.Height - layout.Page.Margin.Top - layout.Page.Margin.Bottom;
        if (contentWidth <= 0 || contentHeight <= 0)
        {
            errors.Add("页面边距超出纸张尺寸。");
            return errors;
        }

        var masterKeys = format.DataContract.Columns
            .Select(column => column.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var detailKeys = format.DataContract.DetailColumns
            .Select(column => column.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var sectionBounds = new (string Name, double Width, double? Height)[]
        {
            ("header", contentWidth, layout.Sections.Header.Height),
            ("content", contentWidth, contentHeight),
            ("footer", contentWidth, layout.Sections.Footer.Height),
        };
        var sectionElements = new[]
        {
            layout.Sections.Header.Elements,
            layout.Sections.Content.Elements,
            layout.Sections.Footer.Elements,
        };

        var totalElements = sectionElements.Sum(elements => elements.Count);
        if (totalElements > maxElements)
            errors.Add($"元素总数 {totalElements} 超过上限 {maxElements}。");

        for (var s = 0; s < sectionBounds.Length; s++)
        {
            var (name, width, height) = sectionBounds[s];
            foreach (var element in sectionElements[s])
            {
                ValidateElement(element, name, width, height, masterKeys, detailKeys, errors);
            }
        }

        return errors;
    }

    private static void ValidateElement(
        LayoutElement element,
        string section,
        double contentWidth,
        double? sectionHeight,
        HashSet<string> masterKeys,
        HashSet<string> detailKeys,
        List<string> errors)
    {
        var id = element.Id ?? element.Type;
        if (!ElementTypes.Contains(element.Type))
        {
            errors.Add($"{section}[{id}] 未知元素类型：{element.Type}。");
            return;
        }
        if (element.X < 0 || element.Y < 0)
            errors.Add($"{section}[{id}] 坐标不能为负（x={element.X}, y={element.Y}）。");
        if (element.W < 0 || element.H < 0)
            errors.Add($"{section}[{id}] 尺寸不能为负（w={element.W}, h={element.H}）。");

        // 坐标 + 宽度必须落在内容区；table 的 h=0（自动高度）允许
        if (element.X + element.W > contentWidth + 0.001)
            errors.Add($"{section}[{id}] 超出内容区宽度：x+ w = {element.X + element.W} > {contentWidth}mm。");
        if (sectionHeight is { } maxHeight && element.Y + element.H > maxHeight + 0.001 && element.Type != "table")
            errors.Add($"{section}[{id}] 超出 {section} 段高度：y + h = {element.Y + element.H} > {maxHeight}mm。");

        if (element.Field is not null)
            CheckReference(id, element.Field, masterKeys, detailKeys, errors);
        if (element.Content is not null)
        {
            foreach (Match match in TemplatePattern.Matches(element.Content))
                CheckReference(id, match.Groups[1].Value, masterKeys, detailKeys, errors);
        }

        if (element.Type == "table")
        {
            if (element.DataSource is null)
            {
                errors.Add($"{section}[{id}] table 缺少 dataSource。");
            }
            else if (!DataSources.Contains(element.DataSource))
            {
                errors.Add($"{section}[{id}] table dataSource 非法：{element.DataSource}（details/master）。");
            }
            if (element.Columns is not null)
            {
                foreach (var column in element.Columns)
                {
                    if (string.IsNullOrWhiteSpace(column.Field))
                    {
                        errors.Add($"{section}[{id}] 列缺少 field。");
                        continue;
                    }
                    CheckReference(id, column.Field, masterKeys, detailKeys, errors);
                }
            }
        }
    }

    private static void CheckReference(
        string elementId, string reference, HashSet<string> masterKeys, HashSet<string> detailKeys,
        List<string> errors)
    {
        if (reference.StartsWith("MASTER.", StringComparison.OrdinalIgnoreCase))
        {
            var key = reference["MASTER.".Length..];
            if (!masterKeys.Contains(key))
                errors.Add($"[{elementId}] 引用 MASTER.{key} 不在 dataContract 白名单。");
        }
        else if (reference.StartsWith("DETAILS.", StringComparison.OrdinalIgnoreCase))
        {
            var key = reference["DETAILS.".Length..];
            if (!detailKeys.Contains(key) && !BuiltinDetailReferences.Contains(key))
                errors.Add($"[{elementId}] 引用 DETAILS.{key} 不在 dataContract 白名单。");
        }
        else if (reference.StartsWith("SYS.", StringComparison.OrdinalIgnoreCase))
        {
            if (!SystemReferences.Contains(reference["SYS.".Length..]))
                errors.Add($"[{elementId}] 引用 SYS.{reference["SYS.".Length..]} 不在系统值白名单。");
        }
        else
        {
            errors.Add($"[{elementId}] 字段引用命名空间非法：{reference}（MASTER.* / DETAILS.* / SYS.*）。");
        }
    }

    private static (double Width, double Height)? PageSizeMm(string size)
    {
        return size.Trim().ToUpperInvariant() switch
        {
            "A4" => (210, 297),
            "A5" => (148, 210),
            "LETTER" => (215.9, 279.4),
            _ => null,
        };
    }
}
