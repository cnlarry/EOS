using System.Data;
using System.Text;
using System.Text.Json;
using EOS.API.Data;
using EOS.API.Features.Assistant.Memory;
using Microsoft.Data.SqlClient;

namespace EOS.API.Features.Assistant.Tools;

/// <summary>
/// get_my_digest：会话内主动摘要（本人画像 + 显式记忆 + 待办/在途流程计数）。
/// 全部走既有权限门：待办只含分派给本人的未处理任务，在途只含本人发起。
/// </summary>
public sealed class GetMyDigestTool(
    IAssistantMemoryStore memory,
    DbConnectionFactory? connections = null) : AssistantToolBase
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

        if (connections is not null)
        {
            var (pending, started) = await LoadFlowCountsAsync(userId, token);
            sb.AppendLine().Append($"- 待我审批（{pending} 条）：流程待办中分派给你的未处理任务");
            sb.AppendLine().Append($"- 我发起在途（{started} 条）：你送审且仍在审批中的流程");
        }

        return ToolExecutionResult.Success(sb.ToString());
    }

    private async Task<(long Pending, long Started)> LoadFlowCountsAsync(string userId, CancellationToken token)
    {
        await using var connection = connections!.Create();
        await connection.OpenAsync(token);
        var pending = await CountAsync(connection, null,
            """
            SELECT COUNT_BIG(1) FROM dbo.WF_MYTASK T WITH (NOLOCK)
            WHERE LTRIM(RTRIM(T.APPROVER)) = @UserId AND ISNULL(T.APPROVE_STATE, '') = ''
              AND ISNULL(T.APPROVE_TAG, 0) = 0;
            """, userId, token);
        var started = await CountAsync(connection, null,
            """
            SELECT COUNT_BIG(1) FROM dbo.WF_MONITOR M WITH (NOLOCK)
            WHERE LTRIM(RTRIM(ISNULL(M.START_USER, ''))) = @UserId AND M.WF_STATE = '0';
            """, userId, token);
        return (pending, started);
    }

    private static async Task<long> CountAsync(
        SqlConnection connection, SqlTransaction? transaction, string sql, string userId, CancellationToken token)
    {
        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@UserId", SqlDbType.VarChar, 20).Value = userId;
        return Convert.ToInt64(await command.ExecuteScalarAsync(token));
    }
}
