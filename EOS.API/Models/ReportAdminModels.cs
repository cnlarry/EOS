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
    bool IsDefault,
    string? ReportFilter,
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
