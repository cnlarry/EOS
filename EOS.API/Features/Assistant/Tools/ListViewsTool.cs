using System.Text;
using System.Text.Json;
using EOS.API.Security;

namespace EOS.API.Features.Assistant.Tools;

public sealed class ListViewsTool(
    IAssistantSchemaGateway schema,
    IPermissionService permissions) : AssistantSchemaToolBase(permissions)
{
    public const string ToolName = "list_views";
    public override string Name => ToolName;
    public override AssistantToolRisk Risk => AssistantToolRisk.Read;
    public override string Description => "查询 dbo 中真实存在的视图对象。SQL 别名不属于物理视图。";
    public override string ParametersJson => """{"type":"object","properties":{"keyword":{"type":"string"}}}""";

    public override async Task<ToolExecutionResult> ExecuteAsync(string userId, JsonElement arguments, CancellationToken token)
    {
        var denied = await RequireSetupAsync(userId, token);
        if (denied is not null) return denied;
        var views = await schema.ListViewsAsync(arguments.GetStringArg("keyword"), token);
        var sb = new StringBuilder($"库中视图（{views.Count}）：");
        foreach (var view in views.Take(100)) sb.AppendLine().Append("- ").Append(view.Name);
        return ToolExecutionResult.Success(sb.ToString());
    }
}
