namespace EOS.API.Models;

/// <summary>新增 / 修改一个模块分组的载荷（2315「模块分组」配置面）。</summary>
public sealed record ModuleGroupSaveRequest(string? Description, string? Expression);

/// <summary>分组排序动作载荷：top / up / down / bottom（同一模块内）。</summary>
public sealed record ModuleGroupMoveRequest(string? Action);
