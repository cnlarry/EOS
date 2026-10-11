namespace EOS.API.Models;

/// <summary>新增 / 修改一个模块分组的载荷（230102「模块分组」配置面）。</summary>
public sealed record ModuleGroupSaveRequest(string? Description, string? Expression);

/// <summary>分组排序动作载荷：top / up / down / bottom（同一模块内）。</summary>
public sealed record ModuleGroupMoveRequest(string? Action);

/// <summary>
/// 模块分组**系统级总览**的一行：某个模块的某个分组 + 它属于哪个模块。
///
/// 为什么要有它：配置面若只支持"先选模块"，打开页面就是一片空态——使用者无从知道
/// "系统里到底配了哪些分组"。总览是回答这个问题的那一屏；**编辑仍按模块进行**（选中某行即进那个模块）。
/// `Available` 与按模块视图**同源**（受控编译器能否执行该表达式）。
/// </summary>
public sealed record ModuleGroupOverviewDto(
    int ModuleId,
    string ModuleDesc,
    int GroupId,
    int SortIdx,
    string Description,
    string Expression,
    bool Available,
    string? Error);

/// <summary>
/// 模块分组总览的响应：<paramref name="Targets"/> 是界面左栏的模块清单（含每个模块已配条数），
/// <paramref name="Groups"/> 是全库分组行。
/// </summary>
public sealed record ModuleGroupOverviewViewDto(
    IReadOnlyList<ModuleConfigTargetOptionDto> Targets,
    IReadOnlyList<ModuleGroupOverviewDto> Groups,
    /// <summary>调用者在本配置面上是否有写权。界面据此决定"能编辑 / 只读"。</summary>
    bool CanWrite = false);
