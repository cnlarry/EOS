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
        if(!await CanRunAsync(token))return Forbid();
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

    private async Task<bool> CanRunAsync(CancellationToken token)
    {
        var userId=User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if(userId is null)return false;
        await using var connection=connections.Create();
        await connection.OpenAsync(token);
        const string sql="""
            SELECT TOP 1 1 FROM dbo.SYSDD WITH (NOLOCK)
            WHERE USER_ID=@UserId AND M_IDX=230901 AND LTRIM(RTRIM(ISNULL(EXEC_TAG,'')))<>'A' AND SETUP_TAG=1;
            """;
        await using var command=new SqlCommand(sql,connection);
        command.Parameters.Add("@UserId",SqlDbType.NChar,10).Value=userId.Trim();
        return await command.ExecuteScalarAsync(token) is not null;
    }
}
