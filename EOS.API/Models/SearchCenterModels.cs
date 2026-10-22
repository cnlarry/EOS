namespace EOS.API.Models;

public sealed record SearchableModule(
    int ModuleId,
    string Title,
    string MasterTable,
    string? DetailTable,
    bool SearchMaster,
    bool SearchDetail);

public sealed record SearchField(
    string Key,
    string Label,
    string DataType,
    // 显示口径与统一工作台同源（FIELDS 的同一批列）：宽度、对齐、格式、数值语义族。
    // 查询中心不另定一套，否则同一字段在列表与查询中心会长得不一样。
    int Width = 0,
    string? Align = null,
    string? HeaderAlign = null,
    string? DisplayFormat = null,
    // 数值语义族（ADR-038）：前端据此把金额默认显示成两位小数。
    string? NumericRole = null);

/// <summary>
/// 检索定义。字段面与统一工作台**同一来源**（模块定义里的字段，已按字段级权限过滤），
/// 因此同一用户在这里能搜到的字段，与他在该模块列表里能看到的字段一致。
/// </summary>
public sealed record SearchDefinition(
    int ModuleId,
    string Title,
    string Table,
    IReadOnlyList<SearchField> Fields,
    IReadOnlyList<SearchField> Columns,
    IReadOnlyList<string> PkOrder,
    // 主表主键：查明细表时用它把结果行定位回所属单据（前端据此给出「打开单据」）。
    IReadOnlyList<string> MasterPkOrder);

/// <summary>
/// 检索请求：结构化多条件 + 关键字 + 多列排序（与统一工作台完全同一口径：
/// 条件解析与排序白名单都复用 <see cref="EOS.API.Data.WorkbenchQueryComposer"/>）。
/// </summary>
public sealed record SearchQueryRequest(
    string Table,
    IReadOnlyList<WorkbenchQueryCondition>? Conditions = null,
    string? Keyword = null,
    // 逗号分隔、与 <see cref="Conditions"/> 同一套白名单；空则按主键排序（与工作台一致）
    string? SortFields = null,
    string? SortDirections = null);

public sealed record SearchQueryResult(
    IReadOnlyList<Dictionary<string, object?>> Rows,
    int Total,
    int Page,
    int PageSize);
