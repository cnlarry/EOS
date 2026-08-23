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
[Route("api/v1/jobs")]
public sealed class JobsController(
    DbConnectionFactory connections,
    LegacyRightsRepository rightsRepository,
    CurrentUserContext userContext,
    AttendanceCalcService attendanceCalc) : ControllerBase
{
    private static readonly Regex DayColumn = new("^DAY_(0[1-9]|[12][0-9]|3[01])$", RegexOptions.Compiled);
    private static readonly Regex Identifier = new("^[A-Za-z_][A-Za-z0-9_]{0,127}$", RegexOptions.Compiled);

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

    /// <summary>
    /// 考勤计算（阶段 6.2 核心）：对日期范围内 HRM_DIARY 逐员工计算
    /// 排班班次（含调休）、签卡覆盖、加班申请上限、休/节假日分类与工时，幂等重算。
    /// </summary>
    [HttpPost("attendance-calc")]
    public async Task<IActionResult> AttendanceCalc([FromBody]AttendanceGenerateRequest request,CancellationToken token)
    {
        if(!await CanRunAsync(180654,token)&&!await CanRunAsync(180659,token))return Forbid();
        if(request.StartDate==default||request.EndDate==default||request.EndDate<request.StartDate)
            return BadRequest(new{code="INVALID_RANGE",message="日期范围不合法。"});
        var days=(request.EndDate-request.StartDate).Days+1;
        if(days>62)return BadRequest(new{code="RANGE_TOO_LARGE",message="日期范围不能超过 62 天。"});
        var empIds=(request.EmpIds??[])
            .Select(id=>(id??"").Trim())
            .Where(id=>id.Length>0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(500)
            .ToList();
        if(empIds.Count==0&&string.IsNullOrWhiteSpace(request.DeptId))
            return BadRequest(new{code="NO_TARGET",message="请指定员工或部门。"});
        var result=await attendanceCalc.CalculateAsync(request.StartDate,request.EndDate,empIds,request.DeptId,token);
        return Ok(new{StartDate=request.StartDate,EndDate=request.EndDate,EmployeeCount=result.EmployeeCount,
            DiaryRows=result.DiaryRows,Updated=result.Updated,SkippedNoTimeType=result.SkippedNoTimeType,
            SkippedNotActive=result.SkippedNotActive});
    }

    /// <summary>
    /// 依薪资调整考勤（180505 受控移植）：按当月工资表 WAGE_ADD&lt;0（扣款）的员工，
    /// 从节假日加班→休息日加班→平时加班→正常工时依次清空 HRM_DIARY 对应字段，
    /// 调整前后各执行一次 P_HRM_WAGE_CALC（受控白名单 SP）。
    /// HR_SETUP 的薪资调整项目列名须为 HRM_WAGE_D 真实列（白名单校验），全部值参数化。
    /// </summary>
    [HttpPost("attendance-adjust-wage")]
    public async Task<IActionResult> AttendanceAdjustWage([FromBody]AttendanceAdjustWageRequest request,CancellationToken token)
    {
        if(!await CanRunAsync(180505,token))return Forbid();
        var month=(request.Month??"").Trim();
        if(!MonthKey.IsMatch(month))
            return BadRequest(new{code="INVALID_MONTH",message="月份格式应为 yyyyMM。"});
        await using var connection=connections.Create();
        await connection.OpenAsync(token);
        var wageFields=await ReadWageAdjustConfigAsync(connection,token);
        if(wageFields is null)
            return BadRequest(new{code="WAGE_SETUP_MISSING",message="考勤系统设置错误：未设置薪资表调整项目（HR_SETUP.WAGE_*）。"});
        // P_HRM_WAGE_CALC 内部自带 BEGIN TRAN/COMMIT，外层再包事务会"事务计数不匹配"；
        // 工资计算与考勤调整均以自动提交运行（对齐旧页面无外层事务的行为）。
        var calcCount=await RunWageCalcForMonthAsync(connection,null,month,token);
        var adjustments=await LoadWageAdjustmentsAsync(connection,null,month,wageFields,token);
        var affected=await AdjustDiaryByWageAsync(connection,null,adjustments,wageFields,token);
        // TODO: 临时诊断字段，验证后移除
        var recalcCount=await RunWageCalcForMonthAsync(connection,null,month,token);
        return Ok(new{Month=month,WageCalcRuns=calcCount+recalcCount,AdjustedEmployees=adjustments.Count,ClearedDiaryRows=affected});
    }

    private static readonly Regex MonthKey=new("^\\d{6}$",RegexOptions.Compiled);

    private sealed record WageAdjustConfig(string Add,string Work,string Over,string Rest,string Holiday,string WorkTime,string OverTime,string RestTime,string HoliTime);

    private static async Task<WageAdjustConfig?> ReadWageAdjustConfigAsync(SqlConnection connection,CancellationToken token)
    {
        const string sql="SELECT LTRIM(RTRIM(ISNULL(WAGE_ADD,''))),LTRIM(RTRIM(ISNULL(WAGE_WORK,''))),LTRIM(RTRIM(ISNULL(WAGE_OVER,''))),LTRIM(RTRIM(ISNULL(WAGE_REST,''))),LTRIM(RTRIM(ISNULL(WAGE_HOLIDAY,''))),LTRIM(RTRIM(ISNULL(WAGE_WORKTIME,''))),LTRIM(RTRIM(ISNULL(WAGE_OVERTIME,''))),LTRIM(RTRIM(ISNULL(WAGE_RESTTIME,''))),LTRIM(RTRIM(ISNULL(WAGE_HOLITIME,''))) FROM dbo.HR_SETUP;";
        await using var command=new SqlCommand(sql,connection);
        await using var reader=await command.ExecuteReaderAsync(token);
        if(!await reader.ReadAsync(token))return null;
        var config=new WageAdjustConfig(reader.GetString(0),reader.GetString(1),reader.GetString(2),reader.GetString(3),reader.GetString(4),reader.GetString(5),reader.GetString(6),reader.GetString(7),reader.GetString(8));
        await reader.CloseAsync();
        var fields=new[]{config.Add,config.Work,config.Over,config.Rest,config.Holiday,config.WorkTime,config.OverTime,config.RestTime,config.HoliTime};
        if(fields.Any(string.IsNullOrWhiteSpace))return null;
        if(fields.Any(field=>!Identifier.IsMatch(field)))return null;
        // 校验配置列均为 HRM_WAGE_D 真实列（白名单），防止配置注入
        var valid=await GetWageDetailColumnsAsync(connection,token);
        return fields.All(valid.Contains)?config:null;
    }

    private static async Task<HashSet<string>> GetWageDetailColumnsAsync(SqlConnection connection,CancellationToken token)
    {
        const string sql="SELECT c.name FROM sys.columns c JOIN sys.objects o ON c.object_id=o.object_id AND o.type IN ('U','V') JOIN sys.schemas s ON o.schema_id=s.schema_id WHERE s.name=N'dbo' AND o.name=N'HRM_WAGE_D' ORDER BY c.column_id;";
        await using var command=new SqlCommand(sql,connection);
        await using var reader=await command.ExecuteReaderAsync(token);
        var columns=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while(await reader.ReadAsync(token))columns.Add(reader.GetString(0));
        return columns;
    }

    private static async Task<int> RunWageCalcForMonthAsync(SqlConnection connection,SqlTransaction? transaction,string month,CancellationToken token)
    {
        const string listSql="SELECT LTRIM(RTRIM(WAGE_TYPE)),LTRIM(RTRIM(WAGE_NO)) FROM dbo.HRM_WAGE_M WITH (NOLOCK) WHERE LTRIM(RTRIM(COUNT_MONTH))=@month;";
        var types=new List<(string Type,string No)>();
        await using (var listCommand=new SqlCommand(listSql,connection,transaction))
        {
            listCommand.Parameters.Add("@month",SqlDbType.NVarChar,10).Value=month;
            await using var reader=await listCommand.ExecuteReaderAsync(token);
            while(await reader.ReadAsync(token))types.Add((reader.GetString(0),reader.GetString(1)));
        }
        var runs=0;
        foreach(var (type,no) in types)
        {
            await using var calc=new SqlCommand("EXEC dbo.P_HRM_WAGE_CALC @wage_type=@t,@wage_no=@n,@emp_ids=@e,@calc_mode=@m,@if_secrecy=@s;",connection,transaction);
            calc.Parameters.Add("@t",SqlDbType.NVarChar,20).Value=type;
            calc.Parameters.Add("@n",SqlDbType.NVarChar,30).Value=no;
            calc.Parameters.Add("@e",SqlDbType.NVarChar,100).Value="";
            calc.Parameters.Add("@m",SqlDbType.NVarChar,5).Value="A";
            calc.Parameters.Add("@s",SqlDbType.Int).Value=0;
            await calc.ExecuteNonQueryAsync(token);
            runs++;
        }
        return runs;
    }

    private sealed record WageAdjustment(string EmpId,double Add,double Work,double Over,double Rest,double Holiday,double WorkT,double OverT,double RestT,double HoliT);

    private static async Task<IReadOnlyList<WageAdjustment>> LoadWageAdjustmentsAsync(
        SqlConnection connection,SqlTransaction? transaction,string month,WageAdjustConfig c,CancellationToken token)
    {
        var sql=$"""
            SELECT LTRIM(RTRIM(d.EMP_ID)),d.[{c.Add}],d.[{c.Work}],d.[{c.Over}],d.[{c.Rest}],d.[{c.Holiday}],
                   d.[{c.WorkTime}],d.[{c.OverTime}],d.[{c.RestTime}],d.[{c.HoliTime}]
            FROM dbo.HRM_WAGE_D d
            INNER JOIN dbo.HRM_WAGE_M m ON m.WAGE_TYPE=d.WAGE_TYPE AND m.WAGE_NO=d.WAGE_NO
            WHERE LTRIM(RTRIM(m.COUNT_MONTH))=@month AND d.[{c.Add}]<0;
            """;
        await using var command=new SqlCommand(sql,connection,transaction);
        command.Parameters.Add("@month",SqlDbType.NVarChar,10).Value=month;
        await using var reader=await command.ExecuteReaderAsync(token);
        var rows=new List<WageAdjustment>();
        while(await reader.ReadAsync(token))
        {
            rows.Add(new WageAdjustment(
                reader.GetString(0),GetDouble(reader,1),GetDouble(reader,2),GetDouble(reader,3),GetDouble(reader,4),GetDouble(reader,5),
                GetDouble(reader,6),GetDouble(reader,7),GetDouble(reader,8),GetDouble(reader,9)));
        }
        return rows;
    }

    private static double GetDouble(SqlDataReader reader,int ordinal)=>reader.IsDBNull(ordinal)?0:Convert.ToDouble(reader.GetValue(ordinal));

    private static async Task<int> AdjustDiaryByWageAsync(
        SqlConnection connection,SqlTransaction? transaction,IReadOnlyList<WageAdjustment> adjustments,WageAdjustConfig c,CancellationToken token)
    {
        if(adjustments.Count==0)return 0;
        var empIds=string.Join(',',adjustments.Select(a=>$"'{(a.EmpId.Replace("'","''"))}'"));
        var diaryRows=await LoadDiaryRowsAsync(connection,transaction,empIds,c,token);
        var timeTypes=await LoadTimeTypesAsync(connection,transaction,token);
        var affected=0;
        foreach(var wage in adjustments)
        {
            var rows=diaryRows.Where(row=>row.EmpId==wage.EmpId).OrderByDescending(row=>row.CountDate).ToList();
            var dAdd=wage.Add;
            // 节假日加班 → 休息日加班 → 平时加班 → 正常工时
            if(dAdd<10&&wage.Holiday>0&&wage.HoliT>0)
                foreach(var row in rows.Where(r=>r.HolidayOvertime>0).ToList())
                    if(dAdd<10){ dAdd+=wage.Holiday/wage.HoliT*row.HolidayOvertime; ClearDiaryRow(connection,transaction,row,"holiday",timeTypes,token).GetAwaiter().GetResult(); affected++; } else break;
            if(dAdd<10&&wage.Rest>0&&wage.RestT>0)
                foreach(var row in rows.Where(r=>r.RestOvertime>0).ToList())
                    if(dAdd<10){ dAdd+=wage.Rest/wage.RestT*row.RestOvertime; ClearDiaryRow(connection,transaction,row,"rest",timeTypes,token).GetAwaiter().GetResult(); affected++; } else break;
            if(dAdd<10&&wage.Over>0&&wage.OverT>0)
                foreach(var row in rows.Where(r=>r.Overtime>0).ToList())
                    if(dAdd<10){ dAdd+=wage.Over/wage.OverT*row.Overtime; ClearDiaryRow(connection,transaction,row,"over",timeTypes,token).GetAwaiter().GetResult(); affected++; } else break;
            if(dAdd<10&&wage.Work>0&&wage.WorkT>0)
                foreach(var row in rows.Where(r=>r.Worktime>0).ToList())
                    if(dAdd<10){ dAdd+=wage.Work/wage.WorkT*row.Worktime; ClearDiaryRow(connection,transaction,row,"work",timeTypes,token).GetAwaiter().GetResult(); affected++; } else break;
        }
        return affected;
    }

    private sealed record DiaryRow(string EmpId,DateTime CountDate,string? TimeTypeId,double Worktime,double Overtime,double RestOvertime,double HolidayOvertime);

    private static async Task<IReadOnlyList<DiaryRow>> LoadDiaryRowsAsync(
        SqlConnection connection,SqlTransaction? transaction,string empIds,WageAdjustConfig c,CancellationToken token)
    {
        var sql=$"""
            SELECT LTRIM(RTRIM(EMP_ID)),COUNT_DATE,LTRIM(RTRIM(ISNULL(TIMETYPE_ID,''))),ISNULL(WORKTIME,0),ISNULL(OVERTIME,0),ISNULL(REST_OVERTIME,0),ISNULL(HOLIDAY_OVERTIME,0)
            FROM dbo.HRM_DIARY WITH (NOLOCK)
            WHERE EMP_ID IN ({empIds}) AND (ISNULL(WORKTIME,0)>0 OR ISNULL(OVERTIME,0)>0 OR ISNULL(REST_OVERTIME,0)>0 OR ISNULL(HOLIDAY_OVERTIME,0)>0);
            """;
        await using var command=new SqlCommand(sql,connection,transaction);
        await using var reader=await command.ExecuteReaderAsync(token);
        var rows=new List<DiaryRow>();
        while(await reader.ReadAsync(token))
            rows.Add(new DiaryRow(reader.GetString(0),reader.GetDateTime(1),reader.IsDBNull(2)?null:reader.GetString(2),GetDouble(reader,3),GetDouble(reader,4),GetDouble(reader,5),GetDouble(reader,6)));
        return rows;
    }

    private static async Task<Dictionary<string,bool[]>> LoadTimeTypesAsync(SqlConnection connection,SqlTransaction? transaction,CancellationToken token)
    {
        const string sql="SELECT LTRIM(RTRIM(TIMETYPE_ID)),ISNULL(IF_OVERTIME1,0),ISNULL(IF_OVERTIME2,0),ISNULL(IF_OVERTIME3,0),ISNULL(IF_OVERTIME4,0) FROM dbo.HRM_TIMETYPE WITH (NOLOCK);";
        await using var command=new SqlCommand(sql,connection,transaction);
        await using var reader=await command.ExecuteReaderAsync(token);
        var map=new Dictionary<string,bool[]>(StringComparer.OrdinalIgnoreCase);
        while(await reader.ReadAsync(token))
            map[reader.GetString(0)]=new[]{!reader.IsDBNull(1)&&reader.GetBoolean(1),!reader.IsDBNull(2)&&reader.GetBoolean(2),!reader.IsDBNull(3)&&reader.GetBoolean(3),!reader.IsDBNull(4)&&reader.GetBoolean(4)};
        return map;
    }

    private static async Task ClearDiaryRow(
        SqlConnection connection,SqlTransaction? transaction,DiaryRow row,string mode,IReadOnlyDictionary<string,bool[]> timeTypes,CancellationToken token)
    {
        // 对齐旧 AdjustByWage.aspx.cs：holiday/rest/work 清空对应加班/工时字段 + 全部时段字段与汇总；
        // over 仅清 OVERTIME 与按 HRM_TIMETYPE.IF_OVERTIME1-4 标记的时段（ON/OUT/BE_LATE/LEAVE_EARLY）。
        var fullClear=new[]{"TIMETYPE_ID","ON1","ON2","ON3","ON4","OUT1","OUT2","OUT3","OUT4",
            "BE_LATE_FOR1","BE_LATE_FOR2","BE_LATE_FOR3","BE_LATE_FOR4","BE_LATE_FOR",
            "LEAVE_EARLY1","LEAVE_EARLY2","LEAVE_EARLY3","LEAVE_EARLY4","LEAVE_EARLY",
            "LATE_TIMES","LEAVE_EARLY_TIMES","ON_DUTY_TIME","SIGN_IN"};
        var segments=new List<string>();
        if(mode=="holiday")segments.AddRange(fullClear.Prepend("HOLIDAY_OVERTIME"));
        else if(mode=="rest")segments.AddRange(fullClear.Prepend("REST_OVERTIME"));
        else if(mode=="work")segments.AddRange(fullClear.Prepend("WORKTIME"));
        else
        {
            segments.Add("OVERTIME");
            if(!string.IsNullOrWhiteSpace(row.TimeTypeId)&&timeTypes.TryGetValue(row.TimeTypeId,out var flags))
                for(var i=0;i<4;i++)
                    if(flags[i])
                        segments.AddRange(new[]{$"ON{i+1}",$"OUT{i+1}",$"BE_LATE_FOR{i+1}",$"LEAVE_EARLY{i+1}"});
        }
        var sets=string.Join(',',segments.Select(column=>$"[{column}]=NULL"));
        if(mode is "holiday" or "rest" or "work")sets+=",REMARK=''";
        var sql=$"UPDATE dbo.HRM_DIARY SET {sets} WHERE EMP_ID=@e AND COUNT_DATE=@d;";
        await using var command=new SqlCommand(sql,connection,transaction);
        command.Parameters.Add("@e",SqlDbType.NChar,10).Value=row.EmpId;
        command.Parameters.Add("@d",SqlDbType.SmallDateTime).Value=row.CountDate;
        await command.ExecuteNonQueryAsync(token);
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
public sealed record AttendanceAdjustWageRequest(string? Month);
