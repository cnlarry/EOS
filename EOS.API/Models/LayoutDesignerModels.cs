namespace EOS.API.Models;

/// <summary>设计器权限模式：只有完整设计一档（微调档已退役）。</summary>
public sealed record LayoutDesignerMode(bool CanDesign);

/// <summary>生效版式（内置或客户定制），供设计器编辑与打印读取。</summary>
public sealed record EffectiveLayout(
    string LayoutJson,
    bool IsCustom,
    int? LayoutId,
    string? HeaderId,
    string? TailId,
    bool? PrintPrice);

/// <summary>设计器保存请求（copy-on-write）。</summary>
public sealed record LayoutDesignerSaveRequest(
    string LayoutJson,
    string? ClientId = null);

/// <summary>设计器预览请求：可选数据场景（rows=明细行数；variant=normal/longText/empty）。</summary>
public sealed record LayoutDesignerPreviewRequest(
    string LayoutJson,
    string? ClientId = null,
    int? Rows = null,
    string? Variant = null,
    IReadOnlyList<string>? Key = null,
    string? ReportId = null,
    string? HeaderId = null,
    bool ShowRemark = true);

/// <summary>内置格式包模板（模板库）。</summary>
public sealed record LayoutTemplateInfo(
    string FormatId,
    string Title,
    int ModuleId,
    string Kind);

/// <summary>版式版本历史条目（REPORT_FORM_LAYOUT_VERSION）。</summary>
public sealed record LayoutVersionInfo(
    int Version,
    string CreatePerson,
    DateTime CreateDate);

/// <summary>设计器 definition 响应：布局 + 字段白名单 + 权限模式 + 定制状态。</summary>
public sealed record LayoutDesignerDefinition(
    string FormatId,
    string Title,
    bool IsCustom,
    int? LayoutId,
    LayoutDesignerMode Mode,
    string LayoutJson,
    ReportFormatDataContract DataContract,
    IReadOnlyList<string> SystemFields,
    string? HeaderId,
    string? TailId,
    bool? PrintPrice);

/// <summary>页头条目（REPORT_LAYOUT KIND='HEADER'， 页头字典引用）。</summary>
public sealed record LayoutHeaderOption(
    string HeaderId,
    string Name,
    string? Company,
    string? CompanyEn,
    string? HeaderText,
    string? LogoPath);

/// <summary>页头条目保存请求（完整设计权限 CanDesign）。</summary>
public sealed record LayoutHeaderSaveRequest(
    string HeaderId,
    string Name,
    string? Company,
    string? CompanyEn,
    string? HeaderText,
    string? LogoPath);

/// <summary>版式绑定保存请求（HEADER_ID/TAIL_ID/PRINT_PRICE 随绑定）。</summary>
public sealed record LayoutBindingSaveRequest(
    string? ClientId,
    string? HeaderId,
    string? TailId,
    bool? PrintPrice);
