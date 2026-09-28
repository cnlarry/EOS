using System.Text.RegularExpressions;
using EOS.API.Models;
using EOS.API.Validation;

namespace EOS.API.Data;

/// <summary>
/// 版式保存校验器：
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
        "text", "field", "image", "line", "rect", "table", "barcode",
    };

    private static readonly HashSet<string> BarcodeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "qrcode", "code128", "code39", "code93", "ean13", "upca", "upce",
        "itf", "codabar", "pdf417", "datamatrix", "aztec",
    };

    private static readonly HashSet<string> QrErrorCorrections = new(StringComparer.OrdinalIgnoreCase)
    {
        "L", "M", "Q", "H",
    };

    private static readonly HashSet<string> DataSources = new(StringComparer.OrdinalIgnoreCase)
    {
        "details", "master",
        // report：列表型版式的表格——列不写死在版式里，由报表数据本身决定（列是动态的，最多可达 86 列）。
        "report",
    };

    /// <summary>
    /// 列表型版式可引用的报表级占位符 `{{REPORT.*}}`（对应 QuestPdfLayoutRenderer 的报表渲染分支）。
    /// 与 `{{SYS.*}}` 分开命名而不是复用：`SYS.*` 是单据上下文的语义（客户、明细数…），
    /// 报表页里那些名字要么无意义、要么含义不同（报表的"标题"是报表名，不是单据类型名）。
    /// </summary>
    private static readonly HashSet<string> ReportReferences = new(StringComparer.OrdinalIgnoreCase)
    {
        // 页码不走这里：它由渲染器直接画成"当前页/总页"，用 {{SYS.PAGE_NUMBER}} / {{SYS.TOTAL_PAGES}}，
        // 与单据版式同一套写法——同一种东西两种写法，迟早会有人写错那一种。
        "TITLE", "ISO", "CONDITIONS", "TAIL", "PRINT_PERSON",
        "HEADER_COMPANY", "HEADER_COMPANY_EN", "HEADER_TEXT", "FOOTER_TEXT",
    };

    private static readonly HashSet<string> LayoutKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        "document", "list",
    };

    /// <summary>校验 layout.json 是否可安全保存/渲染；Ok = 通过。</summary>
    public ValidationResult Validate(
        ReportFormatDefinition format,
        string layoutJson,
        int maxLayoutBytes = DefaultMaxLayoutBytes,
        int maxElements = DefaultMaxElements)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(layoutJson))
        {
            errors.Add("layout.json 为空。");
            return ValidationResult.FromMessages(errors);
        }
        if (layoutJson.Length > maxLayoutBytes)
        {
            errors.Add($"layout.json 超过大小上限 {maxLayoutBytes} 字节（实际 {layoutJson.Length}）。");
            return ValidationResult.FromMessages(errors);
        }

        LayoutDocument layout;
        try
        {
            layout = QuestPdfLayoutRenderer.Parse(layoutJson);
        }
        catch (LayoutInvalidException ex)
        {
            errors.Add($"layout.json 解析失败：{ex.Message}");
            return ValidationResult.FromMessages(errors);
        }

        if (layout.SchemaVersion != 1)
            errors.Add($"不支持的 schemaVersion：{layout.SchemaVersion}，一期只支持 1。");
        if (!LayoutKinds.Contains(layout.Kind))
            errors.Add($"不支持的版式类型 kind={layout.Kind}（document / list）。");
        // format.json 的 kind 描述的是**格式包的主用途**（单据或清单），列表型版式是包里的附加资产，
        // 因此这里只要求两者都是受支持的类型，不要求相等——否则 _generic 这种"一包两版式"就永远配不平。
        if (!LayoutKinds.Contains(format.Kind))
            errors.Add($"format.json kind={format.Kind} 不是受支持的版式类型（document / list）。");
        if (string.Equals(layout.Kind, "list", StringComparison.OrdinalIgnoreCase)
            && !HasReportDataSource(layout))
            errors.Add("列表型版式的 content 里必须有 dataSource=report 的表格，否则这张报表打印出来是空页。");

        var pageSize = PageSizeMm(layout.Page.Size);
        if (pageSize is null)
        {
            errors.Add($"不支持的纸张：{layout.Page.Size}（A4/A5/LETTER）。");
            return ValidationResult.FromMessages(errors);
        }

        var contentWidth = pageSize.Value.Width - layout.Page.Margin.Left - layout.Page.Margin.Right;
        var contentHeight = pageSize.Value.Height - layout.Page.Margin.Top - layout.Page.Margin.Bottom;
        if (contentWidth <= 0 || contentHeight <= 0)
        {
            errors.Add("页面边距超出纸张尺寸。");
            return ValidationResult.FromMessages(errors);
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

        return ValidationResult.FromMessages(errors);
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
                errors.Add($"{section}[{id}] table dataSource 非法：{element.DataSource}（details/master/report）。");
            }
            // dataSource=report：列由报表数据决定，版式里的 Columns 只是**列宽/对齐/顺序的覆盖**，
            // 引用的列键不在 dataContract 里（dataContract 描述的是单据字段，不是报表列），
            // 因此这里不做白名单校验——它不构成取数入口，数据本身已在取数阶段过了权限与列过滤。
            var reportTable = string.Equals(element.DataSource, "report", StringComparison.OrdinalIgnoreCase);
            if (element.Columns is not null && !reportTable)
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
        if (element.Type == "barcode")
        {
            if (element.BarcodeType is not null && !BarcodeTypes.Contains(element.BarcodeType))
                errors.Add($"{section}[{id}] barcodeType 非法：{element.BarcodeType}。");
            if (element.BarcodeErrorCorrection is not null
                && !string.Equals(element.BarcodeType, "qrcode", StringComparison.OrdinalIgnoreCase))
                errors.Add($"{section}[{id}] 纠错级别仅适用于二维码（qrcode）。");
            if (element.BarcodeErrorCorrection is not null
                && !QrErrorCorrections.Contains(element.BarcodeErrorCorrection))
                errors.Add($"{section}[{id}] 纠错级别非法：{element.BarcodeErrorCorrection}（L/M/Q/H）。");
            if (element.BarcodeLogo is not null
                && !string.Equals(element.BarcodeType, "qrcode", StringComparison.OrdinalIgnoreCase))
                errors.Add($"{section}[{id}] Logo 仅支持二维码（qrcode）。");
            if (string.IsNullOrWhiteSpace(element.Content))
                errors.Add($"{section}[{id}] barcode 缺少 content（编码内容，支持 {{MASTER.*}}/{{SYS.*}} 模板）。");
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
        else if (reference.StartsWith("REPORT.", StringComparison.OrdinalIgnoreCase))
        {
            // 列型版式的报表页占位符：白名单与 SYS.* 同款——引用写错必须当场报错，
            // 不能等到打印时渲染成空串（那会表现为"某天开始页眉少了一截"，没人会当成配置错误）。
            if (!ReportReferences.Contains(reference["REPORT.".Length..]))
                errors.Add($"[{elementId}] 引用 REPORT.{reference["REPORT.".Length..]} 不在报表页白名单。");
        }
        else
        {
            errors.Add($"[{elementId}] 字段引用命名空间非法：{reference}（MASTER.* / DETAILS.* / SYS.* / REPORT.*）。");
        }
    }

    /// <summary>列表型版式必须有一个 dataSource=report 的表格元素（三节任一处），否则打出来是空页。</summary>
    private static bool HasReportDataSource(LayoutDocument layout)
    {
        foreach (var section in new[] { layout.Sections.Header, layout.Sections.Content, layout.Sections.Footer })
        {
            foreach (var element in section.Elements)
            {
                if (string.Equals(element.Type, "table", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(element.DataSource, "report", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        return false;
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
