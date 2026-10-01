using System.Text;
using System.Text.Json;
using EOS.API.Models;
using EOS.API.Security;

using EOS.API.Features.Assistant.ModelAccess;
using EOS.API.Features.Assistant.Parameters;

namespace EOS.API.Features.Assistant.Tools;

/// <summary>Lists generic workbench modules visible to the current user.</summary>
public sealed class ListModulesTool(
    IWorkbenchSearchGateway gateway,
    IPermissionService permissions,
    IAssistantRuntimeConfig? runtime = null) : AssistantToolBase
{
    public const string ToolName = "list_modules";

    private AssistantToolLimitsOptions Limits => runtime?.Current.Policy.ToolLimits ?? new();

    public override string Name => ToolName;
    public override AssistantToolRisk Risk => AssistantToolRisk.Read;
    public override string Description =>
        "查询当前用户有浏览权限的可操作 ERP 模块。可按模块中文名关键字筛选。";
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
        var visible = new List<SystemKnowledgeModule>();
        foreach (var module in candidates)
        {
            var permission = await permissions.GetAsync(userId, module.Id, token);
            if (permission.CanBrowse) visible.Add(module);
        }

        var shown = visible.Take(Limits.ListModulesMax).ToArray();
        var sb = new StringBuilder($"共 {visible.Count} 个可操作模块（仅列出你有浏览权限的）：");
        foreach (var module in shown)
        {
            sb.AppendLine().Append("- ").Append(module.Id).Append(' ').Append(module.Title);
        }

        if (visible.Count > shown.Length)
            sb.AppendLine().Append($"（仅显示前 {shown.Length} 个）");

        return ToolExecutionResult.Success(sb.ToString());
    }
}
