namespace EOS.API.Models;

/// <summary>
/// 统一选择器查询请求（POST /api/chooser/query）。
/// sourceKey 由服务端注册表解析；args 为白名单校验后的数据源参数（如 menu-admin.fields 的 tableId）；
/// 关键字/排序/分页均为结构化参数，动态标识符不来自前端。
/// </summary>
public sealed record UnifiedChooserQueryRequest(
    string SourceKey,
    IReadOnlyDictionary<string, string>? Args = null,
    string? Keyword = null,
    string? FilterField = null,
    IReadOnlyList<UnifiedChooserCondition>? Conditions = null,
    string? SortField = null,
    string? SortDirection = null,
    int Page = 1,
    int PageSize = 50);

/// <summary>
/// 高级查询条件（与工作台 WorkbenchQueryCondition 同一套运算符语义）：
/// 字段必须命中数据源白名单，值全部参数化；logic 表示与下一条条件的连接词（and/or）。
/// </summary>
public sealed record UnifiedChooserCondition(
    string Field,
    string Operator,
    string? Value = null,
    string? ValueTo = null,
    string Logic = "and");

/// <summary>统一选择器显示列（与前端 UnifiedChooserColumn 对应）。</summary>
public sealed record UnifiedChooserColumn(string Key, string Label, string DataType, string? Format);

/// <summary>
/// 统一选择器分页结果（columns/rows/total，与 document-workbench form-chooser 协议一致）。
/// rows 为「列键 → 值」字典，字符串值统一 Trim。
/// </summary>
public sealed record UnifiedChooserResult(
    IReadOnlyList<UnifiedChooserColumn> Columns,
    IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows,
    int Total);
