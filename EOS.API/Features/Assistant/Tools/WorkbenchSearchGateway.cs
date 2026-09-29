using EOS.API.Data;
using EOS.API.Models;

namespace EOS.API.Features.Assistant.Tools;

/// <summary>
/// 助手工具对工作台查询能力的窄接口。
/// 由 <see cref="DocumentWorkbenchRepository"/> 实现——工具只经此消费列表/定义/记录读取，
/// EXEC_TAG / DATA_FILTER / 模块 FILTER / 字段隐藏等数据范围全部在既有实现内强制生效。
/// </summary>
public interface IWorkbenchSearchGateway
{
    Task<IReadOnlyList<SystemKnowledgeModule>> ListAssistantModulesAsync(string? keyword, CancellationToken token);

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

    /// <summary>统一表单定义（mode=new/edit/view；字段集合经权限过滤）。</summary>
    Task<FormDefinition?> GetFormDefinitionAsync(
        WorkbenchDefinition definition, string userId, string mode,
        bool canViewCost, bool canViewSecrecy,
        IReadOnlySet<string> deniedMasterFields, IReadOnlySet<string> deniedDetailFields,
        IReadOnlySet<string> deniedNewMasterFields, IReadOnlySet<string> deniedNewDetailFields,
        IReadOnlySet<string> deniedModiMasterFields, IReadOnlySet<string> deniedModiDetailFields,
        CancellationToken token,
        bool canAddNew = false, bool canEdit = false, bool canDelete = false,
        bool canApprove = false, bool canDeapprove = false,
        bool canEndCase = false, bool canUnEndCase = false,
        bool canFileView = false, bool canFileUpda = false,
        bool canFileEdit = false, bool canFileDele = false, bool canSetup = false);
}

/// <summary>
/// 工具风险分级：Read=只读查询；Draft=产出草稿/建议不触发写入；
/// Write=触发业务写入；AdminWrite=元数据/配置变更（额外要求 CanSetup）。
/// </summary>
public enum AssistantToolRisk
{
    Read = 0,
    Draft = 1,
    Write = 2,
    AdminWrite = 3,
}

/// <summary>工具执行结果：ContentForModel 是回喂模型的紧凑文本；Draft 非空时随 done 事件下发前端渲染确认卡片。</summary>
public sealed record ToolExecutionResult(bool Ok, string ContentForModel, object? Draft = null)
{
    public static ToolExecutionResult Success(string content) => new(true, content);
    public static ToolExecutionResult Deny(string reason) => new(false, reason);
}

/// <summary>单个受控工具的执行契约。</summary>
public interface IAssistantTool
{
    string Name { get; }

    string Description { get; }

    /// <summary>风险分级。</summary>
    AssistantToolRisk Risk { get; }

    /// <summary>JSON Schema（OpenAI function parameters 格式）。</summary>
    string ParametersJson { get; }

    /// <summary>
    /// 执行工具。任何失败都以 ToolExecutionResult（Ok=false）回喂模型，不抛出业务异常；
    /// userId 由 ChatService 传入（权限判断与数据范围的主体）。
    /// </summary>
    Task<ToolExecutionResult> ExecuteAsync(string userId, System.Text.Json.JsonElement arguments, CancellationToken token);
}

/// <summary>
/// 工具可选实现：接收服务端注入的**页面处境**（当前单据号 / 未保存字段），
/// 用于"用户不必报主键"。上报值只作定位，**不作权限依据**（每次读取仍按当前用户重新授权）。
/// 工具实例是 Scoped，注入发生在同一请求内、紧邻执行，不会跨请求残留。
/// </summary>
public interface IPageContextTool
{
    void UsePageContext(PageContext page);
}
