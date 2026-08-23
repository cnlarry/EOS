using System.Data;
using EOS.API.Data;
using EOS.API.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;

namespace EOS.API.Controllers;

/// <summary>
/// 操作审计查询（旧库 SYSDF 系统日志只读视图）。
/// 权限门：模块 11（基本参数，旧系统日志所在位置）可浏览。
/// </summary>
[ApiController, Authorize, Route("api/v1/audit")]
public sealed class AuditController(
    DbConnectionFactory connections,
    LegacyRightsRepository rightsRepository,
    CurrentUserContext userContext) : ControllerBase
{
    private const int LogModuleId = 11;

    [HttpGet]
    public async Task<IActionResult> Query(
        [FromQuery] int? moduleId,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken token = default)
    {
        if (!(await rightsRepository.GetAsync(userContext.UserId, LogModuleId, token)).CanBrowse)
            return Forbid();
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 10, 200);
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        const string sql = """
            SELECT COUNT_BIG(1) FROM dbo.SYSDF WITH (NOLOCK) WHERE @ModuleId IS NULL OR M_IDX=@ModuleId;
            SELECT LOG_IDX, M_IDX, RECORD_IDX, CONTENT, TYPE, EXEC_BY,
                   CONVERT(varchar(19), EXEC_DATE, 120) AS EXEC_DATE, OPERFLAG
            FROM dbo.SYSDF WITH (NOLOCK)
            WHERE @ModuleId IS NULL OR M_IDX=@ModuleId
            ORDER BY LOG_IDX DESC OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY;
            """;
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@ModuleId", SqlDbType.Int).Value = (object?)moduleId ?? DBNull.Value;
        command.Parameters.Add("@Offset", SqlDbType.Int).Value = (page - 1) * pageSize;
        command.Parameters.Add("@PageSize", SqlDbType.Int).Value = pageSize;
        await using var reader = await command.ExecuteReaderAsync(token);
        await reader.ReadAsync(token);
        var total = Convert.ToInt64(reader.GetInt64(0));
        await reader.NextResultAsync(token);
        var rows = new List<Dictionary<string, object?>>();
        while (await reader.ReadAsync(token))
        {
            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < reader.FieldCount; i++)
                row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            rows.Add(row);
        }
        return Ok(new { Rows = rows, Total = total, Page = page, PageSize = pageSize });
    }
}
