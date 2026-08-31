namespace EOS.API.Models;

/// <summary>设计器权限模式（ADR-010 决策 5 双模式 + 三档权限）。</summary>
public sealed record LayoutDesignerMode(bool CanDesign, bool CanAdjust);

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
    string? Variant = null);

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

/// <summary>页头条目（REPORT_LAYOUT KIND='HEADER'，ADR-009 §9.4.2 页头字典引用）。</summary>
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

/// <summary>版式绑定保存请求（HEADER_ID/TAIL_ID/PRINT_PRICE 随绑定，ADR-010 决策 3）。</summary>
public sealed record LayoutBindingSaveRequest(
    string? ClientId,
    string? HeaderId,
    string? TailId,
    bool? PrintPrice);
