using System.Data;
using System.Diagnostics;
using System.Text.RegularExpressions;
using EOS.API.Data;
using EOS.API.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;

namespace EOS.API.Controllers;

[ApiController]
[Authorize]
[Route("api/jobs")]
public sealed class JobsController(
    DbConnectionFactory connections,
    LegacyRightsRepository rightsRepository,
    CurrentUserContext userContext) : ControllerBase
{
    private static readonly Regex DayColumn = new("^DAY_(0[1-9]|[12][0-9]|3[01])$", RegexOptions.Compiled);

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

    /// <summary>
    /// 考勤生成（180654 模拟生成 / 180659 真实抽取生成的受控基座）：
    /// 按日期范围生成 HRM_DIARY 空白考勤记录（员工 = 指定员工/部门及下级/全部在职），
    /// 并按已批核排班（HRM_PLAN_M.CONFIRM_TAG=1）的班次时间填充 ON1/OUT1。
    /// 旧页面完整计算引擎（调休/放假/请假/出差/签卡/随机模拟滚动计算）未移植，
    /// 登记技术债；本端点全部值参数化、排班日列名由日期白名单生成。
    /// </summary>
    [HttpPost("attendance-generate")]
    public async Task<IActionResult> AttendanceGenerate([FromBody]AttendanceGenerateRequest request,CancellationToken token)
    {
        if(!await CanRunAsync(180654,token)&&!await CanRunAsync(180659,token))return Forbid();
        if(request.StartDate==default||request.EndDate==default||request.EndDate<request.StartDate)
            return BadRequest(new{code="INVALID_RANGE",message="日期范围不合法。"});
        var days=(request.EndDate-request.StartDate).Days+1;
        if(days>62)return BadRequest(new{code="RANGE_TOO_LARGE",message="日期范围不能超过 62 天。"});
        if(request.Mode is not ("simulate" or "extract"))
            return BadRequest(new{code="INVALID_MODE",message="mode 仅支持 simulate 或 extract。"});
        var empIds=(request.EmpIds??[])
            .Select(id=>(id??"").Trim())
            .Where(id=>id.Length>0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(500)
            .ToList();
        if(empIds.Count==0&&string.IsNullOrWhiteSpace(request.DeptId))
            return BadRequest(new{code="NO_TARGET",message="请指定员工或部门。"});
        await using var connection=connections.Create();
        await connection.OpenAsync(token);
        await using var transaction=(SqlTransaction)await connection.BeginTransactionAsync(token);
        try
        {
            await using (var createTemp=new SqlCommand("CREATE TABLE #emps(EMP_ID nchar(10) PRIMARY KEY);",connection,transaction))
                await createTemp.ExecuteNonQueryAsync(token);
            int employeeCount;
            if(empIds.Count>0)
            {
                var existing=await ResolveEmployeeIdsAsync(connection,transaction,empIds,token);
                foreach(var id in existing)
                {
                    await using var insert=new SqlCommand("INSERT INTO #emps(EMP_ID) VALUES(@id);",connection,transaction);
                    insert.Parameters.Add("@id",SqlDbType.NChar,10).Value=id;
                    await insert.ExecuteNonQueryAsync(token);
                }
                employeeCount=existing.Count;
            }
            else
            {
                await using var byDept=new SqlCommand(
                    "INSERT INTO #emps(EMP_ID) SELECT e.EMP_ID FROM dbo.HR_EMPLOYEE e WHERE e.IF_SHOW=1 AND e.DEPT_ID IN (SELECT DEPT_ID FROM dbo.f_get_under_depts(@dept));",connection,transaction);
                byDept.Parameters.Add("@dept",SqlDbType.NVarChar,50).Value=request.DeptId!.Trim();
                employeeCount=await byDept.ExecuteNonQueryAsync(token);
            }
            if(employeeCount==0)
                return BadRequest(new{code="NO_EMPLOYEE",message="没有符合条件的员工。"});
            var inserted=0;
            var filled=0;
            for(var date=request.StartDate;date<=request.EndDate;date=date.AddDays(1))
            {
                await using var skeleton=new SqlCommand(
                    "INSERT INTO dbo.HRM_DIARY(COUNT_DATE,EMP_ID) SELECT @date,EMP_ID FROM #emps e WHERE NOT EXISTS (SELECT 1 FROM dbo.HRM_DIARY d WHERE d.COUNT_DATE=@date AND d.EMP_ID=e.EMP_ID);",connection,transaction);
                skeleton.Parameters.Add("@date",SqlDbType.SmallDateTime).Value=date;
                inserted+=await skeleton.ExecuteNonQueryAsync(token);
                var dayColumn=$"DAY_{date.Day:00}";
                if(DayColumn.IsMatch(dayColumn))
                {
                    await using var fill=new SqlCommand(
                        $"""
                        UPDATE d SET d.ON1=t.IN_TIME1,d.OUT1=t.OUT_TIME1,d.TIMETYPE_ID=t.TIMETYPE_ID
                        FROM dbo.HRM_DIARY d
                        INNER JOIN dbo.HRM_PLAN_D pd ON pd.EMP_ID=d.EMP_ID
                        INNER JOIN dbo.HRM_PLAN_M pm ON pm.PLAN_TYPE=pd.PLAN_TYPE AND pm.PLAN_NO=pd.PLAN_NO AND pm.CONFIRM_TAG=1
                        INNER JOIN dbo.HRM_TIMETYPE t ON t.TIMETYPE_ID=pd.[{dayColumn}]
                        WHERE d.COUNT_DATE=@date AND pm.COUNT_MONTH=CONVERT(varchar(6),@date,112) AND pd.[{dayColumn}] IS NOT NULL AND LTRIM(RTRIM(pd.[{dayColumn}]))<>'';
                        """,connection,transaction);
                    fill.Parameters.Add("@date",SqlDbType.SmallDateTime).Value=date;
                    filled+=await fill.ExecuteNonQueryAsync(token);
                }
            }
            await transaction.CommitAsync(token);
            return Ok(new{Mode=request.Mode,StartDate=request.StartDate,EndDate=request.EndDate,EmployeeCount=employeeCount,Inserted=inserted,Filled=filled});
        }
        catch
        {
            await transaction.RollbackAsync(token);
            throw;
        }
    }

    private static async Task<IReadOnlyList<string>> ResolveEmployeeIdsAsync(
        SqlConnection connection,SqlTransaction transaction,IReadOnlyList<string> empIds,CancellationToken token)
    {
        var result=new List<string>();
        foreach(var id in empIds)
        {
            await using var check=new SqlCommand("SELECT TOP 1 1 FROM dbo.HR_EMPLOYEE WHERE EMP_ID=@id AND IF_SHOW=1;",connection,transaction);
            check.Parameters.Add("@id",SqlDbType.NChar,10).Value=id;
            if(await check.ExecuteScalarAsync(token) is not null)result.Add(id);
        }
        return result;
    }

    private async Task<bool> CanRunAsync(int moduleId,CancellationToken token)
        => (await rightsRepository.GetAsync(userContext.UserId,moduleId,token)).CanSetup;
}

public sealed record CardBatchItem(string EmpId, string CardId);
public sealed record CardBatchRequest(DateTime StartDate, DateTime? EndDate, IReadOnlyList<CardBatchItem> Cards);
public sealed record AttendanceGenerateRequest(DateTime StartDate, DateTime EndDate, string Mode, string? DeptId, IReadOnlyList<string>? EmpIds);
