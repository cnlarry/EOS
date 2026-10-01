using System.Text;
using System.Text.Json;
using EOS.API.Security;

using EOS.API.Features.Assistant.ModelAccess;
using EOS.API.Features.Assistant.Parameters;

namespace EOS.API.Features.Assistant.Tools;

/// <summary>
/// list_my_capabilities：列出本人有权限的模块与动作位。
///
/// <para>
/// 用途**严格限定为"避免误承诺"**：助手据此不说"我帮你批核"，但**不是授权依据**——
/// 每次真实读取与操作仍走 <c>IPermissionService</c> 重新授权（fail-closed）。
/// </para>
/// <para>
/// 清单不注入常驻提示词（成本、幻觉，以及"有人以模型已知为由削弱工具层 fail-closed"的滑坡），
/// 只作按需查询。
/// </para>
/// </summary>
public sealed class ListMyCapabilitiesTool(
    IWorkbenchSearchGateway gateway,
    IPermissionService permissions,
    IAssistantRuntimeConfig? runtime = null) : AssistantToolBase
{
    public const string ToolName = "list_my_capabilities";

    private AssistantToolLimitsOptions Limits => runtime?.Current.Policy.ToolLimits ?? new();

    public override string Name => ToolName;

    public override AssistantToolRisk Risk => AssistantToolRisk.Read;

    public override string Description =>
        "查询当前用户本人在哪些模块上有哪些操作权（浏览/新增/修改/删除/批核/解批/结案/设置）。"
        + "用于判断某件事自己在界面上做得了还是做不了；清单只是预防误承诺，实际操作仍由服务端重新鉴权。";

    public override string ParametersJson => """
        {
          "type": "object",
          "properties": {
            "keyword": { "type": "string", "description": "可选：模块中文名关键字" }
          }
        }
        """;

    public override async Task<ToolExecutionResult> ExecuteAsync(string userId, JsonElement arguments, CancellationToken token)
    {
        var keyword = arguments.GetStringArg("keyword");
        var candidates = await gateway.ListAssistantModulesAsync(keyword, token);
        var described = new List<(string Title, int ModuleId, string Actions)>();
        foreach (var module in candidates)
        {
            var permission = await permissions.GetAsync(userId, module.Id, token);
            if (!permission.CanBrowse) continue;
            described.Add((module.Title, module.Id, DescribeActions(permission)));
        }

        var shown = described.Take(Limits.ListCapabilitiesMax).ToArray();
        var sb = new StringBuilder($"本人有浏览权限的模块共 {described.Count} 个（括号内是你在该模块上的操作权）：");
        foreach (var item in shown)
        {
            sb.AppendLine().Append("- ").Append(item.ModuleId).Append(' ').Append(item.Title)
                .Append("（").Append(item.Actions).Append('）');
        }

        if (described.Count > shown.Length)
        {
            sb.AppendLine().Append($"（仅显示前 {shown.Length} 个；可用 keyword 缩小范围）");
        }

        if (described.Count == 0)
        {
            sb.Append("（没有可浏览模块；可请管理员核对模块权限）");
        }

        return ToolExecutionResult.Success(sb.ToString());
    }

    /// <summary>动作位人话清单：只说本人**有**的权，避免模型把"没有"当成"没查到"。</summary>
    internal static string DescribeActions(ModulePermission permission)
    {
        var actions = new List<string> { "浏览" };
        if (permission.CanAddNew) actions.Add("新增");
        if (permission.CanEdit) actions.Add("修改");
        if (permission.CanDelete) actions.Add("删除");
        if (permission.CanApprove) actions.Add("批核");
        if (permission.CanDeapprove) actions.Add("解批");
        if (permission.CanEndCase) actions.Add("结案");
        if (permission.CanUnEndCase) actions.Add("取消结案");
        if (permission.CanSetup) actions.Add("配置");
        return string.Join("/", actions);
    }
}
