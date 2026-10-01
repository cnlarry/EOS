using System.Text;
using System.Text.Json;
using EOS.API.Features.Assistant.Metrics;
using EOS.API.Security;

using EOS.API.Features.Assistant.ModelAccess;
using EOS.API.Features.Assistant.Parameters;

namespace EOS.API.Features.Assistant.Tools;

/// <summary>
/// get_field_relations：已登记的跨表字段关联事实（哪些 *_ID 指向同一实体）。
/// 关联由开发者经迁移登记，AI 只读消费，用于规划跨模块取数（如订单→产品→库存）。
/// fail-closed：每条关联的两端表都必须挂靠模块且用户对两侧均有 CanBrowse，
/// 任一侧不可见即不展示——关联事实不成为绕过模块认知边界的旁路。
/// </summary>
public sealed class GetFieldRelationsTool(
    IFieldRelationRepository relations,
    IMetricRepository moduleLocator,
    IPermissionService permissions,
    IAssistantRuntimeConfig? runtime = null) : AssistantToolBase
{
    public const string ToolName = "get_field_relations";

    private AssistantToolLimitsOptions Limits => runtime?.Current.Policy.ToolLimits ?? new();

    public override string Name => ToolName;
    public override AssistantToolRisk Risk => AssistantToolRisk.Read;
    public override string Description =>
        "查询已登记的跨表字段关联（哪些表的外键列指向同一实体，如订单明细的产品、库存的产品）。"
        + "规划跨模块取数或解释表间关系时使用；输出已按当前用户可见模块过滤。";

    public override string ParametersJson => """
        {
          "type": "object",
          "properties": {
            "keyword": { "type": "string", "description": "可选：表名/列名/说明关键字，如「产品」「PRO_NO」「库存」" }
          }
        }
        """;

    public override async Task<ToolExecutionResult> ExecuteAsync(string userId, JsonElement arguments, CancellationToken token)
    {
        var keyword = arguments.GetStringArg("keyword").Trim();
        var all = await relations.ListAsync(keyword.Length == 0 ? null : keyword, token);

        // 同请求内缓存表→模块反查与权限判定，避免逐条重复查询。
        var visibleByTable = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var output = new StringBuilder();
        var count = 0;
        var hidden = 0;
        foreach (var relation in all)
        {
            if (count >= Limits.FieldRelationsMax)
            {
                hidden++;
                continue;
            }
            if (!await IsSideVisibleAsync(userId, relation.FromTable, visibleByTable, token)
                || !await IsSideVisibleAsync(userId, relation.ToTable, visibleByTable, token))
            {
                hidden++;
                continue;
            }
            count++;
            output.Append("- ").Append(relation.FromTable).Append('.').Append(relation.FromColumn)
                .Append(" = ").Append(relation.ToTable).Append('.').Append(relation.ToColumn);
            if (!string.IsNullOrWhiteSpace(relation.Description))
            {
                output.Append("（").Append(relation.Description).Append('）');
            }
            output.AppendLine();
        }

        if (count == 0)
        {
            return ToolExecutionResult.Success("没有当前用户可见的已登记字段关联（关联两侧模块均需浏览权限）。");
        }
        var header = $"共 {count} 条已登记字段关联（开发者登记事实，用于跨表取数规划，非当前用户数据）：\n";
        if (hidden > 0)
        {
            header += $"（另有 {hidden} 条因模块不可见或超出输出上限未展示）\n";
        }
        return ToolExecutionResult.Success(header + output.ToString().TrimEnd());
    }

    private async Task<bool> IsSideVisibleAsync(
        string userId, string table, Dictionary<string, bool> cache, CancellationToken token)
    {
        if (cache.TryGetValue(table, out var cached))
        {
            return cached;
        }
        var moduleIds = await moduleLocator.FindModuleIdsByTableAsync(table, token);
        var visible = false;
        foreach (var moduleId in moduleIds)
        {
            if ((await permissions.GetAsync(userId, moduleId, token)).CanBrowse)
            {
                visible = true;
                break;
            }
        }
        cache[table] = visible;
        return visible;
    }
}
