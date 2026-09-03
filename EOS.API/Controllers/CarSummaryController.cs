using System.Data;
using System.Text.RegularExpressions;
using EOS.API.Data;
using EOS.API.Errors;
using EOS.API.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;

namespace EOS.API.Controllers;

[ApiController]
[Authorize]
[Route("api/v1/car-summary")]
public sealed class CarSummaryController(
    DbConnectionFactory connections,
    ModuleRightsRepository rightsRepository,
    CurrentUserContext userContext) : ControllerBase
{
    private const int ModuleId = ModuleIds.CarSummary;
    private static readonly Regex Month = new(@"^\d{4}-(0[1-9]|1[0-2])$", RegexOptions.Compiled);

    /// <summary>
    /// 车辆汇总分析表（199901）：受控执行 RPT_CAR_SUMMARY（服务端常量白名单），
    /// 参数化传入车牌范围与月份范围（yyyy-MM，与日历一致）；返回汇总行。
    /// 权限门：模块 199901 可浏览。
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Query(
        [FromQuery]string? carIdFrom,
        [FromQuery]string? carIdTo,
        [FromQuery]string? monthFrom,
        [FromQuery]string? monthTo,
        CancellationToken token)
    {
        if(!(await rightsRepository.GetAsync(userContext.UserId,ModuleId,token)).CanBrowse) return Forbid();
        var from=(carIdFrom??"").Trim();
        var to=(carIdTo??"").Trim();
        var m1=(monthFrom??"").Trim();
        var m2=(monthTo??"").Trim();
        if(!Month.IsMatch(m1)||!Month.IsMatch(m2))
            return BadRequest(ApiProblem.Create(StatusCodes.Status400BadRequest,"INVALID_MONTH","月份格式应为 yyyy-MM。"));
        if(string.CompareOrdinal(m1,m2)>0)
            return BadRequest(ApiProblem.Create(StatusCodes.Status400BadRequest,"INVALID_RANGE","起始月份不能大于结束月份。"));
        await using var connection=connections.Create();
        await connection.OpenAsync(token);
        await using var command=new SqlCommand("EXEC dbo.RPT_CAR_SUMMARY @car_id_1=@f,@car_id_2=@t,@ym_1=@m1,@ym_2=@m2;",connection);
        command.Parameters.Add("@f",SqlDbType.NChar,40).Value=from;
        command.Parameters.Add("@t",SqlDbType.NChar,40).Value=to;
        command.Parameters.Add("@m1",SqlDbType.Char,7).Value=m1;
        command.Parameters.Add("@m2",SqlDbType.Char,7).Value=m2;
        var rows=await WorkbenchSql.ReadRowsAsync(command,token);
        return Ok(new{Columns=rows.Count>0?rows[0].Keys.ToList():new List<string>{"CAR_ID","YM_RPT","RUN_METER","FILLOIL_METER","FILLOIL_TOTAL","FILLOIL_AMOUNT","REPAIR_AMOUNT"},Rows=rows,Count=rows.Count});
    }
}
