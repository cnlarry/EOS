using System.Text;
using System.Text.Json;
using EOS.API.Features.Assistant.Memory;
using EOS.API.Features.Assistant.Situation;

namespace EOS.API.Features.Assistant.Tools;

/// <summary>
/// get_my_digest：会话内主动摘要（本人画像 + 显式记忆 + 待办/在途流程计数）。
/// 计数与"打开即见"同一口径（见 <see cref="AssistantSituationService.LoadPendingAsync"/>）：
/// 待办只含分派给本人的未处理任务，在途只含本人发起。
/// </summary>
public sealed class GetMyDigestTool(
    IAssistantMemoryStore memory,
    AssistantSituationService? situation = null) : AssistantToolBase
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

        if (situation is not null)
        {
            var pending = await situation.LoadPendingAsync(userId, token);
            sb.AppendLine().Append($"- 待我审批（{pending.MyApproval} 条）：流程待办中分派给你的未处理任务");
            sb.AppendLine().Append($"- 我发起在途（{pending.StartedInFlight} 条）：你送审且仍在审批中的流程");
        }

        return ToolExecutionResult.Success(sb.ToString());
    }
}
