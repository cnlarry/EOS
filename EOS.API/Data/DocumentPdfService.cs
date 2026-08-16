using System.Globalization;
using EOS.API.Models;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace EOS.API.Data;

/// <summary>
/// 单据打印版式配置（由旧前端 PrintViewPage.DOCUMENT_LAYOUTS 迁移而来）：
/// 单号/日期/往来单位/金额列均为服务端常量白名单，渲染层只按配置取主表字段。
/// </summary>
internal sealed record DocumentLayoutProfile(
    IReadOnlyList<string> NoFields,
    string? DateField,
    string? PartyField,
    string? PartyNameField,
    string? AmountField,
    string? PartyLabel = null);

internal static class DocumentLayoutProfiles
{
    public static IReadOnlyDictionary<int, DocumentLayoutProfile> All { get; } =
        new Dictionary<int, DocumentLayoutProfile>
        {
            [1416] = new(["QUOTE_NO", "QUOTE_TYPE"], "QUOTE_DATE", "CLIENT_ID", "CLIENT_NAME", "AMOUNT_TAX"),
            [1604] = new(["QUOTE_NO", "QUOTE_TYPE"], "QUOTE_DATE", "SUPPLIER_ID", "SUPPLIER_NAME", "AMOUNT_TAX"),
            [1405] = new(["ORDER_NO", "ORDER_TYPE"], "ORDER_DATE", "CLIENT_ID", "CLIENT_NAME", "AMOUNT_TAX"),
            [1406] = new(["SEND_NO", "SEND_TYPE"], "SEND_DATE", "CLIENT_ID", "CLIENT_NAME", "AMOUNT_TAX"),
            [170101] = new(["ACCOUNT_NO", "ACCOUNT_TYPE"], "ACCOUNT_DATE", "CLIENT_ID", "CLIENT_NAME", "AMOUNT_TAX"),
            [170102] = new(["RECEIPT_NO", "RECEIPT_TYPE"], "RECEIPT_DATE", "CLIENT_ID", "CLIENT_NAME", "AMOUNT_TAX"),
            [170201] = new(["DUE_NO", "DUE_TYPE"], "DUE_DATE", "SUPPLIER_ID", "SUPPLIER_NAME", "AMOUNT_TAX"),
            [170202] = new(["PAY_NO", "PAY_TYPE"], "PAY_DATE", "SUPPLIER_ID", "SUPPLIER_NAME", "AMOUNT_TAX"),
            [1607] = new(["RECEIVE_NO", "RECEIVE_TYPE"], "RECEIVE_DATE", "SUPPLIER_ID", "SUPPLIER_NAME", "AMOUNT_TAX"),
            [1615] = new(["APPLY_NO", "APPLY_TYPE"], "APPLY_DATE", "SUPPLIER_ID", "SUPPLIER_NAME", "AMOUNT_TAX"),
            [1606] = new(["PURCHASE_NO", "PURCHASE_TYPE"], "PURCHASE_DATE", "SUPPLIER_ID", "SUPPLIER_NAME", "AMOUNT_TAX"),
            [1408] = new(["SHIPMENT_NO", "SHIPMENT_TYPE"], "SHIPMENT_DATE", "CLIENT_ID", "CLIENT_NAME", "AMOUNT_TAX"),
            [170103] = new(["PREPAY_NO", "PREPAY_TYPE"], "PREPAY_DATE", "CLIENT_ID", "CLIENT_NAME", "AMOUNT"),
            [170203] = new(["PREPAY_NO", "PREPAY_TYPE"], "PREPAY_DATE", "SUPPLIER_ID", "SUPPLIER_NAME", "AMOUNT"),
            [1407] = new(["RETURN_NO", "RETURN_TYPE"], "RETURN_DATE", "CLIENT_ID", "CLIENT_NAME", "AMOUNT_TAX"),
            [1409] = new(["RETURN_NO", "RETURN_TYPE"], "RETURN_DATE", "CLIENT_ID", "CLIENT_NAME", "AMOUNT_TAX"),
            [1608] = new(["CANCEL_NO", "CANCEL_TYPE"], "CANCEL_DATE", "SUPPLIER_ID", "SUPPLIER_NAME", "AMOUNT_TAX"),
            [1612] = new(["CANCEL_NO", "CANCEL_TYPE"], "CANCEL_DATE", "SUPPLIER_ID", "SUPPLIER_NAME", "AMOUNT_TAX"),
            [1610] = new(["CALLBACK_NO", "CALLBACK_TYPE"], "CALLBACK_DATE", "SUPPLIER_ID", "SUPPLIER_NAME", "AMOUNT_TAX"),
            [1503] = new(["GET_NO", "GET_TYPE"], "GET_DATE", "PRODUCE_NO", "", "", "制令单"),
            [1514] = new(["GET_NO", "GET_TYPE"], "GET_DATE", "PRODUCE_NO", "", "", "制令单"),
            [1504] = new(["BACK_NO", "BACK_TYPE"], "BACK_DATE", "BACK_NO", "", "", "单据号"),
            [1515] = new(["PRODUCT_OUT_NO", "PRODUCT_OUT_TYPE"], "PRODUCT_OUT_DATE", "CLIENT_ID", "", "", "客户"),
            [1616] = new(["APPLY_NO", "APPLY_TYPE"], "APPLY_DATE", "PRODUCE_NO", "", "", "制令单"),
        };
}

