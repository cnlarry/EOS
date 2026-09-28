using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.RegularExpressions;
using EOS.API.Models;
using QuestPDF.Elements.Table;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using ZXing;
using ZXing.Common;
using ZXing.QrCode.Internal;
using ZXing.Rendering;

namespace EOS.API.Data;

/// <summary>
/// layout.json 解释层：
/// 解析 schema v1 → 绝对定位逐元素绘制（mm→pt 换算）→ table 自动分页 + 表头重复 + 合计。
/// fail-closed：未知字段引用渲染为空串、非法坐标/结构拒绝，不做猜测。
/// 内置版式与客户定制统一走本渲染器。
/// </summary>
public sealed class QuestPdfLayoutRenderer(ILogger<QuestPdfLayoutRenderer> logger) : ILayoutRenderer
{
    /// <summary>mm → pt 换算（QuestPDF 默认单位 point，1mm ≈ 2.8346pt）。</summary>
    public const double MmToPt = 2.8346456692913386;

    private const double TextLineHeightRatio = 1.4667; // QuestPDF 实测行高/字号（12pt→17.6pt）

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly Regex TemplatePattern = new(
        @"\{\{([A-Za-z][A-Za-z0-9_.]*)\}\}", RegexOptions.Compiled);

    public byte[] Render(PrintData data, string layoutJson, LayoutRenderContext? context = null)
    {
        var layout = Parse(layoutJson);
        if (layout.SchemaVersion != 1)
            throw new LayoutInvalidException($"不支持的 schemaVersion：{layout.SchemaVersion}。");
        if (!string.Equals(layout.Kind, "document", StringComparison.OrdinalIgnoreCase))
            throw new LayoutInvalidException($"一期只支持 document 版式，收到 kind={layout.Kind}。");

        return RenderLayout(layout, data, context ?? new LayoutRenderContext());
    }

    /// <summary>
    /// 报表清单渲染（列表型版式）：与单据渲染**共用同一套页面/分节/元素管道**，
    /// 差别只在"值从哪来"（报表数据经 <see cref="LayoutRenderContext.Report"/> 传入）。
    /// 另起一条渲染旁路会让两边的定位、样式、分页慢慢长歪，所以这里只做入口分派。
    /// </summary>
    public byte[] RenderReportList(PrintData data, string layoutJson, LayoutRenderContext context)
    {
        if (context.Report is null)
            throw new LayoutInvalidException("报表清单渲染缺少报表数据（LayoutRenderContext.Report）。");
        var layout = Parse(layoutJson);
        if (layout.SchemaVersion != 1)
            throw new LayoutInvalidException($"不支持的 schemaVersion：{layout.SchemaVersion}。");
        if (!string.Equals(layout.Kind, "list", StringComparison.OrdinalIgnoreCase))
            throw new LayoutInvalidException($"报表清单只能用列表型版式（kind=list），收到 kind={layout.Kind}。");
        return RenderLayout(layout, data, context);
    }

    private byte[] RenderLayout(LayoutDocument layout, PrintData data, LayoutRenderContext effective)
    {
        // orientation=auto：按列数决定横竖版（沿用报表清单打印的既有口径——列多了竖版挤成一团）。
        // 声明式版式里"纸张朝向"仍是一个属性；只有 auto 这一档需要看运行时列数。
        var landscape = IsLandscape(layout.Page.Orientation)
            || (string.Equals(layout.Page.Orientation, "auto", StringComparison.OrdinalIgnoreCase)
                && (effective.Report?.Columns.Count ?? 0) > 12);
        var document = Document.Create(container =>
        {
            container.Page(page =>
            {
                var pageSize = PdfLayout.PageSizeFor(layout.Page.Size, landscape);
                page.Size(pageSize);
                page.MarginTop(Mm(layout.Page.Margin.Top));
                page.MarginRight(Mm(layout.Page.Margin.Right));
                page.MarginBottom(Mm(layout.Page.Margin.Bottom));
                page.MarginLeft(Mm(layout.Page.Margin.Left));
                page.DefaultTextStyle(TextStyle.Default.FontFamily(PdfLayout.FontFamily).FontSize(9));
                var contentWidth = pageSize.Width - Mm(layout.Page.Margin.Left) - Mm(layout.Page.Margin.Right);
                var contentHeight = pageSize.Height - Mm(layout.Page.Margin.Top) - Mm(layout.Page.Margin.Bottom);

                page.Content().Element(content => RenderContent(
                    content, layout.Sections.Content, contentWidth, contentHeight, data, effective));
                // 多页模板：页头/页脚按页码选择（第 1 页 / 续页 / 末页，缺省回退默认 sections）
                page.Header().Dynamic(new PageTemplateDynamic((header, pageNumber, totalPages) =>
                {
                    var section = SelectPageTemplate(layout.PageTemplates, pageNumber, totalPages, isHeader: true)
                        ?? layout.Sections.Header;
                    RenderFixedSection(header, section, contentWidth, contentHeight, data, effective);
                }));
                page.Footer().Dynamic(new PageTemplateDynamic((footer, pageNumber, totalPages) =>
                {
                    var section = SelectPageTemplate(layout.PageTemplates, pageNumber, totalPages, isHeader: false)
                        ?? layout.Sections.Footer;
                    RenderFixedSection(footer, section, contentWidth, contentHeight, data, effective);
                }));
            });
        });
        logger.LogDebug("layout.json 渲染 module={ModuleId} rows={RowCount}", data.ModuleId, data.Details.Count);
        return document.GeneratePdf();
    }

