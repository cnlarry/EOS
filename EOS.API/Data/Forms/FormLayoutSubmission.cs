using EOS.API.Models;

namespace EOS.API.Data.Forms;

/// <summary>
/// 设计态提交的规范化：把"用户按下保存时的整份版式"编译成库内行模型。
///
/// 三条约定在这里一次定死，避免各调用点各写一份：
/// · 页签必须含常驻的 1 号页签（其余页签可删，删掉页签的字段回落到它）；
/// · 排列序号由**提交顺序**重排为 1..n（不信任前端传来的序号，避免碎片序号）；
/// · 列跨度夹到 <c>[1, 该行所属页签的列数]</c>、行跨度夹到 <c>[1, 3]</c>，复合格角色只认 0/1/2。
///
/// 列数是**页签级**的：取该页签声明的列数（`FormTabInput.Columns`），未给（null）落兜底
/// <see cref="FormLayoutDerivation.DefaultColumns"/>——库内那一列是 NOT NULL DEFAULT 4，
/// 写入侧不落 NULL（模块级那一层已随迁移 322 删除）。
/// 用户 2026-10-06 拍板"调整布局时自动把越界跨度夹到布局列数"，
/// 所以写入侧照旧夹取——库里不落"看着排了、其实排不出来"的跨度。
/// </summary>
internal static class FormLayoutSubmission
{
    public static FormLayoutDefinition Normalize(FormLayoutSaveRequest request)
    {
        var tabs = (request.Tabs ?? [])
            .Where(tab => tab.No > 0)
            .GroupBy(tab => tab.No)
            .Select(group => new FormTabDefinition(
                group.Key,
                group.First().Title?.Trim() ?? string.Empty,
                NormalizeTabColumns(group.First().Columns)))
            .OrderBy(tab => tab.No)
            .ToList();
        if (tabs.All(tab => tab.No != 1))
        {
            tabs.Insert(0, new FormTabDefinition(1, string.Empty, FormLayoutDerivation.DefaultColumns));
        }

        var master = (request.Master ?? [])
            .Where(row => !string.IsNullOrWhiteSpace(row.Key))
            .Select((row, index) =>
            {
                var tabNo = row.TabNo > 0 ? row.TabNo : 1;
                return new FormLayoutRow(
                    row.Key.Trim(),
                    tabNo,
                    index + 1,
                    Math.Clamp(row.Span, 1, FormLayoutDerivation.ResolveTabColumns(tabs, tabNo)),
                    Math.Clamp(row.RowSpan, 1, FormLayoutDerivation.MaxRowSpan),
                    row.NewLine,
                    Trim(row.SectionId),
                    Trim(row.CellGroup),
                    row.CellRole is 1 or 2 ? row.CellRole : 0,
                    row.Hidden);
            })
            .ToList();

        var detail = (request.Detail ?? [])
            .Where(row => !string.IsNullOrWhiteSpace(row.Key))
            .Select((row, index) => new FormDetailLayoutRow(row.Key.Trim(), index + 1, row.Hidden))
            .ToList();

        return new FormLayoutDefinition(
            tabs, master, detail, MasterCustomized: true, DetailCustomized: true);
    }

    /// <summary>
    /// 呈现配置（打开方式 / 弹窗宽高）的写入侧解析：设计器与模块管理两侧同一条口径。
    ///
    /// 与版式的区别是**这几项是可选段**：全 null 表示"这次不动它们"（只存版式的调用方行为不变），
    /// 一旦给了值就逐项严格校验——非法值抛 <see cref="ArgumentException"/>
    /// （由统一异常出口映射为 400），不放行到库里撞 CHECK 约束变成 500。
    /// 非弹窗方式下窗体宽高没有意义，读回与运行态一致地一律写 NULL（避免留下一份"看着配了、其实不生效"的值）。
    ///
    /// **栅格列数不在其中**：列数从 2026-10-06 起是**页签级事实**（`MODULE_FORM_TAB.LAYOUT_COLUMNS`，
    /// 见 <see cref="Normalize"/>），呈现配置里不再有它——这里也就不该再写 `MODULES.FORM_LAYOUT_COLUMNS`
    /// （它退化为"未声明页签的兜底列数"，由迁移与库内数据自行维护，设计器不再改它）。
    /// </summary>
    public static FormPresentation ParsePresentation(FormLayoutSaveRequest request)
    {
        // 三项全空 = 这份请求没带呈现配置（只存版式）：不动 MODULES 的呈现列
        var given = request.OpenMode is not null || request.DialogWidth is not null
            || request.DialogHeight is not null;
        if (!given)
        {
            return FormPresentation.None;
        }
        var openMode = FormOpenModes.ParseForWrite(request.OpenMode);
        var isDialog = FormOpenModes.IsDialog(openMode);
        var width = isDialog ? CheckDialogSize(request.DialogWidth, "宽度", FormDialogLimits.MinWidth, FormDialogLimits.MaxWidth) : null;
        var height = isDialog ? CheckDialogSize(request.DialogHeight, "高度", FormDialogLimits.MinHeight, FormDialogLimits.MaxHeight) : null;
        return new FormPresentation(openMode, width, height, IsGiven: true);
    }

    /// <summary>
    /// 页签级列数的写入侧解析：未给（null）= 落兜底 4 列（与库内 DEFAULT 同值，写入侧不落 NULL）；
    /// 给了值必须落在 1..4，否则抛 <see cref="ArgumentException"/>（统一异常出口映射为 400），
    /// 不放行到库里去撞 CHECK 约束变成 500。
    /// </summary>
    private static int NormalizeTabColumns(int? declared)
    {
        if (declared is null)
        {
            return FormLayoutDerivation.DefaultColumns;
        }
        if (declared is < FormLayoutDerivation.MinColumns or > FormLayoutDerivation.MaxColumns)
        {
            throw new ArgumentException(
                $"页签布局列数只能取 {FormLayoutDerivation.MinColumns}..{FormLayoutDerivation.MaxColumns}，收到 {declared}。");
        }
        return declared.Value;
    }

    private static int? CheckDialogSize(int? value, string name, int min, int max)
    {
        if (value is null)
        {
            return null;
        }
        if (value < min || value > max)
        {
            throw new ArgumentException($"弹窗{name}只能取 {min}..{max}（px），收到 {value}。");
        }
        return value;
    }

    private static string? Trim(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>
/// 呈现配置的写入载荷。**整段要么全给、要么不给**：<see cref="IsGiven"/> 为真时三项一律按本载荷写下去
/// （值 null 即"清空该列"，例如从弹窗改回本页签要把窗体宽高清掉）；为假时一个字都不动——
/// 只存版式的调用方（含历史客户端）因此不会把模块的呈现配置清空。
/// </summary>
internal sealed record FormPresentation(
    string? OpenMode, int? DialogWidth, int? DialogHeight, bool IsGiven)
{
    /// <summary>本次不带呈现配置（重置版式走这条）。</summary>
    public static readonly FormPresentation None = new(null, null, null, IsGiven: false);
}
