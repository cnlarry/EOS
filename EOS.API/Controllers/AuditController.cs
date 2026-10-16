using System.Data;
using EOS.API.Data;
using EOS.API.Data.Query;
using EOS.API.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;

namespace EOS.API.Controllers;

/// <summary>
/// Operation audit query over AUDIT_EVENT (v2, the only operation log store).
/// SYSDF（旧操作日志表）已随迁移 325 整表退役，不再存在于库中。
/// Permission gate: module 2316 (system management → operation audit) CanBrowse.
/// 2316 与 2313 日志管理是两层：那里读磁盘运行日志文件，这里读库表 AUDIT_EVENT，
/// 故各自一个模块号——共号会把"能看日志 = 能看审计"焊死，审计无法单独授权。
/// </summary>
[ApiController, Authorize, Route("api/v1/audit")]
public sealed class AuditController(
    DbConnectionFactory connections,
    ModuleRightsRepository rightsRepository,
    CurrentUserContext userContext) : ControllerBase
{
    private const int AuditModuleId = 2316;

    [HttpGet]
    public async Task<IActionResult> Query(
        [FromQuery] int? moduleId,
        [FromQuery] string? action,
        [FromQuery] string? actor,
        [FromQuery] byte? result,
        [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken token = default)
    {
        if (!(await rightsRepository.GetAsync(userContext.UserId, AuditModuleId, token)).CanBrowse)
            return Forbid();
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 10, 200);
        const string filter = """
            WHERE (@ModuleId IS NULL OR M_IDX = @ModuleId)
              AND (@Action IS NULL OR ACTION = @Action)
              AND (@Actor IS NULL OR ACTOR_USER_ID = @Actor)
              AND (@Result IS NULL OR RESULT = @Result)
              AND (@From IS NULL OR OCCURRED_AT >= @From)
              AND (@To IS NULL OR OCCURRED_AT <= @To)
            """;
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        const string sql = """
            SELECT COUNT_BIG(1) FROM dbo.AUDIT_EVENT WITH (NOLOCK)
            """ + filter + """
            ;
            SELECT EVENT_ID, CONVERT(varchar(23), OCCURRED_AT, 121) AS OCCURRED_AT,
                   CORRELATION_ID, TRACE_ID, ACTOR_USER_ID, ACTOR_DISPLAY_NAME, ACTOR_TYPE,
                   CLIENT_TYPE, CLIENT_IP, M_IDX AS MODULE_ID, RESOURCE_TYPE, RESOURCE_KEY, ACTION,
                   RESULT, ERROR_CODE, DEFINITION_VERSION, SUMMARY
            FROM dbo.AUDIT_EVENT WITH (NOLOCK)
            """ + filter + """
            ORDER BY EVENT_ID DESC OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY;
            """;
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@ModuleId", SqlDbType.Int).Value = (object?)moduleId ?? DBNull.Value;
        command.Parameters.Add("@Action", SqlDbType.NVarChar, 50).Value =
            string.IsNullOrWhiteSpace(action) ? DBNull.Value : (object)action.Trim();
        command.Parameters.Add("@Actor", SqlDbType.NVarChar, 20).Value =
            string.IsNullOrWhiteSpace(actor) ? DBNull.Value : (object)actor.Trim();
        command.Parameters.Add("@Result", SqlDbType.TinyInt).Value = (object?)result ?? DBNull.Value;
        command.Parameters.Add("@From", SqlDbType.DateTime2).Value =
            (object?)from?.UtcDateTime ?? DBNull.Value;
        command.Parameters.Add("@To", SqlDbType.DateTime2).Value =
            (object?)to?.UtcDateTime ?? DBNull.Value;
        command.Parameters.Add("@Offset", SqlDbType.Int).Value = PageWindow.ResolveOffset(page, pageSize);
        command.Parameters.Add("@PageSize", SqlDbType.Int).Value = pageSize;
        await using var reader = await command.ExecuteReaderAsync(token);
        await reader.ReadAsync(token);
        var total = Convert.ToInt64(reader.GetInt64(0));
        await reader.NextResultAsync(token);
        var rows = await WorkbenchSql.ReadRowsAsync(reader, token);
        // 信封字段用小写驼峰：System.Text.Json 默认把 C# 属性名转 camelCase，
        // 前端按大写读会静默拿到 undefined（页面显示 0 条）。行内键保持库列名大写。
        return Ok(new { rows, total, page, pageSize });
    }
}
