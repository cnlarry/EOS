using System.Data;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data;

/// <summary>
/// 统一业务审计写入（ADR-005 §2/§8，阶段 3）：集中承载工作台写路径审计（当前落 SYSDF，
/// 与业务事务同生共死）；阶段 4 切换到 AUDIT_EVENT 时只改本组件，消费方不变。
/// </summary>
public sealed class WorkbenchAuditWriter
{
    public async Task WriteAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        int moduleId,
        string recordKey,
        string type,
        string content,
        string executor,
        CancellationToken token)
    {
        const string sql = """
            INSERT INTO dbo.SYSDF (M_IDX, RECORD_IDX, CONTENT, TYPE, EXEC_BY, EXEC_DATE, CI, OPERFLAG)
            VALUES (@ModuleId, @RecordKey, @Content, @Type, @ExecBy, GETDATE(), NULL, 1);
            """;
        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@ModuleId", SqlDbType.Int).Value = moduleId;
        command.Parameters.Add("@RecordKey", SqlDbType.NVarChar, 100).Value =
            recordKey.Length > 100 ? recordKey[..100] : recordKey;
        command.Parameters.Add("@Content", SqlDbType.NVarChar, 1000).Value =
            content.Length > 1000 ? content[..1000] : content;
        command.Parameters.Add("@Type", SqlDbType.NVarChar, 50).Value = type;
        command.Parameters.Add("@ExecBy", SqlDbType.NVarChar, 50).Value = executor;
        await command.ExecuteNonQueryAsync(token);
    }
}
