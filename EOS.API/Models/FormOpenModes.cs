namespace EOS.API.Models;

/// <summary>
/// 统一表单的打开方式（`MODULES.FORM_OPEN_MODE`）：模块级呈现开关，随模块定义快照下发给前端，
/// 前端据此决定表单是占当前页签、另开一个工作区页签，还是以固定尺寸的窗体浮在当前页之上。
///
/// 读取侧宽松、写入侧严格：库里未配置（NULL）等同 <see cref="Tab"/>；
/// 非法取值在写入时被拒（见 <see cref="ParseForWrite"/>），读取侧再兜一层，
/// 免得一行脏数据让表单整个打不开。
/// </summary>
public static class FormOpenModes
{
    /// <summary>本页签打开（默认）：表单占据当前标签页。</summary>
    public const string Tab = "TAB";

    /// <summary>新页签打开：在工作区标签栏新开一个标签。</summary>
    public const string NewTab = "NEWTAB";

    /// <summary>弹窗打开：表单以固定尺寸的窗体浮在当前页之上（只有这种方式消费窗体宽高）。</summary>
    public const string Dialog = "DIALOG";

    /// <summary>读取侧规范化：去空白 + 大写；未配置或无法识别一律回落 <see cref="Tab"/>。</summary>
    public static string Normalize(string? value) => value?.Trim().ToUpperInvariant() switch
    {
        NewTab => NewTab,
        Dialog => Dialog,
        _ => Tab,
    };

    /// <summary>是否为弹窗方式（窗体宽高只在这条分支上有意义）。</summary>
    public static bool IsDialog(string? value) => Normalize(value) == Dialog;

    /// <summary>
    /// 写入侧的严格解析：未配置（空）返回 null；能识别返回规范大写；其余抛
    /// <see cref="ArgumentException"/>（由统一异常出口映射为 400 INVALID_ARGUMENT）。
    /// </summary>
    public static string? ParseForWrite(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim().ToUpperInvariant();
        return normalized is Tab or NewTab or Dialog
            ? normalized
            : throw new ArgumentException($"打开方式只能取 {Tab} / {NewTab} / {Dialog}，收到「{value}」。");
    }
}

/// <summary>
/// 弹窗尺寸的取值区间（px）。库内 CHECK 约束（迁移 319 的 `CK_MODULES_FORM_DIALOG_*`）
/// 与本处必须同源：改这里要同步改那条迁移的区间，否则会出现"端点放行、库里拒绝"的 500。
/// </summary>
public static class FormDialogLimits
{
    public const int MinWidth = 240;
    public const int MaxWidth = 4000;
    public const int MinHeight = 200;
    public const int MaxHeight = 4000;
}
