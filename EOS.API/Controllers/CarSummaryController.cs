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
    ReportRepository reports,
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
        var rows=await reports.QueryCarSummaryAsync(from,to,m1,m2,token);
        return Ok(new{Columns=rows.Count>0?rows[0].Keys.ToList():new List<string>{"CAR_ID","YM_RPT","RUN_METER","FILLOIL_METER","FILLOIL_TOTAL","FILLOIL_AMOUNT","REPAIR_AMOUNT"},Rows=rows,Count=rows.Count});
    }
}
