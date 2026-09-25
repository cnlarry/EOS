using System.Text.RegularExpressions;
using EOS.API.Models;

namespace EOS.API.Data.Forms;

/// <summary>
/// 默认版式推导的输入：字段级既有版式元数据（FIELDS.FORM_*）与该字段的可见性/必填口径。
/// 仅取推导真正用到的列，便于纯函数单测与前后端对齐。
/// </summary>
public sealed record FormLayoutFieldInput(
    string Key,
    string DataType,
    bool IsVisible,
    bool IsRequired,
    int TabNo,
    int? FormOrder,
    int Span,
    bool NewLine,
    string? CellGroup,
    int CellRole);

/// <summary>
/// 模块级表单版式的纯函数：默认推导（零配置模块用）与版式施加（已定制模块用）。
///
/// 默认推导与前端共用同一套规则：页签取既有配置（无配置即空列表，渲染侧兜底为单页签「默认」）；
/// 顺序取字段级排序位，缺省按传入次序；列跨度取字段级配置，备注类整行；行跨度备注类 2、其余 1；
/// 分节不推导（新模型里分节是显式配置）；复合格沿用字段级主从；隐藏只针对「本来就进不了表单」的字段。
///
/// **施加只在已定制的表上发生**：未定制的模块，其字段集与顺序仍由既有的字段级配置决定，
/// 默认推导的结果只是"该模块若开始定制时的起点"，不参与运行期渲染——这样零配置模块的
/// 表单定义逐字不变（对拍判据）。
/// </summary>
public static class FormLayoutDerivation
{
    /// <summary>行跨度上限：三行 ≈ 78px，更高诉求改用多行文本控件高度。</summary>
    public const int MaxRowSpan = 3;

    /// <summary>
    /// 统一表单的栅格列数（固定四子列）。
    /// 取 4 而非 2：既有统一表单历史上就固定按 4 列排布，而 279 个工作台模块里 140 个未声明列数——
    /// 默认成 2 会让这些模块的表单从每行 4 个字段变成每行 2 个，属纯粹的观感回退。
    /// `MODULES.FORM_COLUMNS` 已退役（用户 2026-08-31 拍板"全局固定一行四列"），列数不再随模块变。
    /// </summary>
    public const int DefaultColumns = 4;

    private static readonly Regex RemarkKey = new(@"REMARK$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>备注类/长文本字段：整行独占且占两行高（与前端同一判据）。</summary>
    public static bool IsWideText(string? key, string? dataType)
    {
        var type = (dataType ?? string.Empty).Trim();
        if (type.Equals("text", StringComparison.OrdinalIgnoreCase)
            || type.Equals("ntext", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        return RemarkKey.IsMatch(key ?? string.Empty);
    }

    /// <summary>
    /// 【已退休，运行时不调用】按字段级配置推导默认版式。
    /// 模块级版式是唯一真源后，无版式行 = 未定制（字段按元数据顺序原样渲染），不再推导；
    /// 本方法与 <see cref="FormLayoutFieldInput"/> 随字段级配置列一并删除（P4 删列批次）。
    /// </summary>
    public static FormLayoutDefinition DeriveDefault(
        IReadOnlyList<FormLayoutFieldInput> master,
        IReadOnlyList<FormLayoutFieldInput> detail,
        int columns)
    {
        var cols = columns > 0 ? columns : DefaultColumns;
        return new FormLayoutDefinition(
            cols,
            [],
            DeriveRows(master, cols),
            DeriveRows(detail, cols).Select(row => new FormDetailLayoutRow(row.Key, row.OrderNo, row.Hidden)).ToList());
    }

    private static IReadOnlyList<FormLayoutRow> DeriveRows(IReadOnlyList<FormLayoutFieldInput> fields, int columns)
    {
        var rows = new List<FormLayoutRow>(fields.Count);
        for (var index = 0; index < fields.Count; index++)
        {
            var field = fields[index];
            var wide = IsWideText(field.Key, field.DataType);
            var group = string.IsNullOrWhiteSpace(field.CellGroup) ? null : field.CellGroup.Trim();
            var cellRole = field.CellRole;
            rows.Add(new FormLayoutRow(
                field.Key.Trim(),
                field.TabNo > 0 ? field.TabNo : 1,
                field.FormOrder ?? index + 1,
                // 字段级旧语义映射到子列：FORM_SPAN=1（半行）→ 2 子列、=2（整行独占）→ 4 子列；
                // 备注类始终整行。统一表单固定 4 子列，故这里不再按模块列数缩放。
                wide ? columns : Math.Clamp(field.Span, 1, 2) * (columns / 2),
                wide ? 2 : 1,
                field.NewLine,
                SectionId: null,
                group,
                cellRole,
                Hidden: !field.IsVisible && !field.IsRequired && !(cellRole == 2 && group is not null)));
        }
        return rows;
    }

    /// <summary>
    /// 主表字段施加版式：按版式行重排，剔除「未加入表单」的行；属性以版式行为准。
    /// 未定制的表原样返回（不改字段集与顺序）。权限过滤后的字段集是输入——版式引用不到的字段
    /// 直接跳过：**版式只做减法与排布，不能把权限挡掉的字段变出来**。
    /// </summary>
    public static IReadOnlyList<FormFieldDefinition> ApplyMasterLayout(
        IReadOnlyList<FormFieldDefinition> fields,
        FormLayoutDefinition? layout)
    {
        if (layout is not { MasterCustomized: true } || layout.Master.Count == 0) return fields;

        var byKey = new Dictionary<string, FormFieldDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in fields) byKey.TryAdd(field.Key, field);

        var result = new List<FormFieldDefinition>(layout.Master.Count);
        foreach (var row in layout.Master.OrderBy(row => row.OrderNo))
        {
            if (row.Hidden || !byKey.TryGetValue(row.Key, out var field)) continue;
            result.Add(field with
            {
                TabNo = row.TabNo,
                FormOrder = row.OrderNo,
                Span = Math.Clamp(row.Span, 1, Math.Max(1, layout.Columns)),
                RowSpan = Math.Clamp(row.RowSpan, 1, MaxRowSpan),
                NewLine = row.NewLine,
                SectionId = row.SectionId,
                CellGroup = string.IsNullOrWhiteSpace(row.CellGroup) ? null : row.CellGroup.Trim(),
                CellRole = row.CellRole,
            });
        }
        return result;
    }

    /// <summary>
    /// 明细列顺序施加版式：按版式行重排并剔除隐藏列；未定制的表原样返回。
    /// 返回的是输入里确实存在的键（调用方随后仍会补回必填与引擎必需的列，见明细构建顺序）。
    /// </summary>
    public static IReadOnlyList<string> ApplyDetailOrder(IReadOnlyList<string> keys, FormLayoutDefinition? layout)
    {
        if (layout is not { DetailCustomized: true } || layout.Detail.Count == 0) return keys;

        var present = new HashSet<string>(keys, StringComparer.OrdinalIgnoreCase);
        var ordered = new List<string>(keys.Count);
        foreach (var row in layout.Detail.OrderBy(row => row.OrderNo))
        {
            if (row.Hidden || !present.Contains(row.Key)) continue;
            if (ordered.Any(key => key.Equals(row.Key, StringComparison.OrdinalIgnoreCase))) continue;
            ordered.Add(keys.First(key => key.Equals(row.Key, StringComparison.OrdinalIgnoreCase)));
        }
        return ordered;
    }
}