    public static LayoutDocument Parse(string layoutJson)
    {
        try
        {
            return JsonSerializer.Deserialize<LayoutDocument>(layoutJson, JsonOptions)
                ?? throw new LayoutInvalidException("layout.json 为空。");
        }
        catch (JsonException ex)
        {
            throw new LayoutInvalidException($"layout.json 解析失败：{ex.Message}");
        }
    }

    /// <summary>多页模板选择：第 1 页用 first；最后一页用 last（优先于 continuation）；中间用 continuation。</summary>
    private static LayoutSection? SelectPageTemplate(
        LayoutPageTemplates? templates, int pageNumber, int totalPages, bool isHeader)
    {
        if (templates is null) return null;
        LayoutSection? Of(LayoutPageTemplate? template) => isHeader ? template?.Header : template?.Footer;
        if (pageNumber == 1)
            return Of(templates.First) ?? Of(templates.Continuation);
        if (pageNumber == totalPages && Of(templates.Last) is { } last)
            return last;
        return Of(templates.Continuation);
    }

    /// <summary>QuestPDF Dynamic 组件：按当前页码/总页数组合内容（多页模板核心）。</summary>
    private sealed class PageTemplateDynamic : IDynamicComponent
    {
        private readonly Action<IContainer, int, int> _compose;

        public PageTemplateDynamic(Action<IContainer, int, int> compose) => _compose = compose;

        public DynamicComponentComposeResult Compose(QuestPDF.Elements.DynamicContext context)
        {
            var content = context.CreateElement(container =>
                _compose(container, context.PageNumber, context.TotalPages));
            return new DynamicComponentComposeResult { Content = content };
        }
    }

    // ============================================================================
    // Section 渲染
    // ============================================================================

    /// <summary>header/footer：固定高度带，元素按绝对坐标（Layers 叠层 + TranslateX/Y）。</summary>
    private static void RenderFixedSection(
        IContainer container, LayoutSection section, float contentWidth, float contentHeight,
        PrintData data, LayoutRenderContext context)
    {
        var elements = section.Elements.Where(element => element.Visible).ToList();
        if (elements.Count == 0) return;
        var height = Mm((float)(section.Height ?? 10));
        container.Height(height).Layers(layers =>
        {
            layers.PrimaryLayer().Width(contentWidth).Height(height);
            foreach (var element in elements)
                AddElementLayer(layers, element, 0, contentWidth, height, data, context);
        });
    }

    /// <summary>
    /// content：静态元素绝对定位 + table 区域内流动（自动分页 + 表头重复）。
    /// 表上方元素在固定高区域内按绝对坐标绘制；table 流入主列；
    /// 表下方元素（签名等）在 table 结束后按相对偏移绘制（避免与流动表格重叠）。
    /// </summary>
    private static void RenderContent(
        IContainer container, LayoutSection section, float contentWidth, float contentHeight,
        PrintData data, LayoutRenderContext context)
    {
        var elements = section.Elements.Where(element => element.Visible).ToList();
        if (elements.Count == 0) return;
        var tables = elements.Where(element => element.Type == "table").OrderBy(element => element.Y).ToList();
        var statics = elements.Where(element => element.Type != "table").ToList();

        container.Column(column =>
        {
            if (tables.Count == 0)
            {
                var height = statics.Count == 0
                    ? 0
                    : Math.Max(statics.Max(element => element.Y + NaturalHeight(element, data)), 0.1);
                if (height > 0)
                    column.Item().Height(Mm((float)height)).Layers(layers =>
                    {
                        layers.PrimaryLayer().Width(contentWidth).Height(Mm((float)height));
                        foreach (var element in statics)
                            AddElementLayer(layers, element, 0, contentWidth, Mm((float)height), data, context);
                    });
                return;
            }

            var firstTable = tables[0];
            var top = statics.Where(element => element.Y < firstTable.Y).ToList();
            var bottom = statics.Where(element => element.Y >= firstTable.Y).ToList();

            var topBandHeight = Math.Max(
                firstTable.Y,
                top.Count == 0 ? 0 : top.Max(element => element.Y + NaturalHeight(element, data)));
            if (topBandHeight > 0.01)
            {
                column.Item().Height(Mm((float)topBandHeight)).Layers(layers =>
                {
                    layers.PrimaryLayer().Width(contentWidth).Height(Mm((float)topBandHeight));
                    foreach (var element in top)
                        AddElementLayer(
                            layers, element, 0, contentWidth, Mm((float)topBandHeight), data, context);
                });
            }

            var tableWidth = firstTable.W > 0 ? (float)firstTable.W : contentWidth / MmToPt;
            var padLeft = Mm((float)firstTable.X);
            var padRight = Math.Max(0, contentWidth - padLeft - Mm(tableWidth));
            // 显式列宽合计可能比可用宽度多零点几个 pt（如 A4 595pt vs 精确 210mm），
            // 按可用宽度收缩表格列，避免 conflicting size constraints。
            var availableWidth = Math.Max(0, contentWidth - padLeft);
            column.Item().PaddingLeft(padLeft).PaddingRight(padRight)
                .Element(cell => RenderTable(cell, firstTable, data, context, availableWidth));

            if (bottom.Count > 0)
            {
                var bottomHeight = Math.Max(
                    bottom.Max(element => element.Y + NaturalHeight(element, data)) - firstTable.Y, 0.1);
                column.Item().Height(Mm((float)bottomHeight)).Layers(layers =>
                {
                    layers.PrimaryLayer().Width(contentWidth).Height(Mm((float)bottomHeight));
                    foreach (var element in bottom)
                        AddElementLayer(
                            layers, element, firstTable.Y, contentWidth, Mm((float)bottomHeight), data, context);
                });
            }
        });
    }

