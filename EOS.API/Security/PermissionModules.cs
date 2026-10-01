namespace EOS.API.Security;

/// <summary>
/// 权限资源模块号集中定义，消除控制器中的魔法数字。
/// </summary>
public static class PermissionModules
{
    /// <summary>系统管理（用户权限设定 2306）：Workbench Definition 快照发布/回填等管理动作的门。</summary>
    public const int SystemManagement = 2306;

    /// <summary>
    /// 工作助手管理（菜单组 31）下的模块号。见 ADR-030：新开根组而不是挂 23，
    /// 权限门沿用 SYSDH/SYSDD（镜像 2306），不新造角色。
    /// </summary>
    public static class AssistantAdmin
    {
        /// <summary>3101 会话管理：读 = CanBrowse；归档/取消归档/删除 = CanEdit。</summary>
        public const int SessionAdmin = 3101;
    }
}
