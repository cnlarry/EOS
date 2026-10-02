using System.Data;
using System.Text;
using System.Text.Json;
using EOS.API.Data;
using EOS.API.Features.Assistant.ModelAccess;
using EOS.API.Features.Assistant.Parameters;
using Microsoft.Data.SqlClient;

namespace EOS.API.Features.Assistant.Tools;

/// <summary>审批历史的一行（与 <c>WorkflowEngine.GetHistoryAsync</c> 的输出形状对应）。</summary>
public sealed record RecordApprovalEntry(
    string? Kind,
    string? Step,
    string? StepDesc,
    string? Approver,
    string? State,
    string? Message,
    string? Date);

/// <summary>最近操作的一行：**只有摘要素**，不含字段级明细与明细原文。</summary>
public sealed record RecordActivityEntry(
    string OccurredAt,
    string? Action,
    bool Success,
    string? Actor,
    string? ErrorCode,
    string? Summary);

/// <summary>
/// 单据历史的读取入口。单独立一个窄契约的理由：审批历史来自工作流引擎（具体类，且要自己开连接），
/// 操作历史要查 <c>AUDIT_EVENT</c>——两者都不该让工具直接依赖，否则这个工具没法离线验证它在
/// 权限与防探测上的行为。
/// </summary>
public interface IRecordHistoryGateway
{
    /// <summary>按单取审批时间线（复用工作流引擎的既有实现，主键拼接也在它那里）。</summary>
    Task<IReadOnlyList<RecordApprovalEntry>> GetApprovalHistoryAsync(
        int moduleId, IReadOnlyList<string> pkColumns, IReadOnlyList<string> keys, CancellationToken token);

    /// <summary>按模块 + 主键取最近操作（含失败），只回摘要素。</summary>
    Task<IReadOnlyList<RecordActivityEntry>> GetRecentActivityAsync(
        int moduleId, IReadOnlyList<string> keys, int max, int days, CancellationToken token);
}

/// <summary><see cref="IRecordHistoryGateway"/> 的默认实现：审批历史转发工作流引擎，操作历史查审计表。</summary>
public sealed class WorkflowRecordHistoryGateway(
    WorkflowEngine engine,
    DbConnectionFactory connections) : IRecordHistoryGateway
{
    /// <summary>审计里"通用工作台单据"的资源类型，与诊断侧同一常量值。</summary>
    private const string RecordResourceType = "WORKBENCH_RECORD";

    private static readonly JsonSerializerOptions Projection = new(JsonSerializerDefaults.Web);

    /// <inheritdoc />
    public async Task<IReadOnlyList<RecordApprovalEntry>> GetApprovalHistoryAsync(
        int moduleId, IReadOnlyList<string> pkColumns, IReadOnlyList<string> keys, CancellationToken token)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        var rows = await engine.GetHistoryAsync(connection, moduleId, pkColumns, keys, token);
        if (rows.Count == 0) return [];

        // 引擎返回的是匿名对象（控制器与前端直接消费同一形状）。这里按属性名折进 DTO：
        // 与其为第三个消费方改引擎签名，不如在这一层做一次投影——形状仍只有引擎一处定义。
        // 折不动（形状变了）就返回空，而不是把半截数据当历史讲出去。
        try
        {
            var json = JsonSerializer.Serialize(rows, Projection);
            return JsonSerializer.Deserialize<List<RecordApprovalEntry>>(json, Projection) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RecordActivityEntry>> GetRecentActivityAsync(
        int moduleId, IReadOnlyList<string> keys, int max, int days, CancellationToken token)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        await using var command = new SqlCommand("""
            SELECT TOP (@Max) CONVERT(varchar(19), OCCURRED_AT, 120), ACTION, RESULT,
                   ACTOR_DISPLAY_NAME, ACTOR_USER_ID, ERROR_CODE, SUMMARY
            FROM dbo.AUDIT_EVENT WITH (NOLOCK)
            WHERE M_IDX = @ModuleId AND RESOURCE_TYPE = @ResourceType AND RESOURCE_KEY = @ResourceKey
              AND OCCURRED_AT >= @Since
            ORDER BY OCCURRED_AT DESC, EVENT_ID DESC;
            """, connection);
        command.Parameters.Add("@Max", SqlDbType.Int).Value = max;
        command.Parameters.Add("@ModuleId", SqlDbType.Int).Value = moduleId;
        command.Parameters.Add("@ResourceType", SqlDbType.NVarChar, 40).Value = RecordResourceType;
        command.Parameters.Add("@ResourceKey", SqlDbType.NVarChar, 200).Value = string.Join(',', keys);
        command.Parameters.Add("@Since", SqlDbType.DateTime2).Value = DateTime.UtcNow.AddDays(-days);

        var entries = new List<RecordActivityEntry>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            entries.Add(new RecordActivityEntry(
                reader.IsDBNull(0) ? string.Empty : reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                !reader.IsDBNull(2) && Convert.ToInt32(reader.GetValue(2)) == 1,
                reader.IsDBNull(3) ? (reader.IsDBNull(4) ? null : reader.GetString(4)) : reader.GetString(3),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6)));
        }

        return entries;
    }
}

