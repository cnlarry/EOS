using System.Text;
using System.Text.Json;
using EOS.API.Security;

namespace EOS.API.Features.Assistant.Tools;

public sealed class ListProceduresTool(
    IAssistantSchemaGateway schema,
    IPermissionService permissions) : AssistantSchemaToolBase(permissions)
{
    public const string ToolName = "list_procedures";
    public override string Name => ToolName;
    public override AssistantToolRisk Risk => AssistantToolRisk.Read;
    public override string Description => "查询 dbo 中真实存在的存储过程及参数签名，不返回过程正文。";
    public override string ParametersJson => """{"type":"object","properties":{"keyword":{"type":"string"}}}""";

    public override async Task<ToolExecutionResult> ExecuteAsync(string userId, JsonElement arguments, CancellationToken token)
    {
        var denied = await RequireSetupAsync(userId, token);
        if (denied is not null) return denied;
        var procedures = await schema.ListProceduresAsync(arguments.GetStringArg("keyword"), token);
        var sb = new StringBuilder($"共 {procedures.Count} 个存储过程：");
        foreach (var procedure in procedures.Take(100)) sb.AppendLine().Append("- ").Append(procedure.Signature);
        return ToolExecutionResult.Success(sb.ToString());
    }
}
