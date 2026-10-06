using System.Data;
using System.Security.Claims;
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
/// Unified business audit writer: AUDIT_EVENT (+ AUDIT_FIELD_CHANGE for updates)
/// is the only operation log store. SYSDF（旧操作日志表）已随迁移 325 整表退役，
/// 其数据留档于 logs/archive/retire-324/SYSDF-*.csv。
/// Large/sensitive fields store only summary or SHA-256.
/// Read paths (export/print/permission denial/log query trace) use
/// WriteBestEffortAsync (separate connection, failures ignored).
/// </summary>
public sealed class WorkbenchAuditWriter(
    DbConnectionFactory connections,
    IHttpContextAccessor httpContextAccessor,
    WorkbenchDefinitionProvider definitionProvider,
    IOptions<AuditSettings> auditSettings,
    Workbench.AgentWriteContext? agentWriteContext = null)
{
    /// <summary>Record audit event (business transaction scope, no field details).</summary>
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

    /// <summary>Unified audit event (business transaction scope: AUDIT_EVENT [+ AUDIT_FIELD_CHANGE]).</summary>
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
        CancellationToken token,
        string? detailJson = null)
    {
        var context = httpContextAccessor.HttpContext;
        var correlationId = context is null ? "system" : RequestContext.GetCorrelationId(context);
        var (traceId, _) = context is null
            ? ("system", string.Empty)
            : RequestContext.GetTraceIds(context);
        var definitionVersion = moduleId is int moduleIndex ? definitionProvider.GetVersion(moduleIndex) : null;
        var fieldChangesEnabled = auditSettings.Value.FieldChangesEnabled;
        var effectiveFieldChanges = fieldChangesEnabled ? fieldChanges : null;
        const string insertSql = """
            INSERT INTO dbo.AUDIT_EVENT
                (OCCURRED_AT, CORRELATION_ID, TRACE_ID, ACTOR_USER_ID, ACTOR_DISPLAY_NAME, ACTOR_TYPE,
                 CLIENT_TYPE, CLIENT_IP, USER_AGENT, REQUEST_METHOD, REQUEST_PATH, M_IDX,
                 RESOURCE_TYPE, RESOURCE_KEY, ACTION, RESULT, ERROR_CODE, DEFINITION_VERSION, SUMMARY,
                 DETAIL_JSON, CREATED_DATE)
            VALUES (SYSDATETIME(), @CorrelationId, @TraceId, @Actor, @ActorDisplay, @ActorType,
                    @ClientType, @ClientIp, @UserAgent, @RequestMethod, @RequestPath, @ModuleId,
                    @ResourceType, @ResourceKey, @Action, @Result, @ErrorCode, @DefinitionVersion, @Summary,
                    @DetailJson, SYSDATETIME());
            SELECT CAST(SCOPE_IDENTITY() AS bigint);
            """;
        await using var command = new SqlCommand(insertSql, connection, transaction);
        command.Parameters.Add("@CorrelationId", SqlDbType.NVarChar, 64).Value = Truncate(correlationId, 64);
        command.Parameters.Add("@TraceId", SqlDbType.NVarChar, 64).Value =
            (object?)Truncate(traceId, 64) ?? DBNull.Value;
        command.Parameters.Add("@Actor", SqlDbType.NVarChar, 20).Value = Truncate(executor ?? "system", 20);
        command.Parameters.Add("@ActorDisplay", SqlDbType.NVarChar, 50).Value =
            (object?)ResolveActorDisplayName(context) ?? DBNull.Value;
        command.Parameters.Add("@ActorType", SqlDbType.TinyInt).Value = ResolveActorType(executor);
        command.Parameters.Add("@ClientType", SqlDbType.TinyInt).Value = ResolveClientType();
        command.Parameters.Add("@ClientIp", SqlDbType.NVarChar, 45).Value =
            (object?)context?.Connection.RemoteIpAddress?.ToString() ?? DBNull.Value;
        command.Parameters.Add("@UserAgent", SqlDbType.NVarChar, 500).Value =
            context is null || !context.Request.Headers.TryGetValue("User-Agent", out var userAgent)
                ? DBNull.Value
                : (object)Truncate(userAgent.ToString(), 500);
        command.Parameters.Add("@RequestMethod", SqlDbType.NVarChar, 10).Value =
            context is null ? DBNull.Value : (object)Truncate(context.Request.Method, 10);
        command.Parameters.Add("@RequestPath", SqlDbType.NVarChar, 500).Value =
            context is null ? DBNull.Value : (object)Truncate(context.Request.Path.ToString(), 500);
        command.Parameters.Add("@ModuleId", SqlDbType.Int).Value = (object?)moduleId ?? DBNull.Value;
        command.Parameters.Add("@ResourceType", SqlDbType.NVarChar, 50).Value = Truncate(resourceType, 50);
        command.Parameters.Add("@ResourceKey", SqlDbType.NVarChar, 200).Value = Truncate(recordKey ?? string.Empty, 200);
        command.Parameters.Add("@Action", SqlDbType.NVarChar, 50).Value = Truncate(action, 50);
        command.Parameters.Add("@Result", SqlDbType.TinyInt).Value = result;
        command.Parameters.Add("@ErrorCode", SqlDbType.NVarChar, 100).Value =
            context is null ? DBNull.Value : (object?)RequestContext.GetErrorCode(context) ?? DBNull.Value;
        command.Parameters.Add("@DefinitionVersion", SqlDbType.NVarChar, 64).Value = (object?)definitionVersion ?? DBNull.Value;
        command.Parameters.Add("@Summary", SqlDbType.NVarChar, 1000).Value = summary is { Length: > 0 } ? (object)Truncate(summary, 1000) : DBNull.Value;
        var effectiveDetailJson = detailJson ?? (effectiveFieldChanges is { Count: > 0 }
            ? JsonSerializer.Serialize(effectiveFieldChanges)
            : null);
        command.Parameters.Add("@DetailJson", SqlDbType.NVarChar, -1).Value =
            effectiveDetailJson is { Length: > 0 } ? effectiveDetailJson : DBNull.Value;
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
            System.Diagnostics.Debug.WriteLine($"Audit write failed (best-effort, ignored): {ex.Message}");
        }
    }

    private static string? ResolveActorDisplayName(HttpContext? context) =>
        context?.User.FindFirstValue(ClaimTypes.Name);

    /// <summary>
    /// 调用方类型：助手代表的写入一律记为 Agent（进程内动作不经过外部客户端，
    /// 请求头里的 clientId 描述的是**发起对话的人**，不是这次写入的真正执行者）。
    /// </summary>
    private byte ResolveClientType()
    {
        if (agentWriteContext is { IsActive: true })
        {
            return (byte)AuditClientType.Agent;
        }
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

    private byte ResolveActorType(string? executor)
    {
        if (agentWriteContext is { IsActive: true })
        {
            return (byte)AuditActorType.Agent;
        }
        return string.Equals(executor?.Trim(), "SYSTEM", StringComparison.OrdinalIgnoreCase)
            ? (byte)AuditActorType.SystemTask
            : (byte)AuditActorType.User;
    }

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
