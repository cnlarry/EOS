using System.Text;
using System.Text.Json;
using EOS.API.Models;
using EOS.API.Security;

namespace EOS.API.Features.Assistant.Tools;

public sealed class ListTablesTool(
    IAssistantSchemaGateway schema,
    IPermissionService permissions) : AssistantSchemaToolBase(permissions)
{
    public const string ToolName = "list_tables";
    public override string Name => ToolName;
    public override AssistantToolRisk Risk => AssistantToolRisk.Read;
    public override string Description => "查询 EOS.ERP 中已登记的表元数据，返回表名、中文名、字段数、未纳管列和幽灵字段计数。";
    public override string ParametersJson => """
        {"type":"object","properties":{"keyword":{"type":"string","description":"表名或描述关键字"},"kind":{"type":"string","description":"可选表性质"}}}
        """;

    public override async Task<ToolExecutionResult> ExecuteAsync(string userId, JsonElement arguments, CancellationToken token)
    {
        var denied = await RequireSetupAsync(userId, token);
        if (denied is not null) return denied;
        var keyword = arguments.GetStringArg("keyword");
        var kind = arguments.GetStringArg("kind");
        var tables = await schema.ListTablesAsync(kind, token);
        var matched = tables.Where(t => string.IsNullOrWhiteSpace(keyword)
            || t.TableId.Contains(keyword, StringComparison.OrdinalIgnoreCase)
            || t.Description.Contains(keyword, StringComparison.OrdinalIgnoreCase)).ToArray();
        var shown = matched.Take(100).ToArray();
        var sb = new StringBuilder($"共 {matched.Length} 张匹配表：");
        foreach (var table in shown)
            sb.AppendLine().Append("- ").Append(table.TableId).Append(' ').Append(table.Description)
                .Append(" [").Append(table.Kind ?? "未分类").Append("] ").Append(table.FieldCount).Append(" 字段")
                .Append("（未纳管 ").Append(table.UnmanagedCount).Append("，幽灵 ").Append(table.OrphanCount).Append('）');
        if (matched.Length > shown.Length)
            sb.AppendLine().Append($"（仅显示前 {shown.Length} 张，其余 {matched.Length - shown.Length} 张未展示）");
        return ToolExecutionResult.Success(sb.ToString());
    }
}
