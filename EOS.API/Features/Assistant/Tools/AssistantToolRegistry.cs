using System.Text.Json;
using EOS.API.Features.Assistant.ModelAccess;
using EOS.API.Features.Assistant.Parameters;
using EOS.API.Security;

namespace EOS.API.Features.Assistant.Tools;

/// <summary>
/// 受控工具注册表：白名单 + 每个工具各自的 JSON Schema，外加**能力面参数**的过滤。
///
/// <para>
/// 关闭是**双向**的：既不发工具声明给模型（<see cref="Definitions"/> 里没有它），
/// 也不接受模型对它的调用（<see cref="TryGet"/> 直接拒绝）。只做前一半的话，
/// 模型"幻觉"出一个已关闭的工具名仍然能被执行——那等于开关是装饰。
/// </para>
///
/// <para>
/// 不注入运行期配置时（单测里直接构造，参数是可选依赖）视为**全部可用**：
/// 能力面是"默认全开 + 按参数关掉"，没有配置就等于没有关过任何东西。
/// </para>
/// </summary>
public sealed class AssistantToolRegistry
{
    // 工具轮数上限（原 MaxToolRounds = 4）已搬进参数目录的 CHAT 域
    // （CHAT_MAX_TOOL_ROUNDS，读取方是 ChatService 的多轮循环与成本预留倍率）：
    // 它是个会花钱的数字，不该钉在代码里。这里不再留第二份。

    private readonly Dictionary<string, IAssistantTool> _tools;
    private readonly AssistantCapabilityOptions _capability;
    private readonly AssistantToolLimitsOptions _limits;

    public AssistantToolRegistry(IEnumerable<IAssistantTool> tools, IAssistantRuntimeConfig? runtime = null)
    {
        _capability = runtime?.Current.Policy.Capability ?? new AssistantCapabilityOptions();
        _limits = runtime?.Current.Policy.ToolLimits ?? new AssistantToolLimitsOptions();
        _tools = tools.ToDictionary(t => t.Name, StringComparer.Ordinal);

        // 发给模型的工具声明（按名排序稳定，便于测试与提示词缓存；风险分级随描述下发）。
        // 被关掉的工具**不在这里**——模型看不到它，就不会去调它。
        // 每个工具后面追加一句按参数生成的上限说明（见 LimitNote）。
        Definitions =
        [
            .. _tools.Values
                .Where(tool => _capability.IsToolEnabled(tool.Name))
                .OrderBy(tool => tool.Name, StringComparer.Ordinal)
                .Select(tool => new ToolDefinition(
                    tool.Name,
                    $"[{tool.Risk.ToString().ToUpperInvariant()}] {tool.Description}{LimitNote(tool.Name)}",
                    tool.ParametersJson)),
        ];
    }

    /// <summary>
    /// 工具输出上限的说明句，**数字来自参数目录**（域 TOOL_LIMIT）。
    ///
    /// <para>
    /// 为什么必须由注册表生成而不是写在工具的 <c>Description</c> 里：这些上限原先就是那么写的
    /// （"返回前 5 行""每行 8 列"）。参数接上执行侧之后，若声明文本还写死数字，就会出现
    /// **系统给模型 10 行、说明里写着 5 行**——模型按 5 行规划，用户看到的却是 10 行。
    /// 现在数字只在参数目录里有一份，工具类不再重复。
    /// </para>
    ///
    /// <para>
    /// 没有上限的工具返回空串；<paramref name="toolName"/> 用各工具的 <c>ToolName</c> 常量，
    /// 改名时这里会编译失败（不会静默漏掉一句）。
    /// </para>
    /// </summary>
    private string LimitNote(string toolName) => toolName switch
    {
        SearchRecordsTool.ToolName =>
            $" 输出上限：最多 {_limits.SearchMaxRows} 行、每行 {_limits.SearchMaxColumns} 列、"
            + $"单值 {_limits.SearchMaxValueLength} 字符。",
        GetRecordDetailTool.ToolName =>
            $" 输出上限：最多 {_limits.DetailMaxColumns} 列、单值 {_limits.DetailMaxValueLength} 字符。",
        DraftRecordTool.ToolName =>
            $" 单个字段值超过 {_limits.DraftMaxValueLength} 字符会被截断并在试算结果里告警。",
        DescribeModuleTool.ToolName =>
            $" 每张表最多列出 {_limits.DescribeMaxFields} 个字段（超出部分只报个数）。",
        ListModulesTool.ToolName =>
            $" 最多返回 {_limits.ListModulesMax} 个模块（超出部分只报个数）。",
        ListMyCapabilitiesTool.ToolName =>
            $" 最多返回 {_limits.ListCapabilitiesMax} 个模块（超出部分只报个数）。",
        GetFieldRelationsTool.ToolName =>
            $" 最多返回 {_limits.FieldRelationsMax} 条关系（超出部分只报个数）。",
        ListReportsTool.ToolName =>
            $" 最多列出 {_limits.ReportListMax} 个报表（超出部分只报个数）。",
        RunReportTool.ToolName =>
            $" 输出上限：最多 {_limits.ReportMaxRows} 行、每行 {_limits.ReportMaxColumns} 列、"
            + $"单值 {_limits.ReportMaxValueLength} 字符。",
        _ => string.Empty,
    };

    /// <summary>发给模型的工具声明。</summary>
    public IReadOnlyList<ToolDefinition> Definitions { get; }

    /// <summary>
    /// 取工具。<b>不认识</b>与<b>被管理员关掉</b>都返回 false，但两者对用户是不同的事——
    /// 调用方要先用 <see cref="IsDisabledByParameter"/> 分辨，再给出各自的说法。
    /// </summary>
    public bool TryGet(string name, out IAssistantTool tool)
    {
        if (_tools.TryGetValue(name, out var found) && _capability.IsToolEnabled(name))
        {
            tool = found;
            return true;
        }

        tool = null!;
        return false;
    }

    /// <summary>这是注册表认得的工具，但已被能力面参数关掉（用于给出准确的拒绝文案）。</summary>
    public bool IsDisabledByParameter(string name) =>
        _tools.ContainsKey(name) && !_capability.IsToolEnabled(name);
}

/// <summary>工具基类：统一抽象成员声明。</summary>
public abstract class AssistantToolBase : IAssistantTool
{
    public abstract string Name { get; }

    public abstract AssistantToolRisk Risk { get; }

    public abstract string Description { get; }

    public abstract string ParametersJson { get; }

    public abstract Task<ToolExecutionResult> ExecuteAsync(string userId, JsonElement arguments, CancellationToken token);
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

    /// <summary>
    /// 工具被能力面参数关掉时的统一说法。**要说清是"被关掉了"**——说成"工具不存在"
    /// 会让用户（与模型）以为是系统故障，而这是管理员的一个配置。
    /// </summary>
    public static ToolExecutionResult DenyDisabledByAdmin(this IAssistantTool tool) =>
        ToolExecutionResult.Deny($"「{tool.Name}」这个能力已被管理员关闭，无法调用。");

    /// <summary>把权限结果展开为工作台定义/查询所需的数据范围参数（EXEC_TAG/DATA_FILTER/禁止字段）。</summary>
    public static (string ExecTag, bool CanViewCost, bool CanViewSecrecy,
        IReadOnlySet<string> DeniedMaster, IReadOnlySet<string> DeniedDetail) Scope(
            this ModulePermission permission)
    {
        var r = permission.Rights;
        return (r.ExecuteTag, r.CanViewCost, r.CanViewSecrecy, r.DeniedMasterFields, r.DeniedDetailFields);
    }
}
