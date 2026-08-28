namespace EOS.API.Models;

/// <summary>报表定义维护草稿（REPORT，旧 2201/229801 的受控等价）。</summary>
public sealed record ReportAdminDraft(
    string ReportId,
    string? ReportName,
    int? ModuleId,
    string? IsoNo,
    string? HeaderId,
    string? TailId,
    string? FooterText,
    string? DefaultPaper,
    bool IsDefault,
    string? ReportFilter,
    string? DefaultPrinter,
    string? Remark);

/// <summary>排序/分组方案维护草稿（REPORT_SORT）。</summary>
public sealed record ReportSortDraft(
    int SerialNo,
    string? SortName,
    string? SortFields,
    string? SortDesc,
    string? GroupName,
    string? GroupFields,
    string? GroupDesc);

/// <summary>可维护报表定义的模块（有 RPT 入口或已有报表定义的工作台模块）。</summary>
public sealed record ReportAdminModuleOption(int ModuleId, string Description);

/// <summary>排序/分组字段选择器选项（主表/明细表白名单字段，Table.Column + 描述）。</summary>
public sealed record ReportAdminFieldOption(string Table, string Column, string Label);

/// <summary>报表维护所需页头/表尾选项。</summary>
public sealed record ReportAdminHeaderTailOption(string Id, string Name);

/// <summary>
/// 报表过滤条件行/草稿（SYSQR_DEFAULT，2205 报表过滤条件设置的受控等价）。
/// Type 沿用旧 RptList2 语义：1 范围 / 2 固定单选 / 3 从数据表单选 / 4 固定多选 / 5 从数据表多选；
/// Expression 按类型承载：2/4 为「标签:值;标签:值」选项 DSL，3/5 为
/// 「SELECT 列 C_ID, 列 C_VALUE FROM 表」数据源语句，1 为空（不使用）。
/// </summary>
public sealed record ReportConditionDraft(
    int SerialNo,
    int Type,
    string? Field,
    string? Expression,
    string? Description,
    string? DefaultValue,
    string? ParameterName,
    string? Remark);
