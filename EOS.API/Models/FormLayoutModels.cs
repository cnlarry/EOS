namespace EOS.API.Models;

/// <summary>
/// 模块级表单版式：页签集合 + 主表与明细表字段的排列与占位。
/// 它是**模块级事实**（同一物理表被多个模块共用时各模块版式互不影响），随工作台定义一起进快照。
/// 定制粒度是「模块 × 表」：某表一旦有版式行，该表在表单上的字段集即完全由它决定，
/// 未列出的字段视为「未加入表单」，不再回落到字段级的既有配置。
/// </summary>
public sealed record FormLayoutDefinition(
    int Columns,
    IReadOnlyList<FormTabDefinition> Tabs,
    IReadOnlyList<FormLayoutRow> Master,
    IReadOnlyList<FormDetailLayoutRow> Detail,
    bool MasterCustomized = false,
    bool DetailCustomized = false);

/// <summary>主表字段的版式行（页签 / 顺序 / 占位 / 复合格 / 分节 / 表单内隐藏）。</summary>
public sealed record FormLayoutRow(
    string Key,
    int TabNo,
    int OrderNo,
    int Span,
    int RowSpan,
    bool NewLine,
    string? SectionId,
    string? CellGroup,
    int CellRole,
    bool Hidden);

/// <summary>明细字段的版式行：明细是网格，只消费列顺序与列显隐。</summary>
public sealed record FormDetailLayoutRow(string Key, int OrderNo, bool Hidden);
