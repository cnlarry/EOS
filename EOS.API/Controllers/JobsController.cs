using System.Data;
using System.Diagnostics;
using System.Security.Claims;
using EOS.API.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;

namespace EOS.API.Controllers;

[ApiController]
[Authorize]
[Route("api/jobs")]
public sealed class JobsController(DbConnectionFactory connections) : ControllerBase
{
    /// <summary>
    /// 产品可用库存重计（230901）：受控执行 P_UPDATE_PRO_MRP_ALL（无参白名单 SP）。
    /// 权限门：模块 230901 CanSetup；SP 名为固定白名单常量并经 sys.objects 校验。
    /// </summary>
    [HttpPost("mrp-recalc")]
    public async Task<IActionResult> MrpRecalc(CancellationToken token)
    {
        if(!await CanRunAsync(230901,token))return Forbid();
        const string sproc="P_UPDATE_PRO_MRP_ALL";
        await using var connection=connections.Create();
        await connection.OpenAsync(token);
        const string existsSql="SELECT 1 FROM sys.objects WHERE object_id=OBJECT_ID(@Name) AND type='P';";
        await using var existsCommand=new SqlCommand(existsSql,connection);
        existsCommand.Parameters.Add("@Name",SqlDbType.NVarChar,200).Value=sproc;
        if(await existsCommand.ExecuteScalarAsync(token) is null)
            return BadRequest(new{code="SPROC_NOT_FOUND",message="重算存储过程不存在。"});
        var stopwatch=Stopwatch.StartNew();
        await using var command=new SqlCommand(sproc,connection)
        {
            CommandType=CommandType.StoredProcedure,
            CommandTimeout=600,
        };
        await command.ExecuteNonQueryAsync(token);
        stopwatch.Stop();
        return Ok(new{sproc,elapsedMs=stopwatch.ElapsedMilliseconds});
    }

    /// <summary>
    /// 员工批量发卡（180218）：按员工+卡号列表对 HR_EMPLOYEE_CARD 批量
    /// 更新/新增（已有卡更新生效/截止日期，无卡新增），事务 + 审计列填充，值参数化。
    /// </summary>
    [HttpPost("card-batch")]
    public async Task<IActionResult> CardBatch([FromBody]CardBatchRequest request,CancellationToken token)
    {
        if(!await CanRunAsync(180218,token))return Forbid();
        if(request.Cards.Count==0||request.Cards.Count>2000)return BadRequest(new{code="INVALID_CARDS",message="发卡数量需在 1~2000 之间。"});
        if(request.StartDate==default)return BadRequest(new{code="INVALID_DATE",message="生效日期不能为空。"});
        await using var connection=connections.Create();
        await connection.OpenAsync(token);
        await using var transaction=(SqlTransaction)await connection.BeginTransactionAsync(token);
        var now=DateTime.Now;
        var updated=0;
        var inserted=0;
        foreach(var card in request.Cards)
        {
            var empId=(card.EmpId??"").Trim();
            var cardId=(card.CardId??"").Trim();
            if(empId.Length==0||cardId.Length==0)continue;
            const string existsSql="SELECT TOP 1 1 FROM dbo.HR_EMPLOYEE_CARD WITH (NOLOCK) WHERE EMP_ID=@e AND CARD_ID=@c;";
            await using var existsCommand=new SqlCommand(existsSql,connection,transaction);
            existsCommand.Parameters.Add("@e",SqlDbType.NVarChar,30).Value=empId;
            existsCommand.Parameters.Add("@c",SqlDbType.NVarChar,30).Value=cardId;
            var exists=await existsCommand.ExecuteScalarAsync(token) is not null;
            if(exists)
            {
                await using var update=new SqlCommand(
                    "UPDATE dbo.HR_EMPLOYEE_CARD SET BEGIN_DATE=@bd,END_DATE=@ed,LAST_UPDATE_BY=@u,LAST_UPDATE_DATE=@d WHERE EMP_ID=@e AND CARD_ID=@c;",connection,transaction);
                update.Parameters.Add("@bd",SqlDbType.DateTime).Value=request.StartDate;
                update.Parameters.Add("@ed",SqlDbType.DateTime).Value=(object?)request.EndDate??DBNull.Value;
                update.Parameters.Add("@u",SqlDbType.NVarChar,50).Value=User.Identity?.Name??"SYSTEM";
                update.Parameters.Add("@d",SqlDbType.DateTime).Value=now;
                update.Parameters.Add("@e",SqlDbType.NVarChar,30).Value=empId;
                update.Parameters.Add("@c",SqlDbType.NVarChar,30).Value=cardId;
                await update.ExecuteNonQueryAsync(token);
                updated++;
            }
            else
            {
                await using var insert=new SqlCommand(
                    "INSERT INTO dbo.HR_EMPLOYEE_CARD (EMP_ID,CARD_ID,BEGIN_DATE,END_DATE,CREATE_PERSON,CREATE_DATE,LAST_UPDATE_BY,LAST_UPDATE_DATE) VALUES (@e,@c,@bd,@ed,@u,@d,@u,@d);",connection,transaction);
                insert.Parameters.Add("@e",SqlDbType.NVarChar,30).Value=empId;
                insert.Parameters.Add("@c",SqlDbType.NVarChar,30).Value=cardId;
                insert.Parameters.Add("@bd",SqlDbType.DateTime).Value=request.StartDate;
                insert.Parameters.Add("@ed",SqlDbType.DateTime).Value=(object?)request.EndDate??DBNull.Value;
                insert.Parameters.Add("@u",SqlDbType.NVarChar,50).Value=User.Identity?.Name??"SYSTEM";
                insert.Parameters.Add("@d",SqlDbType.DateTime).Value=now;
                await insert.ExecuteNonQueryAsync(token);
                inserted++;
            }
        }
        await transaction.CommitAsync(token);
        return Ok(new{updated,inserted});
    }

    private async Task<bool> CanRunAsync(int moduleId,CancellationToken token)
    {
        var userId=User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if(userId is null)return false;
        await using var connection=connections.Create();
        await connection.OpenAsync(token);
        const string sql="""
            SELECT TOP 1 1 FROM dbo.SYSDD WITH (NOLOCK)
            WHERE USER_ID=@UserId AND M_IDX=@ModuleId AND LTRIM(RTRIM(ISNULL(EXEC_TAG,'')))<>'A' AND SETUP_TAG=1;
            """;
        await using var command=new SqlCommand(sql,connection);
        command.Parameters.Add("@UserId",SqlDbType.NChar,10).Value=userId.Trim();
        command.Parameters.Add("@ModuleId",SqlDbType.Int).Value=moduleId;
        return await command.ExecuteScalarAsync(token) is not null;
    }
}

public sealed record CardBatchItem(string EmpId, string CardId);
public sealed record CardBatchRequest(DateTime StartDate, DateTime? EndDate, IReadOnlyList<CardBatchItem> Cards);
