using System.Text;
using System.Text.Json;
using EOS.API.Models;
using EOS.API.Security;

namespace EOS.API.Features.Assistant.Tools;

public sealed class DescribeTableTool(
    IAssistantSchemaGateway schema,
    IPermissionService permissions) : AssistantSchemaToolBase(permissions)
{
    public const string ToolName = "describe_table";
    public override string Name => ToolName;
    public override AssistantToolRisk Risk => AssistantToolRisk.Read;
    public override string Description => "查询一张表的字典字段与未纳管物理列；存在性以 sys.* 为准，中文名以字段字典为准。";
    public override string ParametersJson => """
        {"type":"object","properties":{"table_id":{"type":"string","description":"来自 list_tables 的表名，不要臆造"}},"required":["table_id"]}
        """;

    public override async Task<ToolExecutionResult> ExecuteAsync(string userId, JsonElement arguments, CancellationToken token)
    {
        var denied = await RequireSetupAsync(userId, token);
        if (denied is not null) return denied;
        var tableId = arguments.GetStringArg("table_id").Trim();
        if (tableId.Length == 0 || !System.Text.RegularExpressions.Regex.IsMatch(tableId, "^[A-Za-z_][A-Za-z0-9_]{0,127}$"))
            return ToolExecutionResult.Deny("非法表名。");
        var (table, fields, unmanaged) = await schema.DescribeTableAsync(tableId, token);
        if (table is null) return ToolExecutionResult.Deny("表不存在或未登记。");

        var sb = new StringBuilder($"表 {table.TableId}（{table.Description}）");
        sb.AppendLine().Append("字典字段（").Append(fields.Count).Append("）：");
        foreach (var field in fields.Take(100))
        {
            sb.AppendLine().Append("- ").Append(field.FieldId).Append(' ').Append(field.Description)
                .Append(" [").Append(field.DataType).Append(']');
            if (field.IsPrimaryKey) sb.Append(" PK");
            if (!field.PhysicalExists && !field.IsVirtual) sb.Append(" 幽灵字段");
        }
        sb.AppendLine().Append("未纳管物理列（").Append(unmanaged.Count).Append("，无中文名）：");
        foreach (var field in unmanaged.Take(100)) sb.AppendLine().Append("- ").Append(field.FieldId).Append(" [").Append(field.DataType).Append(']');
        return ToolExecutionResult.Success(sb.ToString());
    }
}
