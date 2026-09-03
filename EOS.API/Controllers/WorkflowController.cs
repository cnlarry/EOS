using System.Data;
using EOS.API.Data;
using EOS.API.Errors;
using EOS.API.Models;
using EOS.API.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace EOS.API.Controllers;

[ApiController]
[Authorize]
[Route("api/v1/workflow")]
public sealed class WorkflowController(
    DbConnectionFactory connections,
    ModuleRightsRepository rightsRepository,
    CurrentUserContext userContext,
    WorkflowEngine workflowEngine,
    IOptions<WorkflowSettings> workflowOptions) : ControllerBase
{

    public sealed record ApproveTaskRequest(string ApproveState, string? Message = null, string? JumpNo = null);
    public sealed record WithdrawRequest(int ModuleId, IReadOnlyList<string> Key);

    /// <summary>
    /// 流程任务审批（同意 'Y' / 驳回 'N'）。
    /// </summary>
    [HttpPost("tasks/{myTaskId:long}/approve")]
    public async Task<IActionResult> ApproveTask(long myTaskId, [FromBody] ApproveTaskRequest request, CancellationToken token)
    {
        var state = request.ApproveState.Trim().ToUpperInvariant();
        if (state is not ("Y" or "N"))
            return BadRequest(ApiProblem.Create(StatusCodes.Status400BadRequest, "INVALID_APPROVE_STATE", "approveState 仅支持 Y（同意）或 N（驳回）。"));
        var result = await workflowEngine.ApproveTaskAsync(myTaskId, userContext.UserId, state[0], request.Message, request.JumpNo, token);
        if (!result.Success)
            return BadRequest(ApiProblem.Create(StatusCodes.Status400BadRequest, result.ErrorCode ?? ApiErrorCodes.InvalidArgument, result.ErrorMessage ?? "流程任务审批失败。"));
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
            return BadRequest(ApiProblem.Create(StatusCodes.Status400BadRequest, "INVALID_RECORD_KEY", "key 必须是主键值数组的 JSON 编码（如 [\"A\",\"B\"]）。"));
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        var pkColumns = await GetPrimaryKeyColumnsAsync(connection, moduleId, token);
        if (pkColumns.Count == 0 || pkColumns.Count != keyValues.Count)
            return BadRequest(ApiProblem.Create(StatusCodes.Status400BadRequest, "RECORD_KEY_MISMATCH", "主键数量与模块主键不匹配。"));
        var rows = await workflowEngine.GetHistoryAsync(connection, moduleId, pkColumns, keyValues, token);
        return Ok(new { Rows = rows });
    }

    /// <summary>
    /// 待办工作台（2102 我的任务最小可用版）：工作流引擎（WFFORM/WF_MYTASK）未启用
    /// ，等价实现为"当前用户可浏览的需批核单据清单"——
    /// 各业务模块未批核（CONFIRM_TAG=0）单据计数 + 工作台直达入口（直接批核模型）。
    /// 模块/表名来自服务端常量白名单，计数 SQL 仅引用白名单表 + CONFIRM_TAG 列。
    /// </summary>
    [HttpGet("my-tasks")]
    public async Task<IActionResult> MyTasks(CancellationToken token)
    {
        if(!(await rightsRepository.GetAsync(userContext.UserId,ModuleIds.MyTasks,token)).CanBrowse) return Forbid();
        var tasks=new List<object>();
        await using var connection=connections.Create();
        await connection.OpenAsync(token);
        var flowTasks = await workflowEngine.GetMyFlowTasksAsync(connection, userContext.UserId, token);
        var hasFlow = flowTasks.Count > 0;
        foreach(var (moduleId,table) in ModuleBusinessMap.MasterTables)
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

    /// <summary>
    /// 发起人撤回在途流程（v2.1）：仅发起人可在流程未完成且单据未确认时撤回；
    /// 撤回后单据可编辑，重新批核即重新提交。Key 为主键值数组（模块主键序，
    /// 服务端经 BuildKeyCondition 重新构造条件，不信任前端拼装的 KEY_VALUE 条件串）。
    /// </summary>
    [HttpPost("withdraw")]
    public async Task<IActionResult> Withdraw([FromBody] WithdrawRequest request, CancellationToken token)
    {
        if (request.ModuleId <= 0 || request.Key is null || request.Key.Count == 0)
            return BadRequest(ApiProblem.Create(StatusCodes.Status400BadRequest, "INVALID_WITHDRAW_REQUEST", "moduleId 与 key 不能为空。"));
        if (!(await rightsRepository.GetAsync(userContext.UserId, request.ModuleId, token)).CanBrowse)
            return Forbid();
        var result = await workflowEngine.WithdrawAsync(
            request.ModuleId, request.Key, userContext.UserId, userContext.EmployeeName, token);
        if (result.Status != RecordAccessStatus.Ok)
            return BadRequest(ApiProblem.Create(StatusCodes.Status400BadRequest, result.ErrorCode ?? ApiErrorCodes.InvalidArgument, result.ErrorMessage ?? "流程撤回失败。"));
        return Ok(new { Withdrawn = true, Message = "流程已撤回，单据可修改后重新提交。" });
    }

    /// <summary>当前用户发起的在途流程（v2.1「我发起的」）。</summary>
    [HttpGet("my-started")]
    public async Task<IActionResult> MyStarted(CancellationToken token)
    {
        if (!(await rightsRepository.GetAsync(userContext.UserId, ModuleIds.MyTasks, token)).CanBrowse) return Forbid();
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        var rows = await workflowEngine.GetMyStartedAsync(connection, userContext.UserId, token);
        return Ok(new { Rows = rows });
    }

    /// <summary>
    /// 流程监控列表（模块 2103）：全部流程实例（在途/完成/撤回），支持按状态/模块/关键字过滤。
    /// Overdue 由服务端按 Workflow:OverdueDays 阈值计算（在途超过阈值标记超时）。
    /// </summary>
    [HttpGet("monitor")]
    public async Task<IActionResult> Monitor([FromQuery] string? status = null, [FromQuery] int moduleId = 0, [FromQuery] string? keyword = null, CancellationToken token = default)
    {
        if (!(await rightsRepository.GetAsync(userContext.UserId, ModuleIds.WorkflowMonitor, token)).CanBrowse) return Forbid();
        var state = (status ?? string.Empty).Trim();
        if (state.Length > 0 && state is not ("0" or "1" or "2"))
            return BadRequest(ApiProblem.Create(StatusCodes.Status400BadRequest, "INVALID_FLOW_STATE", "status 仅支持 0（在途）/1（已完成）/2（已撤回）。"));
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        var rows = await workflowEngine.GetMonitorAsync(connection, state, moduleId, keyword,
            workflowOptions.Value.OverdueDays, token);
        return Ok(new { Rows = rows, OverdueDays = workflowOptions.Value.OverdueDays });
    }

    /// <summary>流程监控详情（模块 2103）：实例 + 任务 + 审批日志时间线。</summary>
    [HttpGet("monitor/{wfId:long}")]
    public async Task<IActionResult> MonitorDetail(long wfId, CancellationToken token)
    {
        if (!(await rightsRepository.GetAsync(userContext.UserId, ModuleIds.WorkflowMonitor, token)).CanBrowse) return Forbid();
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        var detail = await workflowEngine.GetMonitorDetailAsync(connection, wfId, token);
        return detail is null
            ? NotFound(ApiProblem.Create(StatusCodes.Status404NotFound, "FLOW_NOT_FOUND", "流程实例不存在。"))
            : Ok(detail);
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
        if (string.IsNullOrWhiteSpace(table) || !WorkbenchSql.Identifier.IsMatch(table)) return [];
        return await WorkbenchSql.GetPrimaryKeyColumnsAsync(connection, null, table, token);
    }

}
