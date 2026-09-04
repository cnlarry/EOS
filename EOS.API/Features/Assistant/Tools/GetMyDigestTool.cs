using System.Text;
using System.Text.Json;
using EOS.API.Features.Assistant.Memory;

namespace EOS.API.Features.Assistant.Tools;

/// <summary>
/// get_my_digest：会话内主动摘要（本人数据：偏好 + 显式记忆）。
/// 只读本人记忆，不触碰业务模块，无越权面。
/// </summary>
public sealed class GetMyDigestTool(IAssistantMemoryStore memory) : AssistantToolBase
{
    public const string ToolName = "get_my_digest";

    public override string Name => ToolName;
    public override AssistantToolRisk Risk => AssistantToolRisk.Read;
    public override string Description =>
        "生成当前用户的个性化摘要（偏好设置 + 已记住的事项）。用户打开助手或询问「我有什么安排/偏好」时使用。";
    public override string ParametersJson => """{"type":"object","properties":{}}""";

    public override async Task<ToolExecutionResult> ExecuteAsync(string userId, JsonElement arguments, CancellationToken token)
    {
        var preferences = await memory.GetPreferencesAsync(userId, token);
        var memories = await memory.ListMemoriesAsync(userId, token);
        var sb = new StringBuilder("我的摘要（仅你本人可见，可能已过期，仅供参考）：");
        sb.AppendLine().Append("- 偏好设置：")
            .Append(string.IsNullOrWhiteSpace(preferences) ? "（未设置）" : preferences.Trim());
        sb.AppendLine().Append($"- 显式记忆（{memories.Count} 条）：");
        if (memories.Count == 0)
        {
            sb.Append("（暂无，可用「记住」功能保存常用查询与偏好）");
        }
        else
        {
            foreach (var item in memories.Take(5))
            {
                sb.AppendLine().Append("- [").Append(item.MemoryType).Append('/')
                    .Append(item.MemoryKey).Append("] ").Append(item.MemoryValue);
            }
        }

        return ToolExecutionResult.Success(sb.ToString());
    }
}
