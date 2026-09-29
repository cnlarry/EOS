using System.Text.Json;
using EOS.API.Features.Assistant.ModelAccess;
using EOS.API.Security;

namespace EOS.API.Features.Assistant.Tools;

/// <summary>
/// Controlled tool registry: whitelist + explicit JSON schema per tool.
/// Read-level tools only; write capability (draft/write/admin-write with structured confirmation)
/// is added at the write tier.
/// </summary>
public sealed class AssistantToolRegistry(IEnumerable<IAssistantTool> tools)
{
    public const int MaxToolRounds = 4;

    private readonly Dictionary<string, IAssistantTool> _tools =
        tools.ToDictionary(t => t.Name, StringComparer.Ordinal);

    /// <summary>发给模型的工具声明（按名排序稳定，便于测试与提示词缓存；风险分级随描述下发）。</summary>
    public IReadOnlyList<ToolDefinition> Definitions { get; } =
        [.. tools.OrderBy(t => t.Name, StringComparer.Ordinal).Select(t =>
            new ToolDefinition(
                t.Name,
                $"[{t.Risk.ToString().ToUpperInvariant()}] {t.Description}",
                t.ParametersJson))];

    public bool TryGet(string name, out IAssistantTool tool) => _tools.TryGetValue(name, out tool!);
}

/// <summary>工具基类：统一抽象成员声明。</summary>
public abstract class AssistantToolBase : IAssistantTool
{
    public abstract string Name { get; }

    public abstract AssistantToolRisk Risk { get; }

    public abstract string Description { get; }

    public abstract string ParametersJson { get; }

    public abstract Task<ToolExecutionResult> ExecuteAsync(string userId, System.Text.Json.JsonElement arguments, CancellationToken token);
}

/// <summary>工具共用小函数。</summary>
public static class AssistantToolExtensions
{
    public static int GetIntArg(this JsonElement arguments, string name) =>
        arguments.ValueKind == JsonValueKind.Object
        && arguments.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Number
            ? el.GetInt32()
            : 0;

    public static string GetStringArg(this JsonElement arguments, string name) =>
        arguments.ValueKind == JsonValueKind.Object
        && arguments.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString() ?? string.Empty
            : string.Empty;

    public static ToolExecutionResult DenyBrowse(this IAssistantTool _, string moduleLabel) =>
        ToolExecutionResult.Deny($"用户没有模块「{moduleLabel}」的浏览权限，无法查询该模块数据。");

    /// <summary>
    /// 防探测口径：记录不可见时**不区分**"不存在"与"不在你的数据范围内"
    /// （沿用统一表单 FormAccess 返回 null → NotFound 的既有设计，助手侧不得改口为"无权限"）。
    /// </summary>
    public const string NotFoundMessage = "记录不存在或不在你的数据范围内。";

    public static ToolExecutionResult DenyNotFound(this IAssistantTool _) =>
        ToolExecutionResult.Deny(NotFoundMessage);

    /// <summary>把权限结果展开为工作台定义/查询所需的数据范围参数（EXEC_TAG/DATA_FILTER/禁止字段）。</summary>
    public static (string ExecTag, bool CanViewCost, bool CanViewSecrecy,
        IReadOnlySet<string> DeniedMaster, IReadOnlySet<string> DeniedDetail) Scope(
            this ModulePermission permission)
    {
        var r = permission.Rights;
        return (r.ExecuteTag, r.CanViewCost, r.CanViewSecrecy, r.DeniedMasterFields, r.DeniedDetailFields);
    }
}
