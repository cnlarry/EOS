using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EOS.API.Models;
using EOS.API.Telemetry;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace EOS.API.Data;

/// <summary>
/// 统一业务审计写入（ADR-005 §2/§8，阶段 4）：
/// 业务事务内同写旧 SYSDF（兼容查询来源）与 AUDIT_EVENT（新系统事实源），
/// UPDATE 另写 AUDIT_FIELD_CHANGE（字段级明细）；大字段/敏感字段只存摘要或 SHA-256。
/// 读路径（导出/打印/权限拒绝/日志查询留痕）用 WriteBestEffortAsync（独立连接 + 事务，失败忽略）。
/// </summary>
public sealed class WorkbenchAuditWriter(
    DbConnectionFactory connections,
    IHttpContextAccessor httpContextAccessor,
    WorkbenchDefinitionProvider definitionProvider,
    IOptions<AuditSettings> auditSettings)
{
    /// <summary>兼容入口：SYSDF + AUDIT_EVENT（无字段级明细）。</summary>
    public async Task WriteAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        int moduleId,
        string recordKey,
        string type,
        string content,
        string executor,
        CancellationToken token)
        => await WriteEventAsync(connection, transaction, moduleId, recordKey, type, content, executor,
            "WORKBENCH_RECORD", result: 1, fieldChanges: null, token);

    /// <summary>统一审计事件（业务事务内：SYSDF 兼容 + AUDIT_EVENT [+ AUDIT_FIELD_CHANGE]）。</summary>
    public async Task WriteEventAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        int? moduleId,
        string recordKey,
        string action,
        string summary,
        string executor,
        string resourceType,
        byte result,
        IReadOnlyList<AuditFieldChange>? fieldChanges,
        CancellationToken token)
    {
        await WriteSysdfAsync(connection, transaction, moduleId ?? 0, recordKey, action, summary, executor, token);

        var correlationId = ResolveCorrelationId();
        var definitionVersion = moduleId is int moduleIndex ? definitionProvider.GetVersion(moduleIndex) : null;
        var fieldChangesEnabled = auditSettings.Value.FieldChangesEnabled;
        var effectiveFieldChanges = fieldChangesEnabled ? fieldChanges : null;
        const string insertSql = """
            INSERT INTO dbo.AUDIT_EVENT
                (OCCURRED_AT, CORRELATION_ID, ACTOR_USER_ID, ACTOR_TYPE, CLIENT_TYPE, MODULE_ID,
                 RESOURCE_TYPE, RESOURCE_KEY, ACTION, RESULT, DEFINITION_VERSION, SUMMARY, DETAIL_JSON, CREATED_DATE)
            VALUES (SYSDATETIME(), @CorrelationId, @Actor, @ActorType, @ClientType, @ModuleId,
                    @ResourceType, @ResourceKey, @Action, @Result, @DefinitionVersion, @Summary, @DetailJson, SYSDATETIME());
            SELECT CAST(SCOPE_IDENTITY() AS bigint);
            """;
        await using var command = new SqlCommand(insertSql, connection, transaction);
        command.Parameters.Add("@CorrelationId", SqlDbType.NVarChar, 64).Value = Truncate(correlationId, 64);
        command.Parameters.Add("@Actor", SqlDbType.NVarChar, 20).Value = Truncate(executor ?? "system", 20);
        command.Parameters.Add("@ActorType", SqlDbType.TinyInt).Value = ResolveActorType(executor);
        command.Parameters.Add("@ClientType", SqlDbType.TinyInt).Value = ResolveClientType();
        command.Parameters.Add("@ModuleId", SqlDbType.Int).Value = (object?)moduleId ?? DBNull.Value;
        command.Parameters.Add("@ResourceType", SqlDbType.NVarChar, 50).Value = Truncate(resourceType, 50);
        command.Parameters.Add("@ResourceKey", SqlDbType.NVarChar, 200).Value = Truncate(recordKey ?? string.Empty, 200);
        command.Parameters.Add("@Action", SqlDbType.NVarChar, 50).Value = Truncate(action, 50);
        command.Parameters.Add("@Result", SqlDbType.TinyInt).Value = result;
        command.Parameters.Add("@DefinitionVersion", SqlDbType.NVarChar, 64).Value = (object?)definitionVersion ?? DBNull.Value;
        command.Parameters.Add("@Summary", SqlDbType.NVarChar, 1000).Value = summary is { Length: > 0 } ? (object)Truncate(summary, 1000) : DBNull.Value;
        command.Parameters.Add("@DetailJson", SqlDbType.NVarChar, -1).Value =
            effectiveFieldChanges is { Count: > 0 } ? (object)JsonSerializer.Serialize(effectiveFieldChanges) : DBNull.Value;
        var eventId = Convert.ToInt64(await command.ExecuteScalarAsync(token));

        if (effectiveFieldChanges is { Count: > 0 })
        {
            const string changeSql = """
                INSERT INTO dbo.AUDIT_FIELD_CHANGE (EVENT_ID, FIELD_NAME, OLD_VALUE, NEW_VALUE, VALUE_HASH)
                VALUES (@EventId, @FieldName, @OldValue, @NewValue, @ValueHash);
                """;
            foreach (var change in effectiveFieldChanges)
            {
                await using var changeCommand = new SqlCommand(changeSql, connection, transaction);
                changeCommand.Parameters.Add("@EventId", SqlDbType.BigInt).Value = eventId;
                changeCommand.Parameters.Add("@FieldName", SqlDbType.NVarChar, 100).Value = Truncate(change.FieldName, 100);
                changeCommand.Parameters.Add("@OldValue", SqlDbType.NVarChar, -1).Value = (object?)change.OldValue ?? DBNull.Value;
                changeCommand.Parameters.Add("@NewValue", SqlDbType.NVarChar, -1).Value = (object?)change.NewValue ?? DBNull.Value;
                changeCommand.Parameters.Add("@ValueHash", SqlDbType.Binary, 32).Value = (object?)ChangeHash(change.NewValue) ?? DBNull.Value;
                await changeCommand.ExecuteNonQueryAsync(token);
            }
        }
    }

    /// <summary>best-effort 审计（读路径/管理端写后/权限拒绝/日志查询留痕）：独立连接 + 事务，失败忽略。</summary>
    public async Task WriteBestEffortAsync(
        int? moduleId,
        string resourceKey,
        string action,
        string summary,
        string executor,
        string resourceType,
        byte result,
        IReadOnlyList<AuditFieldChange>? fieldChanges,
        CancellationToken token)
    {
        try
        {
            await using var connection = connections.Create();
            await connection.OpenAsync(token);
            await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
            try
            {
                await WriteEventAsync(connection, transaction, moduleId, resourceKey, action, summary, executor,
                    resourceType, result, fieldChanges, token);
                await transaction.CommitAsync(token);
            }
            catch
            {
                await transaction.RollbackAsync(token);
                throw;
            }
        }
        catch (Exception ex)
        {
            // best-effort：审计失败不得影响业务响应
            System.Diagnostics.Debug.WriteLine($"审计写入失败（best-effort 忽略）：{ex.Message}");
        }
    }

    private static async Task WriteSysdfAsync(
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
        command.Parameters.Add("@RecordKey", SqlDbType.NVarChar, 100).Value = Truncate(recordKey, 100);
        command.Parameters.Add("@Content", SqlDbType.NVarChar, 1000).Value = Truncate(content, 1000);
        command.Parameters.Add("@Type", SqlDbType.NVarChar, 50).Value = type;
        command.Parameters.Add("@ExecBy", SqlDbType.NVarChar, 50).Value = executor;
        await command.ExecuteNonQueryAsync(token);
    }

    private string ResolveCorrelationId()
    {
        if (httpContextAccessor.HttpContext is { } context)
        {
            return RequestContext.GetCorrelationId(context);
        }
        return "system";
    }

    private byte ResolveClientType()
    {
        if (httpContextAccessor.HttpContext is not { } context)
        {
            return (byte)AuditClientType.Api;
        }
        return RequestContext.GetClientId(context) switch
        {
            ClientIds.EosWeb => (byte)AuditClientType.Web,
            ClientIds.EosAgent => (byte)AuditClientType.Agent,
            ClientIds.External => (byte)AuditClientType.Integration,
            _ => (byte)AuditClientType.Api,
        };
    }

    private static byte ResolveActorType(string? executor) =>
        string.Equals(executor?.Trim(), "SYSTEM", StringComparison.OrdinalIgnoreCase)
            ? (byte)AuditActorType.SystemTask
            : (byte)AuditActorType.User;

    private static byte[]? ChangeHash(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }
        return SHA256.HashData(Encoding.UTF8.GetBytes(value));
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length > maxLength ? value[..maxLength] : value;
}
