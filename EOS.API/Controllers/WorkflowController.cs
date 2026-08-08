using System.Data;
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
    CurrentUserContext userContext) : ControllerBase
{
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
        return Ok(new{Tasks=tasks,EngineEnabled=false,Note="工作流引擎未启用，当前为直接批核模型（待办=各单据未批核数量）。"});
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
