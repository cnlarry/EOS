namespace EOS.API.Models;

/// <summary>
/// 统一表单的模块准入名单（渐进）。
/// - <see cref="EnabledModuleIds"/>：写名单——可新增/修改/删除（表单不再 404）；
/// - <see cref="ReadOnlyModuleIds"/>：只读名单——只能打开表单**浏览**（双击进浏览态、
///   渲染自定义按钮），新增/修改/删除一律不发路由、端点也一律 404。
/// 两个名单都为空 = 不启用任何模块。
/// </summary>
public sealed class UnifiedFormEditorSettings
{
    /// <summary>写名单：表单可新增/修改（含存盘后业务逻辑的模块须先逐模块评审）。</summary>
    public int[] EnabledModuleIds { get; set; } = [];

    /// <summary>
    /// 只读名单：主表由引擎维护、但界面仍需"看得见、按得动自定义按钮"的模块
    /// （如库存余额 `INV_PRO_DEPOT`、批次账 `INV_BATCH_M`）。写路径必须继续封死——
    /// 通用表单写入会绕过库存移动引擎直接改账，故这些模块只能停在浏览态。
    /// </summary>
    public int[] ReadOnlyModuleIds { get; set; } = [];
}
