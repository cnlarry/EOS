namespace EOS.API.Models;

/// <summary>报表查询条件定义（SYSQR_DEFAULT 受控解释）。</summary>
public sealed record ReportCondition(
    int SerialNo,
    string? Field,
    string Desc,
    int Type, // 1 范围 / 2 固定单选 / 4 固定多选
    string? Expression,
    string? DefaultValue,
    string? ParameterName,
    IReadOnlyList<ReportOption> Options);

public sealed record ReportOption(string Label, string Value);

/// <summary>报表字段列（FIELDS + 物理列 + 权限过滤后）。</summary>
public sealed record ReportColumn(string Key, string Label, string DataType);

public sealed record ReportDefinition(
    int ModuleId,
    string Title,
    string MasterTable,
    string? DetailTable,
    IReadOnlyList<ReportCondition> Conditions,
    IReadOnlyList<ReportColumn> Columns,
    IReadOnlyList<string> MasterPkOrder,
    IReadOnlyList<string> SortFields);

public sealed record ReportQueryRequest(
    IReadOnlyDictionary<int, string?> Values,
    IReadOnlyDictionary<int, string?> ValuesTo);

public sealed record ReportQueryResult(
    IReadOnlyList<Dictionary<string, object?>> Rows,
    int Total,
    int Page,
    int PageSize);
