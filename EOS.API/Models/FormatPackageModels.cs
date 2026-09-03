using System.Text.Json.Serialization;

namespace EOS.API.Models;

// ============================================================================
// 格式包模型：format.json / layout.json / sample.json 的运行时契约。
// layout.json 同时是存储格式、渲染契约与设计器文档模型，
// 三个消费方共用同一份 schema（layout.schema.v1.json）与本节类型。
// 坐标单位：mm（套打行业惯例）；QuestPDF 默认单位 point，解释层统一换算 mm→pt。
// ============================================================================

/// <summary>format.json：一份格式的完整定义（开发态资产，进 Git，code review 后发布）。</summary>
public sealed record ReportFormatDefinition(
    string FormatId,
    string Kind,
    string Title,
    int ModuleId,
    ReportFormatDataContract DataContract,
    IReadOnlyList<ReportFormatParam> Params,
    IReadOnlyList<ReportFormatGrouping> Grouping,
    IReadOnlyList<ReportFormatBinding> Bindings);

/// <summary>dataContract：服务端字段白名单来源（保存时校验 layout 字段引用，§9.6 约束 2/3）。</summary>
public sealed record ReportFormatDataContract(
    string? MasterTable,
    IReadOnlyList<ReportContractColumn> Columns,
    string? DetailTable,
    IReadOnlyList<ReportContractColumn> DetailColumns);

public sealed record ReportContractColumn(string Key, string Label, string Type, double? Width = null);

public sealed record ReportFormatParam(string Name, string Field, string Operator, string Label);

public sealed record ReportFormatGrouping(string Field, string Sort, string Aggregate);

/// <summary>bindings：单据打印型绑定 (docType=模块, clientId)；clientId 为空 = 单据类型默认。</summary>
public sealed record ReportFormatBinding(string DocType, string? ClientId);

/// <summary>格式包 = format.json + layout.json（+ sample.json 回归护栏）。</summary>
public sealed record ReportFormatPackage(
    ReportFormatDefinition Format,
    LayoutDocument Layout,
    string RawLayoutJson,
    string? RawSampleJson = null);

// ============================================================================
// layout.json schema v1（见 ReportFormats/layout.schema.v1.json）
// ============================================================================

public sealed record LayoutDocument(
    int SchemaVersion,
    string Kind,
    LayoutPage Page,
    LayoutSections Sections,
    LayoutPageTemplates? PageTemplates = null);

/// <summary>多页模板（第一页 / 续页 / 末页各自的页头页脚，缺省回退默认 sections）。</summary>
public sealed record LayoutPageTemplates(
    LayoutPageTemplate? First,
    LayoutPageTemplate? Continuation,
    LayoutPageTemplate? Last);

public sealed record LayoutPageTemplate(LayoutSection? Header, LayoutSection? Footer);

public sealed record LayoutPage(string Size, string Orientation, LayoutMargin Margin);

public sealed record LayoutMargin(double Top, double Right, double Bottom, double Left);

public sealed record LayoutSections(LayoutSection Header, LayoutSection Content, LayoutSection Footer);

public sealed record LayoutSection(double? Height, IReadOnlyList<LayoutElement> Elements);

/// <summary>
/// 元素最小集：text / field / image / line / rect / table。
/// x/y 为相对所属 section 顶部的 mm 坐标（绝对定位，非流式）。
/// </summary>
public sealed record LayoutElement(
    string Id,
    string Type,
    double X,
    double Y,
    double W,
    double H,
    bool Visible = true,
    string? Content = null,
    string? Field = null,
    string? Format = null,
    string? ResourceId = null,
    string? BarcodeType = null,
    string? BarcodeErrorCorrection = null,
    string? BarcodeColor = null,
    string? BarcodeBackground = null,
    string? BarcodeLogo = null,
    string? DataSource = null,
    IReadOnlyList<LayoutColumn>? Columns = null,
    bool? ShowHeader = null,
    bool? RepeatHeaderOnPageBreak = null,
    bool? ShowTotals = null,
    string? TotalsLabel = null,
    string? TotalsField = null,
    int? MaxRows = null,
    string? Title = null,
    double? RowHeight = null,
    LayoutElementStyle? Style = null);

public sealed record LayoutColumn(
    string Field,
    string Label,
    double? Width = null,
    string? Align = "left",
    string? Format = null,
    string? Suffix = null,
    bool? IsAmount = null,
    bool? NegativeRed = null);

public sealed record LayoutElementStyle(
    double? FontSize = null,
    bool? Bold = null,
    bool? SemiBold = null,
    bool? Italic = null,
    string? Align = "left",
    string? Color = null,
    string? BackgroundColor = null,
    string? BorderColor = null,
    double? BorderWidth = null,
    double? Padding = null,
    double? LineWidth = null,
    bool? Striped = null);

/// <summary>渲染上下文（解释层扩展参数，接口默认调用可省略）。</summary>
public sealed record LayoutRenderContext(string? PrintPerson = null, bool ShowRemark = true);
