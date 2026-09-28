using System.Globalization;
using EOS.API.Models;

namespace EOS.API.Data;

/// <summary>
/// 报表清单的**分组小计**聚合（纯逻辑，便于单独验证）。
///
/// <para>
/// 从原先的命令式报表 PDF 服务里搬出来：那段逻辑（按分组字段顺序聚合同组行、对数值列求和、
/// 隐藏明细时不参与小计）本身与"怎么画 PDF"无关，混在渲染代码里就只能靠生成 PDF 间接验证。
/// 搬出来之后它是可断言的纯函数，渲染层只负责把它算出来的小计画成一行。
/// </para>
/// </summary>
public static class ReportListGrouping
{
    /// <summary>一组的小计：分组键 + 每个参与求和的列及其合计。</summary>
    public sealed record GroupSummary(string Key, IReadOnlyList<(string Column, decimal Total)> Totals);

    /// <summary>分组键的取值（多个分组字段用 " / " 连接；无分组字段返回 null）。</summary>
    public static string? GroupKeyOf(IReadOnlyDictionary<string, object?> row, IReadOnlyList<string> groupFields)
    {
        if (groupFields.Count == 0) return null;
        return string.Join(" / ", groupFields.Select(field =>
            PdfLayout.FormatValue(row.GetValueOrDefault(FieldName(field)))));
    }

    /// <summary>
    /// 按行序聚合同组行并对 <paramref name="subtotalKeys"/> 数值列求和。
    /// 隐藏明细（<paramref name="showDetail"/> = false）时该组只输出小计行，明细行不参与渲染——
    /// 但仍要参与聚合，否则"只看小计"会得到全 0。
    /// </summary>
    public static List<GroupSummary> BuildGroupSummaries(
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
                if (current is not null) summaries.Add(Build(current, subtotalKeys, totals));
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
        if (current is not null) summaries.Add(Build(current, subtotalKeys, totals));
        return summaries;
    }

    private static GroupSummary Build(string key, IReadOnlyList<string> subtotalKeys, decimal[] totals)
    {
        var items = new List<(string Column, decimal Total)>(subtotalKeys.Count);
        for (var i = 0; i < subtotalKeys.Count; i++) items.Add((subtotalKeys[i], totals[i]));
        return new GroupSummary(key, items);
    }

    /// <summary>去掉 `表.列` 形式里的表限定（分组字段可能带限定）。</summary>
    private static string FieldName(string qualified) => qualified.Contains('.')
        ? qualified.Split('.')[^1]
        : qualified;
}
