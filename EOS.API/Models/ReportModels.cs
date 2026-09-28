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

/// <summary>
/// 报表字段列（FIELDS + 物理列 + 权限过滤后，含显示格式）。
///
/// <para>
/// <see cref="IsCost"/> / <see cref="IsSecrecy"/> / <see cref="DenyKey"/> 是**汇总报表聚合列**的
/// 字段级权限声明：聚合列是 SQL 算出来的派生列，不属于任何物理表，因此没有 <c>FIELDS</c> 行可供
/// 反查成本位、保密位与用户级拒绝名单——只能在数据源注册表里逐列显式声明。
/// </para>
/// <para>
/// 主表分支（<c>FIELDS</c> 派生列）在构造本类型之前就已完成同口径过滤，故保持默认 <c>null</c>。
/// 默认 <c>null</c> 的语义是「**未声明**」，不是「非成本/非保密」——聚合分支按 fail-closed 处理：
/// 任一权限位未声明即丢弃该列（漏标不得等于公开），并由构建期门禁强制注册表逐列显式声明，
/// 避免"运行期静默少列"变成无人发现的退化。
/// </para>
/// </summary>
/// <param name="DenyKey">用户级拒绝名单的匹配键，语义等同 <c>FIELDS.F_ID</c>；留空即用 <see cref="Key"/>。</param>
public sealed record ReportColumn(
    string Key,
    string Label,
    string DataType,
    string? DisplayFormat = null,
    [property: JsonIgnore] bool? IsCost = null,
    [property: JsonIgnore] bool? IsSecrecy = null,
    [property: JsonIgnore] string? DenyKey = null);

public sealed record ReportDefinition(
    int ModuleId,
    string Title,
    string MasterTable,
    string? DetailTable,
    IReadOnlyList<ReportCondition> Conditions,
    IReadOnlyList<ReportColumn> Columns,
    IReadOnlyList<string> MasterPkOrder,
    IReadOnlyList<string> SortFields,
    string? ModuleFilter = null)
{
    /// <summary>汇总报表受控数据源；为空表示按主表（+ 子表）列构建查询。不下发客户端。</summary>
    [JsonIgnore] public ReportAggregate? Aggregate { get; init; }

    /// <summary>数据源类型：table 主表查询 / aggregate 服务端聚合。列与排序一律来自服务端元数据。</summary>
    public string DataSource => Aggregate is not null ? "aggregate" : "table";

    /// <summary>参数面板元数据：仅汇总报表有（来自服务端注册表，按查询条件序号取值）。</summary>
    public IReadOnlyList<ReportAggregateParameter> Parameters =>
        Aggregate?.Parameters ?? [];
}

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
    string? Constant = null,
    /// <summary>
    /// 取值来自**系统参数**（`SYSSS`，见 <see cref="SystemParameterService"/>）而不是前端。
    /// 用于"报表与预警必须读同一个阈值"这类参数：写死在注册表里的常量做不到"改了参数报表跟着变"，
    /// 而让用户每次手填，两处口径迟早分叉。
    /// </summary>
    string? SystemParameterKey = null);

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

/// <summary>
/// 报表身份：报表编号是**全局唯一**的，归属模块由服务端解析出来。
/// </summary>
/// <remarks>
/// 归属模块必须由服务端查，不能由调用方（URL 参数、请求体）说了算——否则任何人都能把一个
/// 报表编号挂到别的模块号上用那个模块的权限打开它。解析结果同时充当权限判定的锚点：
/// 编号不存在 → 404；存在但当前用户对该模块没有浏览权 → 403。
/// </remarks>
public sealed record ReportIdentity(int ModuleId, string ReportId, string ReportName, string ModuleName)
{
    /// <summary>同一归属模块下**当前用户可见**的其他报表（含默认报表优先序）。</summary>
    public IReadOnlyList<ReportSibling> Siblings { get; init; } = [];
}

/// <summary>归属模块下的报表清单（"按归属模块列报表"的载荷）。</summary>
public sealed record ReportModuleReports(int ModuleId, string ModuleName, IReadOnlyList<ReportSibling> Reports);

/// <summary>同一归属模块下的另一张报表（用于查看器内的报表切换与工具条清单）。</summary>
public sealed record ReportSibling(string ReportId, string ReportName, bool IsDefault);

/// <summary>
/// 打印面板中的可选报表（REPORT，按预览权限过滤）。<paramref name="FormatId"/> 为该报表专有的
/// 版式包编号（留空即用模块默认版式）——同一模块的多个报表（如送货单 / 拣货单）据此各印各的版式。
/// </summary>
public sealed record ReportPrintOption(
    string ReportId,
    string ReportName,
    string? HeaderId,
    string? TailId,
    string? FooterText,
    string? IsoNo,
    bool IsDefault,
    string? FormatId = null);

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
