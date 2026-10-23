using EOS.API.Models;

namespace EOS.API.Security;

/// <summary>
/// EOS.API 内唯一模块权限判断入口：
/// 控制器与仓储不再直接调用 ModuleRightsRepository 做模块级权限决策；
/// 个人覆盖组、组布尔 OR、EXEC_TAG 最大值规则由底层聚合保持不变。
/// </summary>
public interface IPermissionService
{
    /// <summary>取模块权限（不校验动作，用于需要区分「无权限→404」与「拒绝→403」的路径）。</summary>
    Task<ModulePermission> GetAsync(string userId, int moduleId, CancellationToken cancellationToken);

    /// <summary>取模块权限并要求指定动作；不满足时抛 PermissionDeniedException（403）。</summary>
    Task<ModulePermission> RequireAsync(string userId, int moduleId, PermissionAction action, CancellationToken cancellationToken);

    /// <summary>
    /// 取报表权限（预览/打印/导出与报表级数据范围+禁止字段）。报表权的判定同样落在**归属模块**上，
    /// 与模块权共用同一套聚合，故一并收在这里——否则报表系控制器仍要直接拿权限仓储。
    /// </summary>
    Task<ReportRights> GetReportAsync(string userId, int moduleId, string reportId, CancellationToken cancellationToken);
}
