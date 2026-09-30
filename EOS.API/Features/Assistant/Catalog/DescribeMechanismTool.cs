using System.Text.Json;
using EOS.API.Features.Assistant.Tools;
using EOS.API.Security;

namespace EOS.API.Features.Assistant.Catalog;

/// <summary>
/// describe_mechanism：回答“系统是怎么运作的”。
/// 机制事实一律由 <see cref="SystemCapabilityCatalog"/> 从元数据与代码注册表现算，
/// 不读文档、不落第二份真源；配置域主题（效果链 / 校验规则 / 端点）另行要求配置维护权限。
/// </summary>
public sealed class DescribeMechanismTool(
    SystemCapabilityCatalog catalog,
    IPermissionService permissions) : AssistantToolBase
{
    public const string ToolName = "describe_mechanism";

    /// <summary>配置域主题的权限门：与知识库入库、字段维护同一档（模块 2302 的 CanSetup）。</summary>
    private const int StewardModuleId = 2302;

    public override string Name => ToolName;

    public override AssistantToolRisk Risk => AssistantToolRisk.Read;

    public override string Description =>
        "回答“系统是怎么运作的”：某类机制是什么、怎么配、字段/端点从哪来（统一工作台、统一表单、字段元数据、统一选择器、报表、模块、效果链、校验规则、HTTP 端点）。机制事实取自元数据与代码注册表，不取自文档。";

    public override string ParametersJson => $$"""
        {
          "type": "object",
          "properties": {
            "topic": {
              "type": "string",
              "enum": [{{string.Join(", ", SystemCapabilityCatalog.Topics.Select(topic => $"\"{topic.ToString().ToLowerInvariant()}\""))}}],
              "description": "要解释的机制主题（九选一）：{{SystemCapabilityCatalog.TopicList()}}"
            }
          },
          "required": ["topic"]
        }
        """;

    public override async Task<ToolExecutionResult> ExecuteAsync(
        string userId, JsonElement arguments, CancellationToken token)
    {
        var raw = arguments.GetStringArg("topic").Trim();
        if (raw.Length == 0)
            return ToolExecutionResult.Deny($"请指定机制主题（{SystemCapabilityCatalog.TopicList()}）。");

        if (!SystemCapabilityCatalog.TryParseTopic(raw, out var topic))
            return ToolExecutionResult.Deny($"未知的机制主题「{raw}」，可选主题：{SystemCapabilityCatalog.TopicList()}。");

        // 配置域主题 fail-closed：无配置维护权限一律拒绝，并明说缺哪个动作位。
        if (SystemCapabilityCatalog.RequiresSetup(topic))
        {
            var permission = await permissions.GetAsync(userId, StewardModuleId, token);
            if (!permission.CanSetup)
            {
                return ToolExecutionResult.Deny(
                    $"主题「{topic.ToString().ToLowerInvariant()}」属于配置域（效果链 / 校验规则 / 端点都属于“怎么配”），"
                    + $"需要模块 {StewardModuleId} 的配置维护权限；你没有该权限，无法查询。");
            }
        }

        var explanation = await catalog.DescribeAsync(topic, token);
        return ToolExecutionResult.Success(explanation.Text);
    }
}
