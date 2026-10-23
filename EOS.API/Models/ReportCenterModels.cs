namespace EOS.API.Models;

/// <summary>报表中心目录项（响应契约）。</summary>
public sealed record ReportCatalogItem(
    int ModuleId,
    string ModuleDesc,
    string DomainDesc,
    string ReportId,
    string ReportName,
    bool IsDefault,
    bool Favorite,
    int SortIndex,
    DateTime? LastRunAt);

/// <summary>收藏开关/排序请求。SortIndex 缺省表示本次不改收藏顺序。</summary>
public sealed record ReportFavoriteRequest(
    int ModuleId,
    string? ReportId,
    bool Favorite = false,
    int? SortIndex = null);

/// <summary>最近使用登记请求（时间戳由服务端生成）。</summary>
public sealed record ReportTouchRequest(
    int ModuleId,
    string? ReportId);

/// <summary>报表中心收藏重排项。</summary>
public sealed record ReportReorderItem(int ModuleId, string? ReportId);

/// <summary>收藏重排请求。</summary>
public sealed record ReportReorderRequest(IReadOnlyList<ReportReorderItem>? Items);
