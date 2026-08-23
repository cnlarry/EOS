namespace EOS.API.Security;

/// <summary>
/// 权限资源模块号集中定义（ADR-005 §6），消除控制器中的魔法数字。
/// </summary>
public static class PermissionModules
{
    /// <summary>系统管理（用户权限设定 2306）：Workbench Definition 快照发布/回填等管理动作的门。</summary>
    public const int SystemManagement = 2306;
}