/// <summary>
/// get_record_history：一张单据的**审批时间线 + 最近操作**（只读）。
///
/// <para>
/// 诊断工具回答"现在为什么办不下去"，这个工具回答"**之前发生过什么**"——问"这张单谁改过""审批到哪了"
/// "上次为什么被拒"时用它。两者的证据来源不同：诊断读的是当前状态与校验判据，这里读的是
/// <c>WF_MYTASK_LOG</c>/<c>WF_APPROVE</c> 与 <c>AUDIT_EVENT</c>。
/// </para>
///
/// <para>
/// **只下发摘要素**：审计只取时间/动作/结果/操作者/错误码/摘要六列，字段级明细
/// （<c>AUDIT_FIELD_CHANGE</c>）与明细原文（<c>DETAIL_JSON</c>）一律不进输出——它们可能含敏感原文，
/// 而摘要已经足够回答用户的问题。
/// </para>
/// </summary>
public sealed class RecordHistoryTool(
    AssistantRecordLocator locator,
    IRecordHistoryGateway history,
    IAssistantRuntimeConfig? runtime = null) : AssistantToolBase, IPageContextTool
{
    public const string ToolName = "get_record_history";

    private PageContext? _page;

    private AssistantToolLimitsOptions Limits => runtime?.Current.Policy.ToolLimits ?? new();

    public override string Name => ToolName;

    public override AssistantToolRisk Risk => AssistantToolRisk.Read;

    public override string Description =>
        "看一张单据的审批时间线与最近操作（谁在什么时候做了什么、有没有失败、失败原因）。"
        + "问「这张单谁改过」「审批到哪一步了」「上次为什么被拒」时使用。"
        + "只给摘要素，不含字段级明细；要看「现在为什么办不下去」请用 diagnose_record。";

    public override string ParametersJson => """
        {
          "type": "object",
          "properties": {
            "module_id": { "type": "integer", "description": "模块 ID；与 module_title 二选一" },
            "module_title": { "type": "string", "description": "模块中文名关键字，如「客户订单」" },
            "_keys": { "type": "array", "items": { "type": "string" }, "description": "可选：单据主键值数组；缺省时按当前页面处境推断" }
          }
        }
        """;

    /// <summary>服务端注入页面处境（单据号 / 选中行）：只作定位，不作权限依据。</summary>
    public void UsePageContext(PageContext page) => _page = page;

    public override async Task<ToolExecutionResult> ExecuteAsync(
        string userId, JsonElement arguments, CancellationToken token)
    {
        var (target, deny) = await locator.LocateAsync(userId, arguments, _page, token);
        if (target is null) return deny ?? ToolExecutionResult.Deny("无法定位该单据，请给出模块与主键。");

        var max = Math.Max(1, Limits.RecordHistoryMax);
        var days = Math.Max(1, Limits.RecordActivityDays);

        var approvals = await history.GetApprovalHistoryAsync(
            target.ModuleId, target.Definition.MasterPkOrder, target.Keys, token);
        var activities = await history.GetRecentActivityAsync(target.ModuleId, target.Keys, max, days, token);

        var output = new StringBuilder();
        output.Append("单据：模块 #").Append(target.ModuleId).Append(' ').Append(target.Definition.Title)
            .Append(" / ").Append(string.Join("-", target.Keys)).AppendLine();

        output.Append("审批历史（").Append(approvals.Count).Append(" 条）：").AppendLine();
        if (approvals.Count == 0)
        {
            // 不编原因：可能是没启用流程，也可能是这张单还没走过审批——两者都不该被说成"审批被卡住"
            output.AppendLine("- 无（该模块可能未启用流程，或这张单还没走过审批）");
        }
        else
        {
            foreach (var entry in approvals.Take(max))
            {
                output.Append("- ").Append(entry.Date).Append(' ')
                    .Append(string.IsNullOrWhiteSpace(entry.StepDesc) ? entry.Step : entry.StepDesc)
                    .Append(" · ").Append(entry.Approver);
                if (!string.IsNullOrWhiteSpace(entry.State))
                {
                    output.Append(" · ").Append(string.Equals(entry.Kind, "confirm", StringComparison.Ordinal) ? "批核确认" : entry.State);
                }

                if (!string.IsNullOrWhiteSpace(entry.Message)) output.Append(" · ").Append(entry.Message);
                output.AppendLine();
            }

            if (approvals.Count > max)
            {
                output.Append("（另有 ").Append(approvals.Count - max).Append(" 条未列出）").AppendLine();
            }
        }

        output.Append("最近操作（近 ").Append(days).Append(" 天，最多 ").Append(max).Append(" 条）：").AppendLine();
        if (activities.Count == 0)
        {
            output.AppendLine("- 无");
        }
        else
        {
            foreach (var entry in activities)
            {
                output.Append("- ").Append(entry.OccurredAt).Append(' ')
                    .Append(entry.Success ? "成功" : "失败").Append(' ')
                    .Append(entry.Action ?? "(未知动作)");
                if (!string.IsNullOrWhiteSpace(entry.Actor)) output.Append(" · ").Append(entry.Actor);
                if (!string.IsNullOrWhiteSpace(entry.ErrorCode)) output.Append(" · ").Append(entry.ErrorCode);
                if (!string.IsNullOrWhiteSpace(entry.Summary)) output.Append(" · ").Append(entry.Summary);
                output.AppendLine();
            }
        }

        output.AppendLine("说明：以上只有摘要素（时间/动作/结果/操作者/摘要），不含字段级明细与明细原文。");
        return ToolExecutionResult.Success(output.ToString());
    }
}
