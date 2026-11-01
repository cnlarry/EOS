namespace EOS.API.Security;

/// <summary>
/// 权限资源模块号集中定义，消除控制器中的魔法数字。
/// </summary>
public static class PermissionModules
{
    /// <summary>系统管理（用户权限设定 2306）：Workbench Definition 快照发布/回填等管理动作的门。</summary>
    public const int SystemManagement = 2306;

    /// <summary>
    /// 报表排序汇总设置（2201）：报表定义与排序/分组方案的维护面（定制页 `/admin/report-setup`）。
    ///
    /// <para>
    /// 读 = `CanBrowse`，写 = `CanSetup`。它是**配置面**而不是单据，故不进统一表单：
    /// 写路径带 5 张引用表的级联删除与版式包/开单公司/编号正则/字段白名单四类校验，
    /// 通用管线一样都没有（见 `docs/guide/50-统一报表.md`）。
    /// </para>
    /// </summary>
    public const int ReportAdmin = 2201;

    /// <summary>
    /// 指标口径（2317）：`REPORT_METRIC` 维护面的门。读 = CanBrowse，写 = CanSetup。
    ///
    /// <para>
    /// 单独一个模块而不是挂 2201（报表设置）或 2302（元数据管理）：同一条口径被报表与助手共同消费，
    /// 绑在别的能力上会让"谁能改口径"取决于"谁能改报表"。
    /// </para>
    /// </summary>
    public const int MetricCatalog = 2317;

    /// <summary>
    /// 工作助手管理（菜单组 31）下的模块号。见 ADR-030：新开根组而不是挂 23，
    /// 权限门沿用 SYS_GROUP_MODULE_PERMISSION/SYS_USER_MODULE_PERMISSION（镜像 2306），不新造角色。
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

        /// <summary>
        /// 3106 评估用例：助手评估"尺子"的维护面。走统一工作台（`M_URL=/workbench` + 主表
        /// `ASSISTANT_EVAL_CASE`），因此**权限即模块权限**：能否看见、能否改，全由管理员在
        /// 2306「用户权限设定」按模块/字段分配，本模块不预置任何授权。
        ///
        /// <para>
        /// 这里只登记模块号，供需要按模块门控的代码引用；维护动作本身复用统一表单的写管线
        /// （权限、字段级禁止、审计一处不少），不新造端点。
        /// </para>
        /// </summary>
        public const int EvalCase = 3106;

        /// <summary>
        /// 3107 评估运行：把库内用例跑一轮确定性检查（不调用模型）。读 = `CanBrowse`（看历史与结果），
        /// 跑 = `CanSetup`。
        ///
        /// <para>
        /// 跑一轮为什么要 `CanSetup` 而不是 `CanBrowse`：跑出来的记录是"这批尺子今天是否还准"的
        /// 结论，看板会按它决定下一步补哪个入口——签这个字的人和随手点开看一眼的人不是同一个角色
        /// （ADR-046 §7「客户侧无 CanSetup 责任人则不启用自助评估」）。
        /// </para>
        /// </summary>
        public const int EvalRun = 3107;
    }
}
