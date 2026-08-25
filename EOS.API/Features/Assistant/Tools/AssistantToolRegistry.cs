using System.Text.Json;
using EOS.API.Features.Assistant.ModelAccess;
using EOS.API.Security;

namespace EOS.API.Features.Assistant.Tools;

/// <summary>
/// 受控工具注册表（ADR-007 §4 ToolRegistry）：白名单 + schema 显式声明。
/// M3 只含 read 级工具；写能力（draft/write/admin-write 分级与结构化确认）按 ADR-007 §6 在 M4 接入。
/// </summary>
public sealed class AssistantToolRegistry(IEnumerable<IAssistantTool> tools)
{
    public const int MaxToolRounds = 4;

    private readonly Dictionary<string, IAssistantTool> _tools =
        tools.ToDictionary(t => t.Name, StringComparer.Ordinal);

    /// <summary>发给模型的工具声明（按名排序稳定，便于测试与提示词缓存）。</summary>
    public IReadOnlyList<ToolDefinition> Definitions { get; } =
        [.. tools.OrderBy(t => t.Name, StringComparer.Ordinal).Select(t =>
            new ToolDefinition(t.Name, t.Description, t.ParametersJson))];

    public bool TryGet(string name, out IAssistantTool tool) => _tools.TryGetValue(name, out tool!);
}

/// <summary>工具共用小函数。</summary>
public static class AssistantToolExtensions
{
    public static int GetIntArg(this JsonElement arguments, string name) =>
        arguments.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Number
            ? el.GetInt32()
            : 0;

    public static string GetStringArg(this JsonElement arguments, string name) =>
        arguments.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString() ?? string.Empty
            : string.Empty;

    public static ToolExecutionResult DenyBrowse(this IAssistantTool _, string moduleLabel) =>
        ToolExecutionResult.Deny($"用户没有模块「{moduleLabel}」的浏览权限，无法查询该模块数据。");

    /// <summary>把权限结果展开为工作台定义/查询所需的数据范围参数（EXEC_TAG/DATA_FILTER/禁止字段）。</summary>
    public static (string ExecTag, bool CanViewCost, bool CanViewSecrecy,
        IReadOnlySet<string> DeniedMaster, IReadOnlySet<string> DeniedDetail) Scope(
            this ModulePermission permission)
    {
        var r = permission.Rights;
        return (r.ExecuteTag, r.CanViewCost, r.CanViewSecrecy, r.DeniedMasterFields, r.DeniedDetailFields);
    }
}