    private static void AddElementLayer(
        LayersDescriptor? layers, LayoutElement element, double originY, float contentWidth,
        float bandHeight, PrintData data, LayoutRenderContext context)
    {
        if (layers is null) return;
        // QuestPDF Layers 对坐标 + 尺寸越出容器（含 mm→pt 舍入差，如 A4 595pt vs 精确 210mm）
        // 的 Layer 会整层丢弃。此处把元素宽高收缩到容器内，超出部分按版式语义裁剪。
        var x = Mm((float)element.X);
        var y = Mm((float)(element.Y - originY));
        var width = Math.Min(Mm((float)element.W), Math.Max(0, contentWidth - x));
        var maxHeight = Math.Max(0, bandHeight - y);
        var minHeight = Math.Min(Mm((float)element.H), maxHeight);
        if (width < 0.01f || maxHeight < 0.01f || minHeight < 0.01f) return;
        layers.Layer()
            // 高度用 MinHeight + MaxHeight：文本/字段按实际行高自然撑起
            // （Noto 字体行高可能大于声明高度，固定 Height 会触发整层 Wrap 丢弃），
            // 其余元素按声明高度；MaxHeight 保证不越出固定带。
            .Width(width).MinHeight(minHeight).MaxHeight(maxHeight)
            .OffsetX(x, Unit.Point)
            .OffsetY(y, Unit.Point)
            .Element(cell => RenderElement(cell, element, data, context, null));
    }

    // ============================================================================
    // 元素渲染（text / field / image / line / rect / table）
    // ============================================================================

    private static void RenderElement(
        IContainer container, LayoutElement element, PrintData data, LayoutRenderContext context,
        IReadOnlyDictionary<string, object?>? detailRow)
    {
        switch (element.Type)
        {
            case "text":
                RenderText(container, element, data, context);
                break;
            case "field":
                var value = ResolveField(element.Field, data, context, detailRow);
                if (string.IsNullOrEmpty(value) && element.H == 0) return;
                var fieldFontSize = (float)(element.Style?.FontSize ?? 9);
                container.Text(descriptor =>
                {
                    descriptor.DefaultTextStyle(style => ApplyTextStyle(style, element.Style).FontSize(fieldFontSize));
                    descriptor.AlignFrom(element.Style?.Align);
                    descriptor.Span(value ?? string.Empty);
                });
                break;
            case "image":
                RenderImage(container, element, data, context);
                break;
            case "barcode":
                RenderBarcode(container, element, data, context);
                break;
            case "line":
                container.LineHorizontal((float)(element.Style?.LineWidth ?? 0.5), Unit.Point)
                    .LineColor(ColorOr(element.Style?.Color, Colors.Black));
                break;
            case "rect":
                container.Background(ColorOr(element.Style?.BackgroundColor, Colors.White))
                    .Border((float)(element.Style?.BorderWidth ?? 0.5))
                    .BorderColor(ColorOr(element.Style?.BorderColor, Colors.Black));
                break;
            case "table":
                RenderTable(container, element, data, context, element.W > 0 ? Mm((float)element.W) : 0);
                break;
            default:
                // fail-closed：未知元素类型不渲染（保留位置占位由外层高度控制）
                break;
        }
    }

    private static void RenderText(
        IContainer container, LayoutElement element, PrintData data, LayoutRenderContext context)
    {
        var text = element.Content ?? string.Empty;
        var fontSize = (float)(element.Style?.FontSize ?? 9);
        container.Text(textDescriptor =>
        {
            textDescriptor.DefaultTextStyle(style => ApplyTextStyle(style, element.Style).FontSize(fontSize));
            textDescriptor.AlignFrom(element.Style?.Align);
            var last = 0;
            foreach (Match match in TemplatePattern.Matches(text))
            {
                if (match.Index > last)
                    textDescriptor.Span(text[last..match.Index]);
                var reference = match.Groups[1].Value;
                if (string.Equals(reference, "SYS.PAGE_NUMBER", StringComparison.OrdinalIgnoreCase))
                    textDescriptor.CurrentPageNumber();
                else if (string.Equals(reference, "SYS.TOTAL_PAGES", StringComparison.OrdinalIgnoreCase))
                    textDescriptor.TotalPages();
                else
                    textDescriptor.Span(ResolveField(reference, data, context, null) ?? string.Empty);
                last = match.Index + match.Length;
            }
            if (last < text.Length)
                textDescriptor.Span(text[last..]);
        });
    }

    private static void RenderImage(
        IContainer container, LayoutElement element, PrintData data, LayoutRenderContext context)
    {
        // resourceId=REPORT.LOGO：列表型版式里的公司 LOGO（来自报表页头设置）。
        // 与单据 LOGO 走同一张图片元素、同一条绘制路径，只是"从哪拿到字节"不同。
        var bytes = string.Equals(element.ResourceId, "REPORT.LOGO", StringComparison.OrdinalIgnoreCase)
            ? context.Report?.Logo
            : ResolveImageBytes(element.ResourceId, data);
        if (bytes is null || bytes.Length == 0) return;
        var image = container.Image(bytes);
        if (element.W > 0) image.FitWidth();
        else if (element.H > 0) image.FitHeight();
        else image.FitArea();
    }

