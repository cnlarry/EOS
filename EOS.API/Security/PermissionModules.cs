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

        /// <summary>3104 机制与工具总览：纯只读，读 = CanBrowse。</summary>
        public const int Mechanism = 3104;

        /// <summary>3103 知识库管理：读 = CanBrowse；删除文档 = CanEdit（入库仍走 2302 那条链路，不在此开放）。</summary>
        public const int Kb = 3103;

        /// <summary>3102 模型与用量：读 = CanBrowse；增改模型 / 切换当前 / 写密钥 = CanSetup。</summary>
        public const int ModelUsage = 3102;

        /// <summary>
        /// 3105 助手设置：读 = CanBrowse；改提示词 / 限额 / 熔断 = CanSetup。
        ///
        /// <para>
        /// 这一段的写权限**比模型管理更要紧**：日上限与熔断阈值一改，全体用户的助手行为立刻变化
        /// （把日上限改小会让所有人被拒、把提示词改坏会让回答不再遵守格式契约），所以它不是一个
        /// "顺手也能改"的地方。
        /// </para>
        /// </summary>
        public const int Settings = 3105;
    }
}
