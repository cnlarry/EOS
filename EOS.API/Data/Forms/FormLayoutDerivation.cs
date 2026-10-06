using EOS.API.Models;

namespace EOS.API.Data.Forms;

/// <summary>
/// 模块级表单版式的纯函数：把模块级版式施加到权限过滤后的字段视图上（重排、剔除未加入表单的
/// 字段、覆盖占位与复合格角色）。无版式行 = 未定制，字段按元数据顺序原样渲染。
///
/// **施加只在已定制的表上发生**：未定制的模块，其字段集与顺序保持字段元数据读取的原样——
/// 这样零配置模块的表单定义逐字不变（对拍判据）。
/// </summary>
public static class FormLayoutDerivation
{
    /// <summary>行跨度上限：三行 ≈ 78px，更高诉求改用多行文本控件高度。</summary>
    public const int MaxRowSpan = 3;

    /// <summary>
    /// 页签未声明栅格列数时的兜底列数（四子列）。与库内 `MODULE_FORM_TAB.LAYOUT_COLUMNS`
    /// 的 `DEFAULT 4` 同值：库内一律落具体值，这个常量只兜"历史快照里 `tabs[].columns` 为空"。
    ///
    /// 取 4 而非 2：既有统一表单历史上就固定按 4 列排布——
    /// 默认成 2 会让这些模块的表单从每行 4 个字段变成每行 2 个，属纯粹的观感回退。
    /// </summary>
    public const int DefaultColumns = 4;

    /// <summary>页签可声明的列数区间（库内 CHECK 约束同此）。</summary>
    public const int MinColumns = 1;

    /// <summary>页签可声明的列数上限。</summary>
    public const int MaxColumns = 4;

    /// <summary>
    /// **行所属页签的布局列数**：取该页签声明的列数（`MODULE_FORM_TAB.LAYOUT_COLUMNS`，
    /// 库内 NOT NULL DEFAULT 4），越界或未声明（历史快照）回落 <see cref="DefaultColumns"/>。
    ///
    /// 列数是页签级事实——同一表单的页签 1 一行两列、页签 2 一行一列是允许的，
    /// 所以夹取/校验/渲染都不能拿整份版式的单值去判（用户 2026-10-06 拍板；模块级那一层已随迁移 322 删除）。
    /// </summary>
    public static int ResolveTabColumns(IReadOnlyList<FormTabDefinition> tabs, int tabNo)
    {
        var declared = tabs.FirstOrDefault(tab => tab.No == tabNo)?.Columns;
        return declared is >= MinColumns and <= MaxColumns ? declared.Value : DefaultColumns;
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
        if (layout is not { MasterCustomized: true } || layout.Master.Count == 0)
        {
            // 未定制：字段集与顺序原样保留（零配置模块的观感不变），只摘掉虚拟列——
            // 虚拟列是"选择器回写的伴生显示列"，只有版式显式排入时才作为只读字段出现在表单上，
            // 否则每个模块都会凭空多出一批没有输入控件语义的列。
            return fields.Where(field => !field.IsVirtual).ToList();
        }

        var byKey = new Dictionary<string, FormFieldDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in fields) byKey.TryAdd(field.Key, field);

        var result = new List<FormFieldDefinition>(layout.Master.Count);
        foreach (var row in layout.Master.OrderBy(row => row.OrderNo))
        {
            if (row.Hidden || !byKey.TryGetValue(row.Key, out var field)) continue;
            // 夹取按**该行所属页签**的列数：页签 1 两列、页签 2 一列的表单里，
            // 用整份版式的单值去夹会把页签 2 的格放成两列
            var rowColumns = ResolveTabColumns(layout.Tabs, row.TabNo);
            result.Add(field with
            {
                TabNo = row.TabNo,
                FormOrder = row.OrderNo,
                Span = Math.Clamp(row.Span, 1, Math.Max(1, rowColumns)),
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
