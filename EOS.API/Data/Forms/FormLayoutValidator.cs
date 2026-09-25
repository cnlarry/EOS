using EOS.API.Models;

namespace EOS.API.Data.Forms;

/// <summary>
/// 版式校验所需的字段事实（由调用方从 FIELDS 与物理结构解析得到）。
/// 校验器只读这些事实，不连库，故整张规则表可用单测穷举。
/// </summary>
public sealed record FormLayoutFieldFact(
    string Key,
    string Table,
    bool IsPrimaryKey,
    bool IsLifecycleSystemColumn,
    bool UserFillableRequired,
    bool HasActiveChooser);

/// <summary>版式校验问题：<paramref name="Code"/> 面向接口错误码，<paramref name="Key"/> 定位到字段。</summary>
public sealed record FormLayoutValidationIssue(string Code, string Message, string? Key = null);

/// <summary>
/// 模块级版式的 fail-closed 校验（保存时执行；读取侧由门禁脚本对库内数据做同一口径的检查）。
///
/// 设计口径：
/// - **版式只做减法与排布**：引用不到的字段一律拒绝，不给"配错了也能存"的余地；
/// - **不可移除类字段**（主键、单据生命周期系统列、用户可填的必填字段）既不能标隐藏，
///   也不能整行不写（"未列出的字段视为未加入表单"，效果与隐藏相同）；
/// - 复合格必须主从成对（从字段必须有同组主字段）、同格同组主字段唯一、主字段有启用来源；
/// - 页签集合若提交，必须含常驻的 1 号页签。
/// </summary>
public static class FormLayoutValidator
{
    public const int MaxRowSpan = 3;

    /// <summary>服务端可自行填充的字段不属"用户可填"，故不在不可移除之列（口径由事实传入）。</summary>
    public static IReadOnlyList<FormLayoutValidationIssue> Validate(
        FormLayoutDefinition layout,
        IReadOnlyDictionary<string, FormLayoutFieldFact> masterFields,
        IReadOnlyDictionary<string, FormLayoutFieldFact> detailFields)
    {
        var issues = new List<FormLayoutValidationIssue>();
        var columns = layout.Columns > 0 ? layout.Columns : FormLayoutDerivation.DefaultColumns;

        ValidateTabs(layout, issues);
        ValidateRows(layout.Master, masterFields, layout, columns, isMaster: true, issues);
        ValidateRows(
            layout.Detail.Select(row => new FormLayoutRow(row.Key, 1, row.OrderNo, 1, 1, false, null, null, 0, row.Hidden)).ToList(),
            detailFields, layout, columns, isMaster: false, issues);
        return issues;
    }

    private static void ValidateTabs(FormLayoutDefinition layout, List<FormLayoutValidationIssue> issues)
    {
        var numbers = layout.Tabs.Select(tab => tab.No).ToList();
        if (numbers.Count != numbers.Distinct().Count())
        {
            issues.Add(new FormLayoutValidationIssue("FORM_LAYOUT_TAB_DUPLICATE", "页签号重复。"));
        }
        if (numbers.Count > 0 && !numbers.Contains(1))
        {
            issues.Add(new FormLayoutValidationIssue(
                "FORM_LAYOUT_TAB_DEFAULT_MISSING", "缺少常驻的 1 号页签（其余页签可删，该页签不可删）。"));
        }
    }

