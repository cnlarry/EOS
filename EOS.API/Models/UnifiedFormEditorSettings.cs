namespace EOS.API.Models;

/// <summary>
/// 统一表单编辑的模块准入白名单（渐进）。
/// 空列表 = 不启用任何模块；列表值须在 M5 逐模块评审后维护。
/// 首个切片候选（无 SP 纯 CRUD 模块）见  §4。
/// </summary>
public sealed class UnifiedFormEditorSettings
{
    public int[] EnabledModuleIds { get; set; } = [];
}
