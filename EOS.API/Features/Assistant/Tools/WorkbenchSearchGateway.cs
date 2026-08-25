using EOS.API.Data;
using EOS.API.Models;

namespace EOS.API.Features.Assistant.Tools;

/// <summary>
/// 助手工具对工作台查询能力的窄接口（ADR-007 §4）。
/// 由 <see cref="DocumentWorkbenchRepository"/> 实现——工具只经此消费列表/定义/记录读取，
/// EXEC_TAG / DATA_FILTER / 模块 FILTER / 字段隐藏等数据范围全部在既有实现内强制生效。
/// </summary>
public interface IWorkbenchSearchGateway
{
    Task<int?> FindGenericModuleIdByTitleAsync(string titleKeyword, CancellationToken token);

    Task<WorkbenchDefinition?> GetDefinitionAsync(
        int moduleId, string userId, string? execTag, bool canViewCost, bool canViewSecrecy,
        IReadOnlySet<string> deniedMasterFields, IReadOnlySet<string> deniedDetailFields,
        CancellationToken token);

    Task<WorkbenchData> GetRowsAsync(
        WorkbenchDefinition definition, bool detail, IReadOnlyDictionary<string, string> keys,
        int page, int pageSize, CancellationToken token, WorkbenchQuery? query = null,
        string? keyword = null, string? sortField = null, string? sortDirection = null,
        int? groupIndex = null, string? groupValue = null, string? dataFilter = null);

    /// <summary>按主键值数组集合精确取行（「导出所选」同一路径，列遵循用户选择列与字段过滤）。</summary>
    Task<IReadOnlyList<Dictionary<string, object?>>> GetExportRowsByKeysAsync(
        WorkbenchDefinition definition, IReadOnlyList<IReadOnlyList<string>> keys, CancellationToken token,
        int? groupIndex = null, string? groupValue = null,
        IReadOnlyList<WorkbenchField>? exportFields = null, string? dataFilter = null);
}

/// <summary>工具执行结果：ContentForModel 是回喂模型的紧凑文本（已限行数/列数，防 token 爆炸）。</summary>
public sealed record ToolExecutionResult(bool Ok, string ContentForModel)
{
    public static ToolExecutionResult Success(string content) => new(true, content);
    public static ToolExecutionResult Deny(string reason) => new(false, reason);
}

/// <summary>单个受控工具的执行契约（ADR-007：模型只能调本注册表白名单，schema 服务端硬编码）。</summary>
public interface IAssistantTool
{
    string Name { get; }

    string Description { get; }

    /// <summary>JSON Schema（OpenAI function parameters 格式）。</summary>
    string ParametersJson { get; }

    /// <summary>
    /// 执行工具。任何失败都以 ToolExecutionResult（Ok=false）回喂模型，不抛出业务异常；
    /// userId 由 ChatService 传入（权限判断与数据范围的主体）。
    /// </summary>
    Task<ToolExecutionResult> ExecuteAsync(string userId, System.Text.Json.JsonElement arguments, CancellationToken token);
}