    /// <summary>条码/二维码：ZXing 生成位图 → PNG → QuestPDF Image（值支持 {{MASTER.*}}/{{SYS.*}} 模板）。</summary>
    [SupportedOSPlatform("windows")]
    private static void RenderBarcode(
        IContainer container, LayoutElement element, PrintData data, LayoutRenderContext context)
    {
        var value = ResolveTemplateText(element.Content ?? string.Empty, data, context);
        if (string.IsNullOrWhiteSpace(value)) return;
        var type = ParseBarcodeFormat(element.BarcodeType);
        var isQr = type == BarcodeFormat.QR_CODE;
        var options = new EncodingOptions
        {
            Width = isQr ? 300 : 400,
            Height = isQr ? 300 : 110,
            Margin = isQr ? 2 : 1,
            PureBarcode = false,
        };
        if (isQr)
        {
            options.Hints[EncodeHintType.ERROR_CORRECTION] = (element.BarcodeErrorCorrection ?? "M").ToUpperInvariant() switch
            {
                "L" => ErrorCorrectionLevel.L,
                "Q" => ErrorCorrectionLevel.Q,
                "H" => ErrorCorrectionLevel.H,
                _ => ErrorCorrectionLevel.M,
            };
        }
        var writer = new BarcodeWriter<PixelData>
        {
            Format = type,
            Options = options,
            Renderer = new PixelDataRenderer(),
        };
        try
        {
            var pixelData = writer.Write(value.Trim());
            using var bitmap = PixelDataToBitmap(pixelData);
            if (element.BarcodeColor is { } fg) ReplaceForeground(bitmap, ParseHex(fg), element.BarcodeBackground);
            if (isQr && element.BarcodeLogo is { } logoResource)
            {
                var logoBytes = string.Equals(logoResource, "SYS.LOGO", StringComparison.OrdinalIgnoreCase)
                    ? TryLoadLogo(data.LogoPath)
                    : null;
                if (logoBytes is { Length: > 0 }) DrawLogoCenter(bitmap, logoBytes);
            }
            using var stream = new MemoryStream();
            bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
            var image = container.Image(stream.ToArray());
            if (element.W > 0) image.FitWidth();
            else if (element.H > 0) image.FitHeight();
            else image.FitArea();
        }
        catch (WriterException)
        {
            // 值无法编码（如 EAN13 位数不符）时 fail-closed 不渲染
        }
    }

    private static BarcodeFormat ParseBarcodeFormat(string? type)
    {
        return (type ?? string.Empty).ToLowerInvariant() switch
        {
            "code128" => BarcodeFormat.CODE_128,
            "code39" => BarcodeFormat.CODE_39,
            "code93" => BarcodeFormat.CODE_93,
            "ean13" => BarcodeFormat.EAN_13,
            "upca" => BarcodeFormat.UPC_A,
            "upce" => BarcodeFormat.UPC_E,
            "itf" => BarcodeFormat.ITF,
            "codabar" => BarcodeFormat.CODABAR,
            "pdf417" => BarcodeFormat.PDF_417,
            "datamatrix" => BarcodeFormat.DATA_MATRIX,
            "aztec" => BarcodeFormat.AZTEC,
            _ => BarcodeFormat.QR_CODE,
        };
    }

