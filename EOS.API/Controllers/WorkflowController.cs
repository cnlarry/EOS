using System.Data;
using System.Text.RegularExpressions;
using EOS.API.Data;
using EOS.API.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;

namespace EOS.API.Controllers;

[ApiController]
[Authorize]
[Route("api/workflow")]
public sealed class WorkflowController(
    DbConnectionFactory connections,
    LegacyRightsRepository rightsRepository,
    CurrentUserContext userContext,
    WorkflowEngine workflowEngine) : ControllerBase
{
    private static readonly Regex Identifier = new("^[A-Za-z_][A-Za-z0-9_]{0,127}$", RegexOptions.Compiled);

    public sealed record ApproveTaskRequest(string ApproveState, string? Message = null);

    /// <summary>
    /// 流程任务审批（同意 'Y' / 驳回 'N'）。
    /// </summary>
    [HttpPost("tasks/{myTaskId:long}/approve")]
    public async Task<IActionResult> ApproveTask(long myTaskId, [FromBody] ApproveTaskRequest request, CancellationToken token)
    {
        var state = request.ApproveState.Trim().ToUpperInvariant();
        if (state is not ("Y" or "N"))
            return BadRequest(new { code = "INVALID_APPROVE_STATE", message = "approveState 仅支持 Y（同意）或 N（驳回）。" });
        var result = await workflowEngine.ApproveTaskAsync(myTaskId, userContext.UserId, state[0], request.Message, token);
        if (!result.Success)
            return BadRequest(new { code = result.ErrorCode, message = result.ErrorMessage });
        return Ok(new { Approved = state == "Y", FlowFinished = result.FlowFinished, Message = result.Message });
    }

    /// <summary>
    /// 按单审批历史时间线。
    /// </summary>
    [HttpGet("{moduleId:int}/history")]
    public async Task<IActionResult> History(int moduleId, [FromQuery] string key, CancellationToken token)
    {
        if (!(await rightsRepository.GetAsync(userContext.UserId, moduleId, token)).CanBrowse) return Forbid();
        var keyValues = ParseKey(key);
        if (keyValues is null)
            return BadRequest(new { code = "INVALID_RECORD_KEY", message = "key 必须是主键值数组的 JSON 编码（如 [\"A\",\"B\"]）。" });
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        var pkColumns = await GetPrimaryKeyColumnsAsync(connection, moduleId, token);
        if (pkColumns.Count == 0 || pkColumns.Count != keyValues.Count)
            return BadRequest(new { code = "RECORD_KEY_MISMATCH", message = "主键数量与模块主键不匹配。" });
        var rows = await workflowEngine.GetHistoryAsync(connection, moduleId, pkColumns, keyValues, token);
        return Ok(new { Rows = rows });
    }

    /// <summary>
    /// 待办工作台（2102 我的任务最小可用版）：工作流引擎（WFFORM/WF_MYTASK）未启用
    /// （旧引擎表为空），等价实现为"当前用户可浏览的需批核单据清单"——
    /// 各业务模块未批核（CONFIRM_TAG=0）单据计数 + 工作台直达入口（直接批核模型）。
    /// 模块/表名来自服务端常量白名单，计数 SQL 仅引用白名单表 + CONFIRM_TAG 列。
    /// </summary>
    [HttpGet("my-tasks")]
    public async Task<IActionResult> MyTasks(CancellationToken token)
    {
        if(!(await rightsRepository.GetAsync(userContext.UserId,2102,token)).CanBrowse) return Forbid();
        var tasks=new List<object>();
        await using var connection=connections.Create();
        await connection.OpenAsync(token);
        var flowTasks = await workflowEngine.GetMyFlowTasksAsync(connection, userContext.UserId, token);
        var hasFlow = flowTasks.Count > 0;
        foreach(var (moduleId,table) in PendingModules)
        {
            if(!(await rightsRepository.GetAsync(userContext.UserId,moduleId,token)).CanBrowse) continue;
            string title;
            await using (var titleCommand=new SqlCommand("SELECT LTRIM(RTRIM(M_DESC)) FROM dbo.MODULES WHERE M_IDX=@m;",connection))
            {
                titleCommand.Parameters.Add("@m",SqlDbType.Int).Value=moduleId;
                title=(await titleCommand.ExecuteScalarAsync(token)) as string??string.Empty;
            }
            await using var command=new SqlCommand($"SELECT COUNT_BIG(1) FROM dbo.[{table}] WITH (NOLOCK) WHERE ISNULL(CONFIRM_TAG,0)=0;",connection);
            var pending=Convert.ToInt64(await command.ExecuteScalarAsync(token));
            tasks.Add(new{ModuleId=moduleId,Title=title,Pending=pending});
        }
        return Ok(new{Tasks=tasks,FlowTasks=flowTasks,EngineEnabled=hasFlow,
            Note=hasFlow?"工作流引擎已启用：FlowTasks 为当前用户真实审批待办，Tasks 为直接批核模型下的未批核计数。"
                         :"当前无流程定义（WFFORM 为空），待办=各单据未批核数量（直接批核模型）。"});
    }

    private static IReadOnlyList<string>? ParseKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        try
        {
            var values = System.Text.Json.JsonSerializer.Deserialize<string[]>(key);
            return values is null ? null : (IReadOnlyList<string>)values;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<IReadOnlyList<string>> GetPrimaryKeyColumnsAsync(
        SqlConnection connection, int moduleId, CancellationToken token)
    {
        string? table;
        await using (var tableCommand = new SqlCommand("SELECT LTRIM(RTRIM(ISNULL(MASTER_TABLE,''))) FROM dbo.MODULES WHERE M_IDX=@ModuleId;", connection))
        {
            tableCommand.Parameters.Add("@ModuleId", SqlDbType.Int).Value = moduleId;
            table = await tableCommand.ExecuteScalarAsync(token) as string;
        }
        if (string.IsNullOrWhiteSpace(table) || !Identifier.IsMatch(table)) return [];
        const string sql = """
            SELECT ku.COLUMN_NAME
            FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc
            INNER JOIN INFORMATION_SCHEMA.KEY_COLUMN_USAGE ku
                ON ku.CONSTRAINT_NAME=tc.CONSTRAINT_NAME AND ku.TABLE_NAME=tc.TABLE_NAME
            WHERE tc.CONSTRAINT_TYPE='PRIMARY KEY' AND tc.TABLE_SCHEMA='dbo' AND tc.TABLE_NAME=@Table
            ORDER BY ku.ORDINAL_POSITION;
            """;
        var result = new List<string>();
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@Table", SqlDbType.NVarChar, 100).Value = table;
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token)) result.Add(reader.GetString(0));
        return result;
    }

    /// <summary>需批核单据白名单：业务闭环 17 单据 + 生产/库存核心单据（主表名来自服务端常量）。</summary>
    private static readonly (int ModuleId,string Table)[] PendingModules =
    [
        (1416,"COP_QUOTE_M"),(1405,"COP_ORDER_M"),(1406,"COP_SEND_M"),(1408,"COP_SHIPMENT_M"),
        (170101,"COP_ACCOUNT_M"),(170102,"COP_RECEIPT_M"),(170103,"COP_PREPAY_M"),
        (1604,"PUR_QUOTE_M"),(1615,"PUR_APPLY_M"),(1606,"PUR_PURCHASE_M"),(1607,"PUR_RECEIVE_M"),
        (170201,"PUR_DUE_M"),(170202,"PUR_PAY_M"),(170203,"PUR_PREPAY_M"),
        (1502,"MOC_PRODUCE_M"),(1505,"MOC_PRODUCT_IN_M"),(130103,"INV_OCCUR_IN_M"),(130104,"INV_OCCUR_OUT_M"),
    ];
}
