using System.Text.Json.Serialization;

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
    IReadOnlyList<ReportOption> Options,
    ReportSelectSource? SelectSource = null,
    string? DefaultValueTo = null);

/// <summary>F_TYPE 3 数据单选：白名单表 + 选项列（受控解析，不执行任意 SQL）。</summary>
public sealed record ReportSelectSource(string Table, string IdColumn, string ValueColumn);

public sealed record ReportOption(string Label, string Value);

/// <summary>报表字段列（FIELDS + 物理列 + 权限过滤后，含显示格式）。</summary>
public sealed record ReportColumn(string Key, string Label, string DataType, string? DisplayFormat = null);

public sealed record ReportDefinition(
    int ModuleId,
    string Title,
    string MasterTable,
    string? DetailTable,
    IReadOnlyList<ReportCondition> Conditions,
    IReadOnlyList<ReportColumn> Columns,
    IReadOnlyList<string> MasterPkOrder,
    IReadOnlyList<string> SortFields,
    string? SpName = null,
    IReadOnlyList<ReportSpParameter> SpParameters = null!,
    string? ModuleFilter = null)
{
    /// <summary>汇总报表受控数据源；为空表示按主表（+ 子表）列构建查询。不下发客户端。</summary>
    [JsonIgnore] public ReportAggregate? Aggregate { get; init; }

    /// <summary>数据源类型：table 主表查询 / aggregate 服务端聚合 / sproc 遗留过程。</summary>
    public string DataSource => Aggregate is not null ? "aggregate" : SpName is not null ? "sproc" : "table";

    /// <summary>
    /// 参数面板元数据：聚合报表来自服务端注册表（按查询条件序号取值），
    /// 遗留过程报表来自 sys.parameters（按序号位置取值）。
    /// </summary>
    public IReadOnlyList<ReportAggregateParameter> Parameters =>
        Aggregate is not null
            ? Aggregate.Parameters
            : SpParameters.Select((item, index) => new ReportAggregateParameter(item.Name, item.DataType, item.MaxLength, index + 1)).ToList();
}

public sealed record ReportSpParameter(string Name, string DataType, int MaxLength);

/// <summary>
/// 汇总报表受控数据源的参数绑定：按查询条件序号（<see cref="SerialNo"/>）取"起值"或"止值"
/// （范围条件），或使用固定常量。值一律参数化传入，不拼接用户输入。
/// </summary>
public sealed record ReportAggregateParameter(
    string Name,
    string DataType,
    int MaxLength,
    int SerialNo = 0,
    bool IsTo = false,
    string? Constant = null);

/// <summary>
/// 汇总报表（RptInteg）受控数据源：聚合 SQL、默认排序与输出列全部来自服务端注册表；
/// 客户端只能提供条件值，不能提供 SQL 片段。
/// </summary>
public sealed record ReportAggregate(
    string ReportId,
    string Sql,
    string OrderBy,
    IReadOnlyList<ReportAggregateParameter> Parameters,
    IReadOnlyList<ReportColumn> Columns);

public sealed record ReportQueryRequest(
    IReadOnlyDictionary<int, string?> Values,
    IReadOnlyDictionary<int, string?> ValuesTo);

public sealed record ReportQueryResult(
    IReadOnlyList<Dictionary<string, object?>> Rows,
    int Total,
    int Page,
    int PageSize);

/// <summary>打印面板中的可选报表（REPORT，按预览权限过滤）。</summary>
public sealed record ReportPrintOption(
    string ReportId,
    string ReportName,
    string? HeaderId,
    string? TailId,
    string? FooterText,
    string? IsoNo,
    bool IsDefault);

/// <summary>页头（REPORT_HEADER）。</summary>
public sealed record ReportHeaderOption(
    string HeaderId,
    string HeaderName,
    string CompanyName,
    string? CompanyNameEn,
    string? HeaderText,
    string? LogoPath,
    string? LogoUrl);

/// <summary>表尾（REPORT_TAIL）。</summary>
public sealed record ReportTailOption(string TailId, string TailName, string TailText);

/// <summary>排序/分组方案（REPORT_SORT）。</summary>
public sealed record ReportSortScheme(
    int SerialNo,
    string SortName,
    string? SortFields,
    string? GroupName,
    string? GroupFields);

/// <summary>用户最近一次打印设置（SYSQR IS_LAST=1）。</summary>
public sealed record ReportUserPrintSettings(
    string? ReportId,
    string? HeaderId,
    string? TailId,
    int? SortSerialNo,
    bool SortAsc,
    bool ShowGroup,
    bool ShowDetail);

/// <summary>报表打印面板设置（GET）。</summary>
public sealed record ReportPrintSettings(
    int ModuleId,
    IReadOnlyList<ReportPrintOption> Reports,
    IReadOnlyList<ReportHeaderOption> Headers,
    IReadOnlyList<ReportTailOption> Tails,
    IReadOnlyDictionary<string, IReadOnlyList<ReportSortScheme>> SortSchemesByReport,
    ReportUserPrintSettings? UserSettings);

/// <summary>报表打印设置保存（POST，写入 SYSQR）。</summary>
public sealed record ReportPrintSettingsRequest(
    string? ReportId,
    string? HeaderId,
    string? TailId,
    int? SortSerialNo,
    bool SortAsc,
    bool ShowGroup,
    bool ShowDetail,
    IReadOnlyDictionary<int, string?>? Values = null,
    IReadOnlyDictionary<int, string?>? ValuesTo = null);

/// <summary>报表 PDF 生成请求。</summary>
public sealed record ReportPdfRequest(
    string? ReportId,
    string? HeaderId,
    string? TailId,
    IReadOnlyDictionary<int, string?>? Values,
    IReadOnlyDictionary<int, string?>? ValuesTo,
    int? SortSerialNo,
    bool SortDirect,
    bool ShowGroup,
    bool ShowDetail);

/// <summary>单据 PDF 生成请求。</summary>
public sealed record DocumentPdfRequest(
    IReadOnlyList<string> Key,
    string? ReportId = null,
    string? HeaderId = null,
    string? TailId = null,
    bool ShowRemark = true);

/// <summary>报表 PDF 渲染所需的元数据（服务端解析后的白名单结果）。</summary>
public sealed record ReportPdfMeta(
    string ReportId,
    string ReportName,
    string? HeaderId,
    string? TailId,
    string? FooterText,
    string? IsoNo,
    string? ReportFilter,
    ReportHeaderOption? Header,
    string? TailText,
    IReadOnlyList<ReportSortScheme> SortSchemes);

/// <summary>页头维护草稿（2202 页头设置）。</summary>
public sealed record PrintHeaderDraft(
    string HeaderId,
    string? HeaderName,
    string? CompanyName,
    string? CompanyNameEn,
    string? HeaderText,
    string? LogoPath);

/// <summary>表尾维护草稿（2204 表尾设置）。</summary>
public sealed record PrintTailDraft(string TailId, string? TailName, string? TailText);

/// <summary>页脚维护草稿（2203 页尾设置）。</summary>
public sealed record PrintFooterDraft(string FooterId, string? FooterName, string? FooterText);
