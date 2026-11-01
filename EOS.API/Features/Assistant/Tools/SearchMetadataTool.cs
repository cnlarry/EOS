using System.Text;
using System.Text.Json;
using EOS.API.Features.Assistant.ModelAccess;
using EOS.API.Features.Assistant.Parameters;

namespace EOS.API.Features.Assistant.Tools;

/// <summary>
/// search_metadata：跨模块 / 表 / 字段 / 自定义动作的关键字检索。
///
/// <para>
/// 它补的是“模型知道要查什么、但不知道在库里叫什么”的那一格：用户说“采购单”“含税金额”“库存冻结”，
/// 模型不必先猜模块名。命中结果按当前用户的模块浏览权过滤，并给出模块号，便于紧接着
/// <c>describe_module</c> / <c>get_form_schema</c>；字段键可直接用于 <c>search_records</c> 的条件。
/// </para>
/// </summary>
public sealed class SearchMetadataTool(
    IMetadataSearchGateway gateway,
    IAssistantRuntimeConfig? runtime = null) : AssistantToolBase
{
    public const string ToolName = "search_metadata";

    private AssistantToolLimitsOptions Limits => runtime?.Current.Policy.ToolLimits ?? new();

    public override string Name => ToolName;

    public override AssistantToolRisk Risk => AssistantToolRisk.Read;

    public override string Description =>
        "按关键字检索可浏览模块、数据表、字段与自定义单据动作，返回模块号/表名/字段键。"
        + "不知道某个业务名词在系统里叫什么时先用本工具；拿到模块号后再 describe_module。";

    public override string ParametersJson => """
        {
          "type": "object",
          "properties": {
            "keyword": { "type": "string", "description": "关键字（模块名/表名/字段名/动作名的一部分）" }
          },
          "required": ["keyword"]
        }
        """;

    public override async Task<ToolExecutionResult> ExecuteAsync(
        string userId, JsonElement arguments, CancellationToken token)
    {
        var keyword = arguments.GetStringArg("keyword")?.Trim();
        if (string.IsNullOrWhiteSpace(keyword))
        {
            return ToolExecutionResult.Deny("缺少 keyword（模块名/表名/字段名/动作名的一部分）。");
        }

        var result = await gateway.SearchAsync(userId, keyword, Limits.MetadataSearchMax, token);
        return ToolExecutionResult.Success(Compress(keyword, result, Limits));
    }

    internal static string Compress(
        string keyword, MetadataSearchResult result, AssistantToolLimitsOptions? limits = null)
    {
        limits ??= new AssistantToolLimitsOptions();
        if (result.Hits.Count == 0)
        {
            return $"没有找到与「{keyword}」匹配的模块/表/字段/动作。可换一个更短的关键字，或先用 list_my_capabilities 看模块清单。";
        }

        var sb = new StringBuilder($"与「{keyword}」匹配 {result.Hits.Count} 条：");
        foreach (var hit in result.Hits)
        {
            sb.AppendLine();
            switch (hit.Kind)
            {
                case "module":
                    sb.Append("- 模块 ").Append(hit.ModuleId).Append(' ').Append(hit.Label);
                    break;
                case "table":
                    sb.Append("- 表 ").Append(hit.Key).Append(' ').Append(hit.Label)
                        .Append("（模块 ").Append(hit.ModuleId).Append(' ').Append(hit.ModuleTitle).Append('）');
                    break;
                case "field":
                    sb.Append("- 字段 ").Append(hit.Key).Append(' ').Append(hit.Label)
                        .Append("（表 ").Append(hit.TableId)
                        .Append("，模块 ").Append(hit.ModuleId).Append(' ').Append(hit.ModuleTitle).Append('）');
                    break;
                default:
                    sb.Append("- 动作 ").Append(hit.Key).Append(' ').Append(hit.Label)
                        .Append("（模块 ").Append(hit.ModuleId).Append(' ').Append(hit.ModuleTitle).Append('）');
                    break;
            }
        }

        if (result.Truncated)
        {
            sb.AppendLine().Append($"（仅显示前 {result.Hits.Count} 条，可加更精确的关键字缩小范围）");
        }

        sb.AppendLine().Append("下一步：模块号可用 describe_module / get_form_schema；字段键可直接用于 search_records 的 conditions。");
        return sb.ToString().TrimEnd();
    }
}
