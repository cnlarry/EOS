using EOS.API.Models;
using Microsoft.AspNetCore.Hosting;

namespace EOS.API.Data;

/// <summary>
/// 把"报表打印输入"（<see cref="ReportPdfRenderInput"/>）整理成解释层要的两件东西：
/// 渲染上下文里的报表数据 + 一个承载页面文案的 <see cref="PrintData"/>
/// （解释层的分析日志与 `{{SYS.*}}` 仍然读它）。
///
/// <para>
/// 这里**只做搬运与投影，不做绘制**：真正画 PDF 的只有解释层一处。
/// 原先那段命令式渲染（硬编码页头、等宽列、分组小计）已经拆成两部分——
/// 页面结构进版式资产，分组小计进 <see cref="ReportListGrouping"/>，
/// "怎么画"不再有第二份实现，也就不会出现"两份实现各自漂移"。
/// </para>
/// </summary>
public static class ReportListPdfComposer
{
    /// <summary>
    /// 打印用的列清单：与查询层同一口径——只渲染实际选中的列（主键优先 + 可见列，上限 40），
    /// 避免 definition.Columns 全量列（可达 86 列）撑爆纸张；汇总报表列由注册表声明，全部渲染。
    /// </summary>
    public static IReadOnlyList<ReportColumn> SelectPrintColumns(ReportDefinition definition)
    {
        if (definition.Aggregate is not null) return definition.Columns;
        var selected = ReportRepository.BuildSelectedColumns(definition);
        return definition.Columns
            .Where(column => selected.Contains(column.Key, StringComparer.OrdinalIgnoreCase))
            .ToList();
    }

    /// <summary>参与分组小计的列（数值列且不在主键里）。</summary>
    public static IReadOnlyList<string> SelectSubtotalKeys(ReportDefinition definition, IReadOnlyList<ReportColumn> columns)
        => columns
            .Where(column => PdfLayout.IsSubtotalColumn(column, definition.MasterPkOrder))
            .Select(column => column.Key)
            .ToList();

    /// <summary>报告条件描述/表尾里的 `{7}` 等占位符替换（与既有打印口径一致）。</summary>
    public static string ExpandFooterText(string? footerText, string printPerson)
        => (footerText ?? string.Empty)
            .Replace("{7}", printPerson)
            .Replace("{1}", string.Empty)
            .Replace("{5}", string.Empty);

    /// <summary>
    /// 组装解释层输入。<paramref name="environment"/> 用于按页头配置读 LOGO 字节——
    /// 读的是字节而不是路径：渲染层不该知道文件系统布局，也不该自己拼路径。
    /// </summary>
    public static (PrintData Data, LayoutRenderContext Context) Compose(
        ReportPdfRenderInput input,
        IWebHostEnvironment environment)
    {
        var meta = input.Meta;
        var definition = input.Definition;
        var header = input.Header;
        var columns = SelectPrintColumns(definition);
        var logo = header is null ? null : PdfLayout.TryLoadLogo(environment, header.LogoPath);
        var footerText = ExpandFooterText(meta.FooterText, input.PrintPerson);

        var payload = new ReportListPayload(
            Title: meta.ReportName,
            IsoNo: meta.IsoNo,
            Conditions: input.ConditionDescription ?? string.Empty,
            TailText: input.TailText,
            FooterText: footerText,
            CompanyName: header?.CompanyName,
            CompanyNameEn: header?.CompanyNameEn,
            HeaderText: header?.HeaderText,
            Logo: logo,
            Columns: columns,
            Rows: input.Data.Rows,
            SubtotalKeys: SelectSubtotalKeys(definition, columns),
            GroupFields: input.GroupFields,
            ShowGroup: input.ShowGroup,
            ShowDetail: input.ShowDetail);

        // 页面文案同时挂到 PrintData 上：解释层的日志、`{{SYS.*}}` 与条码元素仍从这里取值。
        var data = new PrintData(
            ModuleId: definition.ModuleId,
            Title: meta.ReportName,
            HeaderCompany: header?.CompanyName,
            HeaderCompanyEn: header?.CompanyNameEn,
            HeaderText: header?.HeaderText,
            FooterText: footerText,
            LogoPath: header?.LogoPath,
            TailText: input.TailText,
            MasterFields: [],
            DetailFields: columns.Select(column => new PrintField(column.Key, column.Label, column.DisplayFormat)).ToList(),
            Master: new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase),
            Details: input.Data.Rows.Cast<IReadOnlyDictionary<string, object?>>().ToList());

        return (data, new LayoutRenderContext(input.PrintPerson, true, payload));
    }
}