    [SupportedOSPlatform("windows")]
    private static System.Drawing.Bitmap PixelDataToBitmap(PixelData pixelData)
    {
        var bitmap = new System.Drawing.Bitmap(pixelData.Width, pixelData.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        var rect = new System.Drawing.Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var bits = bitmap.LockBits(rect, System.Drawing.Imaging.ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            Marshal.Copy(pixelData.Pixels, 0, bits.Scan0, pixelData.Pixels.Length);
        }
        finally
        {
            bitmap.UnlockBits(bits);
        }
        return bitmap;
    }

    [SupportedOSPlatform("windows")]
    private static void ReplaceForeground(System.Drawing.Bitmap bitmap, System.Drawing.Color foreground, string? backgroundHex)
    {
        var bg = backgroundHex is null ? System.Drawing.Color.White : ParseHex(backgroundHex);
        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                var pixel = bitmap.GetPixel(x, y);
                // 接近前景（黑）的像素替换为目标色；其余按背景色处理
                var luminance = (pixel.R + pixel.G + pixel.B) / 3.0;
                bitmap.SetPixel(x, y, luminance < 140 ? foreground : bg);
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private static void DrawLogoCenter(System.Drawing.Bitmap bitmap, byte[] logoBytes)
    {
        using var logo = new System.Drawing.Bitmap(new MemoryStream(logoBytes));
        var size = Math.Max(12, bitmap.Width / 5);
        var rect = new System.Drawing.Rectangle((bitmap.Width - size) / 2, (bitmap.Height - size) / 2, size, size);
        using var graphics = System.Drawing.Graphics.FromImage(bitmap);
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var whiteBrush = new System.Drawing.SolidBrush(System.Drawing.Color.White);
        graphics.FillEllipse(whiteBrush, rect);
        graphics.DrawImage(logo, rect);
    }

    [SupportedOSPlatform("windows")]
    private static System.Drawing.Color ParseHex(string hex)
    {
        try
        {
            return System.Drawing.ColorTranslator.FromHtml(hex);
        }
        catch
        {
            return System.Drawing.Color.Black;
        }
    }

    /// <summary>解析 {{...}} 模板（barcode/image 等非文本元素取值用；页码/总页不适用返回空）。</summary>
    private static string ResolveTemplateText(
        string template, PrintData data, LayoutRenderContext context)
    {
        return TemplatePattern.Replace(template, match =>
        {
            var reference = match.Groups[1].Value;
            if (reference.StartsWith("SYS.", StringComparison.OrdinalIgnoreCase)
                && string.Equals(reference, "SYS.PAGE_NUMBER", StringComparison.OrdinalIgnoreCase) is false
                && string.Equals(reference, "SYS.TOTAL_PAGES", StringComparison.OrdinalIgnoreCase) is false)
                return ResolveSystemValue(reference["SYS.".Length..], data, context) ?? string.Empty;
            if (reference.StartsWith("MASTER.", StringComparison.OrdinalIgnoreCase))
                return PdfLayout.FormatValue(data.Master.GetValueOrDefault(reference["MASTER.".Length..]));
            return string.Empty;
        });
    }

    // ============================================================================
    // table 渲染（明细自动分页 + 表头重复 + 合计；master 数据源名值对表）
    // ============================================================================

    /// <summary>
    /// 报表清单表（`dataSource=report`）：列来自 <see cref="ReportListPayload.Columns"/>，
    /// 支持分组表头行与小计行（分组/小计口径由打印面板决定，聚合走 <see cref="ReportListGrouping"/>）。
    ///
    /// <para>
    /// 表头**跨页重复**是清单类打印的基本要求（第二页没有表头，读的人得回去翻第一页）。
    /// 这里恒开：报表清单不像单据那样有"表头只在第一页"的场景。
    /// </para>
    /// </summary>
    private static void RenderReportTable(IContainer container, LayoutElement table, LayoutRenderContext context)
    {
        var payload = context.Report;
        if (payload is null || payload.Columns.Count == 0) return;

        var columns = payload.Columns;
        var offsetColumns = columns.Count > 0 && string.Equals(columns[0].Key, "ROW_INDEX", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        var body = offsetColumns == 1 ? columns.Skip(1).ToList() : columns;
        if (body.Count == 0) return;

        var summaries = ReportListGrouping.BuildGroupSummaries(
            payload.Rows, payload.GroupFields, payload.ShowGroup, payload.ShowDetail, payload.SubtotalKeys);
        var useGrouping = payload.ShowGroup && payload.GroupFields.Count > 0 && summaries.Count > 0;

        container.Table(t =>
        {
            t.ColumnsDefinition(definition =>
            {
                foreach (var _ in body) definition.RelativeColumn();
            });

            if (table.ShowHeader ?? true)
            {
                t.Header(header =>
                {
                    foreach (var column in body)
                        header.Cell().Background(Colors.Grey.Lighten3).Padding(3)
                            .Text(column.Label).FontSize(8).SemiBold();
                });
            }

            if (useGrouping)
            {
                var rowIndex = 0;
                foreach (var summary in summaries)
                {
                    t.Cell().ColumnSpan((uint)body.Count).Background(Colors.Blue.Lighten5).Padding(3)
                        .Text(summary.Key).FontSize(8).SemiBold();
                    while (rowIndex < payload.Rows.Count
                           && ReportListGrouping.GroupKeyOf(payload.Rows[rowIndex], payload.GroupFields) == summary.Key)
                    {
                        if (payload.ShowDetail) EmitReportRow(t, body, payload.Rows[rowIndex]);
                        rowIndex++;
                    }
                    EmitReportSubtotal(t, body, summary, table.TotalsLabel ?? "小计");
                }
                for (; rowIndex < payload.Rows.Count; rowIndex++)
                    if (payload.ShowDetail) EmitReportRow(t, body, payload.Rows[rowIndex]);
                return;
            }

            foreach (var row in payload.Rows)
                if (payload.ShowDetail) EmitReportRow(t, body, row);

            if ((table.ShowTotals ?? false) && payload.Rows.Count > 0)
            {
                var overall = new ReportListGrouping.GroupSummary(string.Empty,
                    payload.SubtotalKeys.Select(key => (key, SumColumn(payload.Rows, key))).ToList());
                EmitReportSubtotal(t, body, overall, table.TotalsLabel ?? "合计");
            }
        });
    }

    private static decimal SumColumn(IReadOnlyList<Dictionary<string, object?>> rows, string key)
    {
        var total = 0m;
        foreach (var row in rows)
        {
            var raw = row.GetValueOrDefault(key);
            if (raw is null || raw is DBNull) continue;
            if (decimal.TryParse(Convert.ToString(raw, CultureInfo.InvariantCulture),
                    NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed))
                total += parsed;
        }
        return total;
    }

    private static void EmitReportRow(TableDescriptor table, IReadOnlyList<ReportColumn> columns, Dictionary<string, object?> row)
    {
        foreach (var column in columns)
        {
            var value = PdfLayout.FormatValue(row.GetValueOrDefault(column.Key), column.DisplayFormat);
            var cell = table.Cell().Padding(2);
            if (IsNumeric(column.DataType)) cell = cell.AlignRight();
            cell.Text(value).FontSize(8);
        }
    }

    private static void EmitReportSubtotal(
        TableDescriptor table, IReadOnlyList<ReportColumn> columns,
        ReportListGrouping.GroupSummary summary, string label)
    {
        var totals = summary.Totals.ToDictionary(pair => pair.Column, pair => pair.Total, StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < columns.Count; i++)
        {
            var cell = table.Cell().Background(Colors.Grey.Lighten2).Padding(2);
            if (i == 0)
            {
                cell.Text(label).FontSize(8).SemiBold();
                continue;
            }
            if (!totals.TryGetValue(columns[i].Key, out var total))
            {
                cell.Text(string.Empty).FontSize(8);
                continue;
            }
            cell.AlignRight().Text(PdfLayout.FormatValue(total, columns[i].DisplayFormat)).FontSize(8).SemiBold();
        }
    }

    private static bool IsNumeric(string dataType) =>
        dataType.Contains("float", StringComparison.OrdinalIgnoreCase)
        || dataType.Contains("int", StringComparison.OrdinalIgnoreCase)
        || dataType.Contains("decimal", StringComparison.OrdinalIgnoreCase)
        || dataType.Contains("money", StringComparison.OrdinalIgnoreCase);

    private static void RenderTable(
        IContainer container, LayoutElement table, PrintData data, LayoutRenderContext context,
        float availableWidth)
    {
        if (string.Equals(table.DataSource, "master", StringComparison.OrdinalIgnoreCase))
        {
            RenderMasterTable(container, table, data);
            return;
        }
        // 报表清单表：列由报表数据决定（动态列），分组/小计来自打印面板的选择。
        // 与 details/master 并列，是同一张 table 元素的第三种数据来源，而不是另一条渲染旁路。
        if (string.Equals(table.DataSource, "report", StringComparison.OrdinalIgnoreCase))
        {
            RenderReportTable(container, table, context);
            return;
        }

        var autoColumns = table.Columns is null || table.Columns.Count == 0;
        var fields = autoColumns
            ? data.DetailFields
                .Where(field => context.ShowRemark || !PdfLayout.IsRemarkField(field.Key))
                .Select(field => new TableColumnSpec(
                    field.Key, field.Label, null, "left", field.DisplayFormat, null, null, null))
                .ToList()
            : table.Columns!
                .Select(column => new TableColumnSpec(
                    column.Field, column.Label, column.Width, column.Align, column.Format,
                    column.Suffix, column.IsAmount, column.NegativeRed))
                .ToList();

        if (fields.Count == 0) return;
        var columnCount = fields.Count;
        // 显式列宽合计超出可用宽度时按比例缩放（如 A4 595pt 与精确 210mm 的舍入差）
        var declaredWidth = fields.Sum(column => Mm((float)(column.Width ?? 20)));
        var columnScale = declaredWidth > availableWidth && availableWidth > 0
            ? availableWidth / declaredWidth
            : 1f;

        container.Table(t =>
        {
            t.ColumnsDefinition(definition =>
            {
                if (autoColumns)
                {
                    foreach (var _ in fields) definition.RelativeColumn();
                }
                else
                {
                    foreach (var column in fields)
                        definition.ConstantColumn(Mm((float)(column.Width ?? 20)) * columnScale);
                }
            });

            if (table.ShowHeader ?? true)
            {
                t.Header(header =>
                {
                    for (var i = 0; i < columnCount; i++)
                    {
                        var label = fields[i].Label;
                        header.Cell()
                            .Background(Colors.Grey.Lighten3)
                            .BorderBottom(0.75f).BorderColor(Colors.Grey.Darken1)
                            .Padding(3)
                            .Text(label).FontSize(8).SemiBold();
                    }
                });
            }

            var index = 0;
            foreach (var row in data.Details)
            {
                index++;
                for (var i = 0; i < columnCount; i++)
                {
                    var value = autoColumns
                        ? PdfLayout.FormatValue(row.GetValueOrDefault(fields[i].Field), fields[i].Format)
                        : ResolveTableCell(fields[i], row, index);
                    var align = fields[i].Align ?? "left";
                    var cell = t.Cell()
                        .BorderBottom(0.25f).BorderColor(Colors.Grey.Lighten2)
                        .Padding(2);
                    if (table.RowHeight is > 0)
                        cell = cell.MinHeight(Mm((float)table.RowHeight));
                    if (table.Style?.Striped == true && index % 2 == 0)
                        cell = cell.Background(Colors.Grey.Lighten4);
                    var text = cell.AlignFrom(align).Text(value).FontSize(8);
                    // 金额条件格式：负数红字（列显式开启且值为负数）
                    if (fields[i].NegativeRed == true && IsNegativeNumber(value))
                        text.FontColor(Colors.Red.Medium);
                }
            }

            // 价税合计行（profile 表）：标签跨列 + 主表金额
            if (!string.IsNullOrWhiteSpace(table.TotalsLabel) && !string.IsNullOrWhiteSpace(table.TotalsField))
            {
                var amount = PrintColumnFormats.Format(
                    ResolveMasterValue(table.TotalsField, data), PrintColumnFormats.Amount);
                if (columnCount > 1)
                    t.Cell().ColumnSpan((uint)(columnCount - 1))
                        .BorderBottom(0.25f).BorderColor(Colors.Grey.Lighten2)
                        .Padding(3).AlignRight().Text(table.TotalsLabel).FontSize(8).SemiBold();
                t.Cell().BorderBottom(0.25f).BorderColor(Colors.Grey.Lighten2)
                    .Padding(3).AlignRight()
                    .Text(amount.Length == 0 ? "0.00" : amount).FontSize(8).SemiBold();
            }
            else if (table.ShowTotals ?? false)
            {
                for (var i = 0; i < columnCount; i++)
                {
                    var isAmount = fields[i].IsAmount ?? PdfLayout.IsAmountColumn(fields[i].Field);
                    if (!isAmount)
                    {
                        t.Cell().Padding(3);
                        continue;
                    }
                    var total = data.Details.Sum(row =>
                    {
                        var raw = row.GetValueOrDefault(fields[i].Field);
                        return raw is null || raw is DBNull || !decimal.TryParse(
                            Convert.ToString(raw, CultureInfo.InvariantCulture),
                            NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed) ? 0m : parsed;
                    });
                    t.Cell().Padding(3).AlignRight()
                        .Text(total.ToString("0.00", CultureInfo.InvariantCulture)).FontSize(8).SemiBold();
                }
            }
        });
    }

    private static void RenderMasterTable(IContainer container, LayoutElement table, PrintData data)
    {
        var fields = data.MasterFields;
        if (table.MaxRows is { } maxRows) fields = fields.Take(maxRows).ToList();
        if (fields.Count == 0) return;
        container.Table(t =>
        {
            t.ColumnsDefinition(definition =>
            {
                definition.ConstantColumn(120);
                definition.RelativeColumn();
            });
            foreach (var field in fields)
            {
                t.Cell().Padding(3).Text(field.Label).FontSize(8).SemiBold();
                t.Cell().Padding(3).Text(
                    PdfLayout.FormatValue(data.Master.GetValueOrDefault(field.Key), field.DisplayFormat))
                    .FontSize(8);
            }
        });
    }

    private static string ResolveTableCell(TableColumnSpec column, IReadOnlyDictionary<string, object?> row, int index)
    {
        var reference = column.Field;
        if (string.Equals(reference, "DETAILS.ROW_INDEX", StringComparison.OrdinalIgnoreCase))
            return index.ToString(CultureInfo.InvariantCulture);
        if (string.Equals(reference, "DETAILS.PRODUCT_TEXT", StringComparison.OrdinalIgnoreCase))
        {
            var name = PdfLayout.FormatValue(row.GetValueOrDefault("PRO_NAME"));
            var spec = PdfLayout.FormatValue(row.GetValueOrDefault("PRO_SPEC"));
            return string.IsNullOrEmpty(spec) || spec.Equals(name, StringComparison.OrdinalIgnoreCase)
                ? name
                : $"{name} {spec}";
        }
        if (reference.StartsWith("DETAILS.", StringComparison.OrdinalIgnoreCase))
        {
            var value = PdfLayout.FormatValue(
                row.GetValueOrDefault(reference["DETAILS.".Length..]), column.Format);
            return AppendSuffix(value, column.Suffix);
        }
        if (reference.StartsWith("MASTER.", StringComparison.OrdinalIgnoreCase))
            return PdfLayout.FormatValue(ResolveMasterValue(reference, null), column.Format);
        return string.Empty; // fail-closed：未知字段引用渲染为空
    }

    private sealed record TableColumnSpec(
        string Field, string Label, double? Width, string? Align, string? Format,
        string? Suffix, bool? IsAmount, bool? NegativeRed);

    private static string AppendSuffix(string value, string? suffix)
        => string.IsNullOrEmpty(value) || string.IsNullOrWhiteSpace(suffix) ? value : value + suffix;

    /// <summary>金额条件格式判定：去掉千分位/货币符后为负数（用于 negativeRed 列）。</summary>
    private static bool IsNegativeNumber(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var normalized = value.Trim().Replace(",", string.Empty, StringComparison.Ordinal);
        return normalized.StartsWith('-') && decimal.TryParse(
            normalized, NumberStyles.Number, CultureInfo.InvariantCulture, out _);
    }

    // ============================================================================
    // 字段取值（MASTER.* / DETAILS.* / SYS.*；fail-closed 未知 → null）
    // ============================================================================

    private static string? ResolveField(
        string? reference, PrintData data, LayoutRenderContext context,
        IReadOnlyDictionary<string, object?>? detailRow)
    {
        if (string.IsNullOrWhiteSpace(reference)) return null;
        if (reference.StartsWith("MASTER.", StringComparison.OrdinalIgnoreCase))
            return PdfLayout.FormatValue(ResolveMasterValue(reference, data));
        if (reference.StartsWith("DETAILS.", StringComparison.OrdinalIgnoreCase))
        {
            if (detailRow is null) return null;
            if (reference.EndsWith(".ROW_INDEX", StringComparison.OrdinalIgnoreCase))
                return detailRow.TryGetValue("__ROW_INDEX", out var rowIndex) ? Convert.ToString(rowIndex) : null;
            return PdfLayout.FormatValue(detailRow.GetValueOrDefault(reference["DETAILS.".Length..]));
        }
        if (reference.StartsWith("SYS.", StringComparison.OrdinalIgnoreCase))
            return ResolveSystemValue(reference["SYS.".Length..], data, context);
        if (reference.StartsWith("REPORT.", StringComparison.OrdinalIgnoreCase))
            return ResolveReportValue(reference["REPORT.".Length..], context);
        return null;
    }

    /// <summary>
    /// 报表页占位符取值（列表型版式专用）：值来自 <see cref="LayoutRenderContext.Report"/>。
    /// 未设报表数据时一律返回 null——单据渲染里写 `{{REPORT.X}}` 得到空串而不是抛异常，
    /// 与其它未知引用的 fail-closed 口径一致（宁可少画，不可画错）。
    /// </summary>
    private static string? ResolveReportValue(string name, LayoutRenderContext context)
    {
        var report = context.Report;
        if (report is null) return null;
        return name.ToUpperInvariant() switch
        {
            "TITLE" => report.Title,
            "ISO" => report.IsoNo,
            "CONDITIONS" => report.Conditions,
            "TAIL" => report.TailText,
            "PRINT_PERSON" => context.PrintPerson ?? Environment.UserName,
            "HEADER_COMPANY" => report.CompanyName,
            "HEADER_COMPANY_EN" => report.CompanyNameEn,
            "HEADER_TEXT" => report.HeaderText,
            "FOOTER_TEXT" => report.FooterText,
            _ => null,
        };
    }

    private static object? ResolveMasterValue(string reference, PrintData? data)
    {
        if (data is null || !reference.StartsWith("MASTER.", StringComparison.OrdinalIgnoreCase)) return null;
        return data.Master.GetValueOrDefault(reference["MASTER.".Length..]);
    }

    private static string? ResolveSystemValue(string name, PrintData data, LayoutRenderContext context)
    {
        return name.ToUpperInvariant() switch
        {
            "TITLE" => data.Title,
            "HEADER_COMPANY" => data.HeaderCompany,
            "HEADER_COMPANY_EN" => data.HeaderCompanyEn,
            "HEADER_TEXT" => data.HeaderText,
            "FOOTER_TEXT" => data.FooterText,
            "TAIL_TEXT" => data.TailText,
            "PRINT_PERSON" => context.PrintPerson ?? Environment.UserName,
            "TODAY" => DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            "CLIENT_ADDRESS_LINE" => BuildClientAddressLine(data.ClientProfile),
            "CLIENT_NAME" => data.ClientProfile?.ClientName,
            "DETAIL_COUNT" => data.Details.Count.ToString(CultureInfo.InvariantCulture),
            _ => null,
        };
    }

    private static string BuildClientAddressLine(ClientPrintProfile? profile)
    {
        if (profile is null) return string.Empty;
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(profile.DeliAddrCn)) parts.Add($"收货地址：{profile.DeliAddrCn}");
        else if (!string.IsNullOrWhiteSpace(profile.DeliAddrEn)) parts.Add($"Deliver to: {profile.DeliAddrEn}");
        if (!string.IsNullOrWhiteSpace(profile.Linkman)) parts.Add($"联系人：{profile.Linkman}");
        if (!string.IsNullOrWhiteSpace(profile.Tel)) parts.Add($"电话：{profile.Tel}");
        if (!string.IsNullOrWhiteSpace(profile.Fax)) parts.Add($"传真：{profile.Fax}");
        return parts.Count == 0 ? string.Empty : string.Join("　", parts);
    }

    private static byte[]? ResolveImageBytes(string? resourceId, PrintData data)
    {
        if (string.IsNullOrWhiteSpace(resourceId)) return null;
        if (string.Equals(resourceId, "SYS.LOGO", StringComparison.OrdinalIgnoreCase))
            return TryLoadLogo(data.LogoPath);
        return null; // 资源白名单外不渲染（fail-closed）
    }

    private static byte[]? TryLoadLogo(string? logoPath)
    {
        // 与 PdfLayout.TryLoadLogo 同源：本渲染器无 IWebHostEnvironment 依赖时走磁盘候选根
        if (string.IsNullOrWhiteSpace(logoPath)) return null;
        var relative = logoPath.Trim().Replace('\\', '/').TrimStart('~', '/');
        var baseDir = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(baseDir, "wwwroot", relative),
            Path.Combine(baseDir, relative),
        };
        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate)) return File.ReadAllBytes(candidate);
        }
        return null;
    }

    // ============================================================================
    // 样式辅助
    // ============================================================================

    private static TextStyle ApplyTextStyle(TextStyle style, LayoutElementStyle? elementStyle)
    {
        if (elementStyle is null) return style;
        if (elementStyle.FontSize is { } fontSize) style = style.FontSize((float)fontSize);
        if (elementStyle.Bold == true) style = style.Bold();
        if (elementStyle.SemiBold == true) style = style.SemiBold();
        if (elementStyle.Italic == true) style = style.Italic();
        if (elementStyle.Color is { } color) style = style.FontColor(ColorOr(color, Colors.Black));
        return style;
    }

    private static Color ColorOr(string? value, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        try
        {
            return Color.FromHex(value);
        }
        catch
        {
            return fallback;
        }
    }

    private static bool IsLandscape(string orientation)
        => string.Equals(orientation, "landscape", StringComparison.OrdinalIgnoreCase);

    private static float Mm(double millimeters) => (float)(millimeters * MmToPt);

    /// <summary>元素自然高度估计（mm）：text/field 按行高，其余按声明 h；用于静态区域高度计算。</summary>
    private static double NaturalHeight(LayoutElement element, PrintData data)
    {
        var declared = element.H > 0 ? element.H : 0;
        return element.Type switch
        {
            // 文本/字段至少容纳一行：声明高度不足行高时按行高计，避免固定带过矮导致整层丢弃
            "text" or "field" => Math.Max(
                declared, (element.Style?.FontSize ?? 9) * TextLineHeightRatio / MmToPt),
            "image" => element.W > 0 ? Math.Max(declared, element.W) : 10,
            _ => declared,
        };
    }
}

internal static class LayoutRendererExtensions
{
    public static TextDescriptor AlignFrom(this TextDescriptor descriptor, string? align)
    {
        switch (align?.ToLowerInvariant())
        {
            case "center": descriptor.AlignCenter(); break;
            case "right": descriptor.AlignRight(); break;
            default: descriptor.AlignLeft(); break;
        }
        return descriptor;
    }

    public static IContainer AlignFrom(this IContainer container, string? align)
    {
        return align?.ToLowerInvariant() switch
        {
            "center" => container.AlignCenter(),
            "right" => container.AlignRight(),
            _ => container.AlignLeft(),
        };
    }
}