/// <summary>打印版式固定列的展示格式（渲染层约定，不随字段元数据漂移）。</summary>
internal static class PrintColumnFormats
{
    /// <summary>数量：最多 6 位小数、去尾零、千分位。</summary>
    public const string Quantity = "#,##0.######";

    /// <summary>单价：最多 6 位小数、去尾零（兼容电子元件类 6 位精度单价）。</summary>
    public const string Price = "#,##0.######";

    /// <summary>金额：固定两位小数 + 千分位。</summary>
    public const string Amount = "#,##0.00";

    public static string Format(object? value, string format)
    {
        if (value is null || value is DBNull) return string.Empty;
        if (decimal.TryParse(
                Convert.ToString(value, CultureInfo.InvariantCulture),
                NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed))
            return parsed.ToString(format, CultureInfo.InvariantCulture);
        return PdfLayout.FormatValue(value);
    }
}

/// <summary>
/// 单据 PDF 模板（原 RptBill / 工作台"打印单据"）：
/// 三种形态——资料卡（1401/1601）、单据版式配置（主表 + 固定明细列 + 价税合计 + 签名行）、
/// 通用主明细（全部可见字段 + 金额类列合计）。"打印备注"关闭时隐藏 REMARK/NOTE 类列。
/// </summary>
public sealed class DocumentPdfService(IWebHostEnvironment environment, ILogger<DocumentPdfService> logger)
{
    public byte[] Generate(PrintData data, ReportHeaderOption? header, string? tailText, bool showRemark, string printPerson)
    {
        var logo = header is null ? null : PdfLayout.TryLoadLogo(environment, header.LogoPath);
        var document = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(32);
                page.DefaultTextStyle(TextStyle.Default.FontFamily(PdfLayout.FontFamily).FontSize(9));
                page.Header().Column(headerColumn =>
                {
                    headerColumn.Item().Row(row =>
                    {
                        if (logo is { Length: > 0 })
                            row.ConstantItem(70).Image(logo);
                        row.RelativeItem().AlignCenter().Column(center =>
                        {
                            if (!string.IsNullOrWhiteSpace(header?.CompanyName))
                                center.Item().Text(header!.CompanyName).FontSize(14).Bold();
                            if (!string.IsNullOrWhiteSpace(header?.CompanyNameEn))
                                center.Item().Text(header!.CompanyNameEn).FontSize(9);
                            center.Item().Text(data.Title).FontSize(12).SemiBold();
                            if (!string.IsNullOrWhiteSpace(header?.HeaderText))
                                center.Item().Text(header!.HeaderText).FontSize(8);
                        });
                    });
                    headerColumn.Item().PaddingTop(4).LineHorizontal(0.5f);
                });
                page.Content().Column(content =>
                {
                    if (data.ModuleId is 1401 or 1601)
                        RenderCard(content, data, showRemark);
                    else if (DocumentLayoutProfiles.All.TryGetValue(data.ModuleId, out var profile))
                        RenderProfile(content, data, profile, showRemark);
                    else
                        RenderGeneric(content, data, showRemark);
                    if (!string.IsNullOrWhiteSpace(tailText))
                        content.Item().PaddingTop(8).Text(tailText).FontSize(8);
                });
                page.Footer().Row(footer =>
                {
                    footer.RelativeItem().Text(data.FooterText ?? string.Empty).FontSize(8);
                    footer.RelativeItem().AlignRight().Text(text =>
                    {
                        text.DefaultTextStyle(style => style.FontSize(8));
                        text.Span($"列印人：{printPerson}　第 ");
                        text.CurrentPageNumber();
                        text.Span(" / ");
                        text.TotalPages();
                        text.Span(" 页");
                    });
                });
            });
        });
        logger.LogDebug("单据 PDF 生成 module={ModuleId} rows={RowCount}", data.ModuleId, data.Details.Count);
        return document.GeneratePdf();
    }

    private static void RenderCard(ColumnDescriptor content, PrintData data, bool showRemark)
    {
        content.Item().Table(table =>
        {
            table.ColumnsDefinition(def =>
            {
                def.ConstantColumn(120);
                def.RelativeColumn();
            });
            foreach (var field in data.MasterFields.Take(20))
            {
                table.Cell().Padding(3).Text(field.Label).FontSize(8).SemiBold();
                table.Cell().Padding(3).Text(PdfLayout.FormatValue(data.Master.GetValueOrDefault(field.Key), field.DisplayFormat)).FontSize(8);
            }
        });
        RenderDetails(content, data, showRemark, "联系人");
    }

    private static void RenderProfile(ColumnDescriptor content, PrintData data, DocumentLayoutProfile profile, bool showRemark)
    {
        var noValue = string.Join(" / ", profile.NoFields
            .Select(field => PdfLayout.FormatValue(data.Master.GetValueOrDefault(field)))
            .Where(value => value.Length > 0));
        content.Item().AlignCenter().Column(center =>
        {
            if (noValue.Length > 0 || profile.DateField is not null)
            {
                var line = $"编号：{noValue}";
                if (profile.DateField is not null)
                    line += $"　日期：{PdfLayout.FormatValue(data.Master.GetValueOrDefault(profile.DateField))}";
                center.Item().Text(line).FontSize(9);
            }
        });
        content.Item().PaddingTop(6).Row(row =>
        {
            row.RelativeItem().Column(left =>
            {
                if (profile.PartyField is not null)
                {
                    var partyLabel = profile.PartyLabel ?? "客户/厂商";
                    var name = profile.PartyNameField is null
                        ? string.Empty
                        : PdfLayout.FormatValue(data.Master.GetValueOrDefault(profile.PartyNameField));
                    var partyValue = PdfLayout.FormatValue(data.Master.GetValueOrDefault(profile.PartyField));
                    if (partyValue.Length == 0 && name.Length == 0)
                        partyValue = "—";
                    left.Item().Text(
                        $"{partyLabel}：{partyValue} {name}").FontSize(8);
                }
                var currency = PdfLayout.FormatValue(data.Master.GetValueOrDefault("CURR_ID"));
                var rate = PdfLayout.FormatValue(data.Master.GetValueOrDefault("CURR_RATE"));
                if (currency.Length > 0 || rate.Length > 0)
                    left.Item().Text($"币别：{currency}　汇率：{rate}").FontSize(8);
            });
            row.RelativeItem().AlignRight().Column(right =>
            {
                right.Item().Text(
                    $"业务：{PdfLayout.FormatValue(data.Master.GetValueOrDefault("SALES_ID"))}{PdfLayout.FormatValue(data.Master.GetValueOrDefault("BUYER_ID"))}").FontSize(8);
                right.Item().Text($"制单：{PdfLayout.FormatValue(data.Master.GetValueOrDefault("CREATE_PERSON"))}").FontSize(8);
            });
        });
        content.Item().PaddingTop(6).Table(table =>
        {
            table.ColumnsDefinition(def =>
            {
                def.ConstantColumn(22);
                def.RelativeColumn(2);
                def.RelativeColumn(3);
                def.RelativeColumn(1.2f);
                def.RelativeColumn(1);
                def.RelativeColumn(1.2f);
                def.RelativeColumn(1);
                def.RelativeColumn(1.2f);
            });
            table.Header(headerRow =>
            {
                var labels = new[] { "#", "料号", "品名/规格", "数量", "单位", "单价", "折扣", "金额" };
                foreach (var label in labels)
                    headerRow.Cell().Background(Colors.Grey.Lighten3)
                        .BorderBottom(0.75f).BorderColor(Colors.Grey.Darken1)
                        .Padding(3).Text(label).FontSize(8).SemiBold();
            });
            var index = 0;
            foreach (var row in data.Details)
            {
                index++;
                table.Cell().BorderBottom(0.25f).BorderColor(Colors.Grey.Lighten2).Padding(2)
                    .Text(index.ToString(CultureInfo.InvariantCulture)).FontSize(8);
                table.Cell().BorderBottom(0.25f).BorderColor(Colors.Grey.Lighten2).Padding(2)
                    .Text(PdfLayout.FormatValue(row.GetValueOrDefault("PRO_NO"))).FontSize(8);
                var productName = PdfLayout.FormatValue(row.GetValueOrDefault("PRO_NAME"));
                var productSpec = PdfLayout.FormatValue(row.GetValueOrDefault("PRO_SPEC"));
                var productText = string.IsNullOrEmpty(productSpec) || productSpec.Equals(productName, StringComparison.OrdinalIgnoreCase)
                    ? productName
                    : $"{productName} {productSpec}";
                table.Cell().BorderBottom(0.25f).BorderColor(Colors.Grey.Lighten2).Padding(2).Text(productText).FontSize(8);
                table.Cell().BorderBottom(0.25f).BorderColor(Colors.Grey.Lighten2).Padding(2).AlignRight()
                    .Text(PrintColumnFormats.Format(row.GetValueOrDefault("QTY"), PrintColumnFormats.Quantity)).FontSize(8);
                table.Cell().BorderBottom(0.25f).BorderColor(Colors.Grey.Lighten2).Padding(2)
                    .Text(PdfLayout.FormatValue(row.GetValueOrDefault("UNIT_ID"))).FontSize(8);
                table.Cell().BorderBottom(0.25f).BorderColor(Colors.Grey.Lighten2).Padding(2).AlignRight()
                    .Text(PrintColumnFormats.Format(row.GetValueOrDefault("PRICE"), PrintColumnFormats.Price)).FontSize(8);
                var rebate = PdfLayout.FormatValue(row.GetValueOrDefault("REBATE"));
                table.Cell().BorderBottom(0.25f).BorderColor(Colors.Grey.Lighten2).Padding(2).AlignRight()
                    .Text(string.IsNullOrEmpty(rebate) ? string.Empty : rebate + "%").FontSize(8);
                table.Cell().BorderBottom(0.25f).BorderColor(Colors.Grey.Lighten2).Padding(2).AlignRight()
                    .Text(PrintColumnFormats.Format(row.GetValueOrDefault("AMOUNT_TAX"), PrintColumnFormats.Amount)).FontSize(8);
            }
            if (!string.IsNullOrWhiteSpace(profile.AmountField))
            {
                var amount = PrintColumnFormats.Format(
                    data.Master.GetValueOrDefault(profile.AmountField), PrintColumnFormats.Amount);
                table.Cell().ColumnSpan(7).BorderBottom(0.25f).BorderColor(Colors.Grey.Lighten2).Padding(3).AlignRight()
                    .Text("价税合计").FontSize(8).SemiBold();
                table.Cell().BorderBottom(0.25f).BorderColor(Colors.Grey.Lighten2).Padding(3).AlignRight()
                    .Text(amount.Length == 0 ? "0.00" : amount)
                    .FontSize(8).SemiBold();
            }
        });
        content.Item().PaddingTop(10).Row(signature =>
        {
            signature.RelativeItem().Text($"制表：{PdfLayout.FormatValue(data.Master.GetValueOrDefault("CREATE_PERSON"))}").FontSize(8);
            signature.RelativeItem().AlignCenter().Text($"审核：{PdfLayout.FormatValue(data.Master.GetValueOrDefault("CONFIRM_PERSON"))}").FontSize(8);
            signature.RelativeItem().AlignRight().Text("客户/厂商确认：").FontSize(8);
        });
    }

    private static void RenderGeneric(ColumnDescriptor content, PrintData data, bool showRemark)
    {
        content.Item().Table(table =>
        {
            table.ColumnsDefinition(def =>
            {
                def.ConstantColumn(120);
                def.RelativeColumn();
            });
            foreach (var field in data.MasterFields)
            {
                table.Cell().Padding(3).Text(field.Label).FontSize(8).SemiBold();
                table.Cell().Padding(3).Text(PdfLayout.FormatValue(data.Master.GetValueOrDefault(field.Key), field.DisplayFormat)).FontSize(8);
            }
        });
        RenderDetails(content, data, showRemark, null);
    }

    private static void RenderDetails(ColumnDescriptor content, PrintData data, bool showRemark, string? title)
    {
        var fields = data.DetailFields
            .Where(field => showRemark || !PdfLayout.IsRemarkField(field.Key))
            .ToList();
        if (fields.Count == 0) return;
        if (!string.IsNullOrWhiteSpace(title))
            content.Item().PaddingTop(6).Text(title).FontSize(9).SemiBold();
        content.Item().Table(table =>
        {
            table.ColumnsDefinition(def =>
            {
                foreach (var field in fields) def.RelativeColumn();
            });
            table.Header(headerRow =>
            {
                foreach (var field in fields)
                    headerRow.Cell().Background(Colors.Grey.Lighten3).Padding(3).Text(field.Label).FontSize(8).SemiBold();
            });
            foreach (var row in data.Details)
                foreach (var field in fields)
                    table.Cell().Padding(2).Text(PdfLayout.FormatValue(row.GetValueOrDefault(field.Key), field.DisplayFormat)).FontSize(8);
            if (data.Details.Count > 0)
                foreach (var field in fields)
                {
                    if (!PdfLayout.IsAmountColumn(field.Key)) continue;
                    var total = data.Details.Sum(row =>
                    {
                        var raw = row.GetValueOrDefault(field.Key);
                        return raw is null || raw is DBNull || !decimal.TryParse(
                            Convert.ToString(raw, CultureInfo.InvariantCulture),
                            NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed) ? 0m : parsed;
                    });
                    table.Cell().Padding(3).AlignRight().Text(total.ToString("0.00", CultureInfo.InvariantCulture)).FontSize(8).SemiBold();
                }
        });
    }

}
