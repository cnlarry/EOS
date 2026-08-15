using System.Globalization;
using EOS.API.Models;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace EOS.API.Data;

/// <summary>报表 PDF 渲染输入（元数据 + 已授权数据 + 面板选择）。</summary>
public sealed record ReportPdfRenderInput(
    ReportPdfMeta Meta,
    ReportDefinition Definition,
    ReportQueryResult Data,
    string ConditionDescription,
    string PrintPerson,
    IReadOnlyList<string> GroupFields,
    bool ShowGroup,
    bool ShowDetail,
    ReportHeaderOption? Header,
    string? TailText);

/// <summary>
/// 通用报表 PDF 模板（覆盖 RptList/RptList2/RptDirect/RptInteg）：
/// 页头带（公司名/LOGO/报表名/ISO）→ 条件描述 → 明细表（表头跨页重复、
/// 分组表头行、升降序由查询层完成）→ 表尾 → 页脚（页脚文字/列印人/页码）。
/// v1 分组只输出分组表头，不生成小计（技术债）。
/// </summary>
public sealed class ReportPdfService(IWebHostEnvironment environment, ILogger<ReportPdfService> logger)
{
    public byte[] Generate(ReportPdfRenderInput input)
    {
        var meta = input.Meta;
        var definition = input.Definition;
        var header = input.Header;
        var logo = header is null ? null : PdfLayout.TryLoadLogo(environment, header.LogoPath);
        // 与查询层一致：只渲染实际选中的列（主键优先 + 可见列，上限 40），
        // 避免 definition.Columns 全量列（可达 86 列）撑爆 A4 竖版宽度。
        // SP 报表（空主表，如 18029811 考勤分析表）：无 FIELDS 列定义，列由结果集动态提供
        var columns = definition.Columns;
        if (columns.Count == 0 && definition.SpName is not null && input.Data.Rows.Count > 0)
            columns = input.Data.Rows[0].Keys
                .Select(key => new ReportColumn(key, key, "nvarchar", null))
                .ToList();
        else
        {
            var selected = ReportRepository.BuildSelectedColumns(definition);
            columns = definition.Columns
                .Where(column => selected.Contains(column.Key, StringComparer.OrdinalIgnoreCase))
                .ToList();
        }
        var footerText = (meta.FooterText ?? string.Empty)
            .Replace("{7}", input.PrintPerson)
            .Replace("{1}", string.Empty)
            .Replace("{5}", string.Empty);
        var document = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PdfLayout.PageSizeFor(meta.DefaultPaper, columns.Count > 12));
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
                            center.Item().Text(meta.ReportName).FontSize(12).SemiBold();
                            if (!string.IsNullOrWhiteSpace(header?.HeaderText))
                                center.Item().Text(header!.HeaderText).FontSize(8);
                            if (!string.IsNullOrWhiteSpace(meta.IsoNo))
                                center.Item().AlignRight().Text($"ISO：{meta.IsoNo}").FontSize(8);
                        });
                    });
                    headerColumn.Item().PaddingTop(4).LineHorizontal(0.5f);
                });
                page.Content().Column(content =>
                {
                    if (!string.IsNullOrWhiteSpace(input.ConditionDescription))
                        content.Item().PaddingBottom(4).Text(input.ConditionDescription).FontSize(8);
                    content.Item().Table(table => BuildTable(table, input, columns));
                    if (!string.IsNullOrWhiteSpace(input.TailText))
                        content.Item().PaddingTop(8).Text(input.TailText).FontSize(8);
                });
                page.Footer().Row(footer =>
                {
                    footer.RelativeItem().Text(footerText).FontSize(8);
                    footer.RelativeItem().AlignRight().Text(text =>
                    {
                        text.DefaultTextStyle(style => style.FontSize(8));
                        text.Span($"列印人：{input.PrintPerson}　第 ");
                        text.CurrentPageNumber();
                        text.Span(" / ");
                        text.TotalPages();
                        text.Span(" 页");
                    });
                });
            });
        });
        logger.LogDebug("报表 PDF 生成 module={ModuleId} report={ReportId} rows={RowCount}",
            definition.ModuleId, meta.ReportId, input.Data.Rows.Count);
        return document.GeneratePdf();
    }

    private void BuildTable(TableDescriptor table, ReportPdfRenderInput input, IReadOnlyList<ReportColumn> columns)
    {
        if (columns.Count == 0) return;
        table.ColumnsDefinition(definitionBuilder =>
        {
            for (var i = 0; i < columns.Count; i++) definitionBuilder.RelativeColumn();
        });
        table.Header(headerRow =>
        {
            foreach (var column in columns)
                headerRow.Cell().Background(Colors.Grey.Lighten3).Padding(3)
                    .Text(column.Label).FontSize(8).SemiBold();
        });

        var subtotalColumns = columns
            .Where(column => PdfLayout.IsSubtotalColumn(column, input.Definition.MasterPkOrder))
            .ToList();
        var summaries = BuildGroupSummaries(
            input.Data.Rows, input.GroupFields, input.ShowGroup, input.ShowDetail,
            subtotalColumns.Select(column => column.Key).ToList());
        logger.LogDebug("报表 PDF 分组 module={ModuleId} rows={Rows} groupFields={GroupFields} subtotalColumns={SubtotalColumns} summaries={Summaries} firstKey={FirstKey}",
            input.Definition.ModuleId, input.Data.Rows.Count, string.Join(',', input.GroupFields),
            subtotalColumns.Count, summaries.Count, summaries.Count > 0 ? summaries[0].Key : "<none>");
        if (input.ShowGroup && input.GroupFields.Count > 0 && summaries.Count > 0)
        {
            var rowIndex = 0;
            foreach (var summary in summaries)
            {
                table.Cell().ColumnSpan((uint)columns.Count).Background(Colors.Blue.Lighten5).Padding(3)
                    .Text(summary.Key).FontSize(8).SemiBold();
                while (rowIndex < input.Data.Rows.Count && GroupKeyOf(input.Data.Rows[rowIndex], input.GroupFields) == summary.Key)
                {
                    if (input.ShowDetail) EmitDetailRow(table, columns, input.Data.Rows[rowIndex]);
                    rowIndex++;
                }
                EmitGroupFooter(table, columns, summary);
            }
            for (; rowIndex < input.Data.Rows.Count; rowIndex++)
                if (input.ShowDetail) EmitDetailRow(table, columns, input.Data.Rows[rowIndex]);
        }
        else
        {
            foreach (var row in input.Data.Rows)
                if (input.ShowDetail) EmitDetailRow(table, columns, row);
        }
    }

    private static void EmitDetailRow(TableDescriptor table, IReadOnlyList<ReportColumn> columns, Dictionary<string, object?> row)
    {
        foreach (var column in columns)
        {
            var value = PdfLayout.FormatValue(row.GetValueOrDefault(column.Key), column.DisplayFormat);
            var cell = table.Cell().Padding(2);
            if (column.DataType.Contains("float", StringComparison.OrdinalIgnoreCase)
                || column.DataType.Contains("int", StringComparison.OrdinalIgnoreCase)
                || column.DataType.Contains("decimal", StringComparison.OrdinalIgnoreCase))
                cell = cell.AlignRight();
            cell.Text(value).FontSize(8);
        }
    }

    private static string? GroupKeyOf(Dictionary<string, object?> row, IReadOnlyList<string> groupFields)
    {
        if (groupFields.Count == 0) return null;
        return string.Join(" / ", groupFields.Select(field =>
            PdfLayout.FormatValue(row.GetValueOrDefault(FieldName(field)))));
    }

    private static void EmitGroupFooter(
        TableDescriptor table,
        IReadOnlyList<ReportColumn> columns,
        GroupSummary summary)
    {
        var subtotal = summary.Totals.ToDictionary(pair => pair.Column, pair => pair.Total, StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < columns.Count; i++)
        {
            var cell = table.Cell().Background(Colors.Grey.Lighten2).Padding(2);
            if (i == 0)
            {
                cell.Text("小计").FontSize(8).SemiBold();
                continue;
            }
            if (!subtotal.TryGetValue(columns[i].Key, out var total))
            {
                cell.Text(string.Empty).FontSize(8);
                continue;
            }
            cell = cell.AlignRight();
            cell.Text(PdfLayout.FormatValue(total, columns[i].DisplayFormat)).FontSize(8).SemiBold();
        }
    }

    internal sealed record GroupSummary(string Key, IReadOnlyList<(string Column, decimal Total)> Totals);

    /// <summary>
    /// 分组小计聚合（纯逻辑，便于单元测试）：按分组字段顺序聚合同组行，
    /// 对 subtotalKeys 数值列求和；隐藏明细时不参与小计。
    /// </summary>
    internal static List<GroupSummary> BuildGroupSummaries(
        IReadOnlyList<Dictionary<string, object?>> rows,
        IReadOnlyList<string> groupFields,
        bool showGroup,
        bool showDetail,
        IReadOnlyList<string> subtotalKeys)
    {
        var summaries = new List<GroupSummary>();
        string? current = null;
        var totals = new decimal[subtotalKeys.Count];
        foreach (var row in rows)
        {
            var key = showGroup && groupFields.Count > 0
                ? string.Join(" / ", groupFields.Select(field =>
                    PdfLayout.FormatValue(row.GetValueOrDefault(FieldName(field)))))
                : null;
            if (key is not null && key != current)
            {
                if (current is not null) summaries.Add(BuildSummary(current, subtotalKeys, totals));
                totals = new decimal[subtotalKeys.Count];
                current = key;
            }
            if (key is not null && !showDetail) continue;
            for (var i = 0; i < subtotalKeys.Count; i++)
            {
                var raw = row.GetValueOrDefault(subtotalKeys[i]);
                if (raw is null || raw is DBNull) continue;
                if (decimal.TryParse(Convert.ToString(raw, CultureInfo.InvariantCulture),
                        NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed))
                    totals[i] += parsed;
            }
        }
        if (current is not null) summaries.Add(BuildSummary(current, subtotalKeys, totals));
        return summaries;
    }

    private static GroupSummary BuildSummary(string key, IReadOnlyList<string> subtotalKeys, decimal[] totals)
    {
        var items = new List<(string Column, decimal Total)>(subtotalKeys.Count);
        for (var i = 0; i < subtotalKeys.Count; i++) items.Add((subtotalKeys[i], totals[i]));
        return new GroupSummary(key, items);
    }

    private static string FieldName(string qualified) => qualified.Contains('.')
        ? qualified.Split('.')[^1]
        : qualified;
}
