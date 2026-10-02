using EOS.API.Data;
using EOS.API.Models;
using EOS.API.Security;

namespace EOS.API.Features.Assistant.Tools;

/// <summary>
/// 助手侧的**报表窄网关**：把"列可见报表"与"按编号取数"两件事收成一个可替换的入口。
///
/// <para>
/// 为什么工具不直接依赖报表仓储：工具要能**离线**验证自己的门禁与输出（无浏览权、报表不存在、
/// 条件序号不合法、行数截断），而报表仓储是绑着数据库连接的具体类。网关把这两层分开——
/// 与工作台侧的 <see cref="IWorkbenchSearchGateway"/> 同一手法：底层复用既有实现
/// （可见性判定与取数一行都不重写），上层给工具一个可替换的窄契约。
/// </para>
///
/// <para>
/// 契约里刻意**不出现模块号入参**：报表的归属模块只能由 <see cref="FindIdentityAsync"/> 解析出来，
/// 调用方说了不算（否则任何人都能把一个报表编号挂到别的模块上用那个模块的权限打开它）。
/// </para>
/// </summary>
public interface IReportGateway
{
    /// <summary>按模块列当前用户**可见**的报表（可见性口径在实现里，与打印面板同源）。</summary>
    Task<IReadOnlyList<ReportSibling>> ListReportsAsync(int moduleId, string userId, CancellationToken token);

    /// <summary>解析报表身份（编号全库唯一）；不存在返回 null，按防探测口径回答。</summary>
    Task<ReportIdentity?> FindIdentityAsync(string reportId, CancellationToken token);

    /// <summary>构建报表定义：列清单与字段级权限（成本位 / 保密位 / 禁止字段）。</summary>
    Task<ReportDefinition?> GetDefinitionAsync(
        int moduleId, string userId, ModuleRights rights, string reportId, CancellationToken token);

    /// <summary>执行取数：模块 FILTER + 用户 <c>DATA_FILTER</c> + 参数化条件。</summary>
    Task<ReportQueryResult> QueryAsync(
        ReportDefinition definition, ReportQueryRequest request, int page, int pageSize,
        string? dataFilter, CancellationToken token);
}

/// <summary>
/// <see cref="IReportGateway"/> 的默认实现：**只做转发**，判定与取数全在既有仓储里。
/// 这一层不加任何权限逻辑——多一处判定就多一处会与报表页面不一致的地方。
/// </summary>
public sealed class ReportGateway(
    ReportRepository reports,
    PrintSettingsRepository printSettings) : IReportGateway
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<ReportSibling>> ListReportsAsync(
        int moduleId, string userId, CancellationToken token)
    {
        // 打印设置仓储的清单已按归属模块的 REPORT_TAG 过滤（个人 SYSDD 优先，否则组 SYSDH 取或）
        var settings = await printSettings.GetAsync(moduleId, userId, token);
        return [.. settings.Reports.Select(item => new ReportSibling(item.ReportId, item.ReportName, item.IsDefault))];
    }

    /// <inheritdoc />
    public Task<ReportIdentity?> FindIdentityAsync(string reportId, CancellationToken token) =>
        reports.FindIdentityAsync(reportId, token);

    /// <inheritdoc />
    public Task<ReportDefinition?> GetDefinitionAsync(
        int moduleId, string userId, ModuleRights rights, string reportId, CancellationToken token) =>
        reports.GetDefinitionAsync(
            moduleId, userId, rights.CanViewCost, rights.CanViewSecrecy, rights.DeniedMasterFields, reportId, token);

    /// <inheritdoc />
    public Task<ReportQueryResult> QueryAsync(
        ReportDefinition definition, ReportQueryRequest request, int page, int pageSize,
        string? dataFilter, CancellationToken token) =>
        reports.QueryAsync(definition, request, page, pageSize, dataFilter, token);
}