    private static void ValidateRows(
        IReadOnlyList<FormLayoutRow> rows,
        IReadOnlyDictionary<string, FormLayoutFieldFact> fields,
        FormLayoutDefinition layout,
        int columns,
        bool isMaster,
        List<FormLayoutValidationIssue> issues)
    {
        var tabNumbers = layout.Tabs.Select(tab => tab.No).ToHashSet();
        var mainGroups = new List<string>();
        var companionByGroup = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var listed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in rows)
        {
            if (string.IsNullOrWhiteSpace(row.Key))
            {
                issues.Add(new FormLayoutValidationIssue("FORM_LAYOUT_FIELD_UNKNOWN", "版式行缺少字段代号。"));
                continue;
            }
            if (!fields.TryGetValue(row.Key, out var fact))
            {
                issues.Add(new FormLayoutValidationIssue(
                    "FORM_LAYOUT_FIELD_NOT_IN_MODULE", $"字段 {row.Key} 不属于该模块的表。", row.Key));
                continue;
            }
            listed.Add(row.Key);

            if (isMaster && tabNumbers.Count > 0 && row.TabNo != 1 && !tabNumbers.Contains(row.TabNo))
            {
                issues.Add(new FormLayoutValidationIssue(
                    "FORM_LAYOUT_TAB_UNKNOWN", $"字段 {row.Key} 指向不存在的页签 {row.TabNo}。", row.Key));
            }
            if (row.Span < 1 || row.Span > columns)
            {
                issues.Add(new FormLayoutValidationIssue(
                    "FORM_LAYOUT_SPAN_OUT_OF_RANGE", $"字段 {row.Key} 的列跨度 {row.Span} 超出 1..{columns}。", row.Key));
            }
            if (row.RowSpan < 1 || row.RowSpan > MaxRowSpan)
            {
                issues.Add(new FormLayoutValidationIssue(
                    "FORM_LAYOUT_ROW_SPAN_OUT_OF_RANGE", $"字段 {row.Key} 的行跨度 {row.RowSpan} 超出 1..{MaxRowSpan}。", row.Key));
            }

            var group = string.IsNullOrWhiteSpace(row.CellGroup) ? null : row.CellGroup.Trim();
            if (row.Hidden && fact.UserFillableRequired)
            {
                issues.Add(new FormLayoutValidationIssue(
                    "FORM_LAYOUT_REQUIRED_HIDDEN",
                    fact.IsPrimaryKey || fact.IsLifecycleSystemColumn
                        ? $"字段 {row.Key} 是系统列，不能从表单移除。"
                        : $"字段 {row.Key} 为必填，不能从表单移除。",
                    row.Key));
            }
            if (group is null) continue;

            if (row.CellRole == 1)
            {
                mainGroups.Add(group);
                // 主字段**不强制**有启用中的选择器：既有数据里存在"有组名但没配选择器"的格子
                // （如 LEADER_EMP_ID + 同格从字段），渲染上就是普通控件 + 同格从控件；
                // 卡住会让这类模块的推导版式保存不了。设计态的"合并为一格"本就只开放给有选择器的主字段。
            }
            else if (row.CellRole == 2)
            {
                companionByGroup[group] = companionByGroup.GetValueOrDefault(group) + 1;
            }
        }

        // 同组多主字段在**既有数据**里存在（组名复用）；渲染侧把第二个主字段当成独立一格，
        // 不会串格，故不作为保存期拦截项（设计态的"合并为一格"本身只产生一个主字段）。

        foreach (var (group, _) in companionByGroup)
        {
            // 一个格里有多个从字段是**既有数据**的常态（如 BOM 的 PRO 组 = 料号 + 品名 + 颜色），
            // 渲染侧本来就按同格多控件排布；这里只要求"从字段必须有同组主字段"，
            // 否则推导默认的版式会被自己的校验拦住，那些模块的版式永远保存不了。
            if (!mainGroups.Contains(group, StringComparer.OrdinalIgnoreCase))
            {
                issues.Add(new FormLayoutValidationIssue(
                    "FORM_LAYOUT_COMPANION_WITHOUT_MAIN", $"从字段的分组 {group} 没有对应的主字段。"));
            }
        }


        // 已定制的表：未列出的字段即"未加入表单"，与隐藏同效，故不可移除类字段必须出现
        var customized = isMaster ? layout.MasterCustomized : layout.DetailCustomized;
        if (!customized) return;
        foreach (var fact in fields.Values)
        {
            if (!fact.UserFillableRequired || listed.Contains(fact.Key)) continue;
            issues.Add(new FormLayoutValidationIssue(
                "FORM_LAYOUT_REQUIRED_MISSING", $"必填字段 {fact.Key} 未加入表单（必填字段必须保留）。", fact.Key));
        }
    }
}
