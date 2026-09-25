using EOS.API.Models;

namespace EOS.API.Data.Forms;

/// <summary>
/// 设计态提交的规范化：把"用户按下保存时的整份版式"编译成库内行模型。
///
/// 三条约定在这里一次定死，避免各调用点各写一份：
/// · 页签必须含常驻的 1 号页签（其余页签可删，删掉页签的字段回落到它）；
/// · 排列序号由**提交顺序**重排为 1..n（不信任前端传来的序号，避免碎片序号）；
/// · 列跨度夹到 <c>[1, 模块列数]</c>、行跨度夹到 <c>[1, 3]</c>，复合格角色只认 0/1/2。
/// </summary>
internal static class FormLayoutSubmission
{
    public static FormLayoutDefinition Normalize(FormLayoutSaveRequest request, int columns)
    {
        var effectiveColumns = columns > 0 ? columns : 2;

        var tabs = (request.Tabs ?? [])
            .Where(tab => tab.No > 0)
            .GroupBy(tab => tab.No)
            .Select(group => new FormTabDefinition(group.Key, group.First().Title?.Trim() ?? string.Empty))
            .OrderBy(tab => tab.No)
            .ToList();
        if (tabs.All(tab => tab.No != 1))
        {
            tabs.Insert(0, new FormTabDefinition(1, string.Empty));
        }

        var master = (request.Master ?? [])
            .Where(row => !string.IsNullOrWhiteSpace(row.Key))
            .Select((row, index) => new FormLayoutRow(
                row.Key.Trim(),
                row.TabNo > 0 ? row.TabNo : 1,
                index + 1,
                Math.Clamp(row.Span, 1, effectiveColumns),
                Math.Clamp(row.RowSpan, 1, FormLayoutDerivation.MaxRowSpan),
                row.NewLine,
                Trim(row.SectionId),
                Trim(row.CellGroup),
                row.CellRole is 1 or 2 ? row.CellRole : 0,
                row.Hidden))
            .ToList();

        var detail = (request.Detail ?? [])
            .Where(row => !string.IsNullOrWhiteSpace(row.Key))
            .Select((row, index) => new FormDetailLayoutRow(row.Key.Trim(), index + 1, row.Hidden))
            .ToList();

        return new FormLayoutDefinition(
            effectiveColumns, tabs, master, detail, MasterCustomized: true, DetailCustomized: true);
    }

    private static string? Trim(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
