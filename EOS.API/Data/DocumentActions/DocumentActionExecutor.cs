using System.Data;
using System.Text.Json;
using EOS.API.Data.Effects;
using EOS.API.Models;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data.DocumentActions;

/// <summary>Execution outcome of one document action request, mapped to HTTP by the controller.</summary>
public enum DocumentActionStatus
{
    Ok,
    NotFound,
    OutOfScope,
    FilterUnsupported,
    ValidationFailed,
    Failed,
    KeyMismatch,
}

public sealed record DocumentActionExecution(
    DocumentActionStatus Status,
    DocumentActionResult? Result = null,
    bool RequiresConfirmation = false,
    string? ErrorCode = null,
    string? ErrorMessage = null,
    IReadOnlyList<FieldError>? FieldErrors = null);

/// <summary>
/// Runs one user-triggered document action. The endpoint stays a thin wrapper; everything that must
/// hold for every action lives here, once:
///
/// · range filter first (fail-closed) — authorization answers "may you press this button", the range
///   predicate answers "is this document yours", and both must hold;
/// · idempotency — the user-triggered button is the one most likely to be double-clicked, so the
///   request must carry a key and a repeated key returns the recorded outcome instead of running again;
/// · one transaction — the action either lands completely or not at all;
/// · one AUDIT_EVENT per action (ACTION = the action key), written with the payload that was used.
///
/// Probe semantics (CONFIRM_TAG=1 with confirm=false): the handler runs inside the transaction and the
/// transaction is rolled back, so the message describes what would happen and nothing is written. The
/// idempotency key is not claimed in that mode — claiming it would block the real execution.
/// </summary>
public sealed class DocumentActionExecutor(
    DbConnectionFactory connections,
    DocumentActionRegistry registry,
    WorkbenchScopeFilter scopeFilter,
    WorkbenchIdempotency idempotency,
    EffectPipeline effectPipeline,
    WorkbenchAuditWriter auditWriter,
    ILogger<DocumentActionExecutor> logger)
{
    /// <summary>Idempotency bucket for user-triggered actions (WORKBENCH_IDEMPOTENCY.ACTION is NVARCHAR(20)).</summary>
    private const string IdempotencyAction = "ACTION";

    private const int StoredKeyLimit = 1000;

    private static readonly JsonSerializerOptions StoredJsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public async Task<DocumentActionExecution> ExecuteAsync(
        WorkbenchDefinition definition,
        FormDefinition form,
        string actionKey,
        DocumentActionRequest request,
        string userId,
        string employeeName,
        string? dataFilter,
        string idempotencyKey,
        CancellationToken token)
    {
        var config = DocumentActionConfigs.Find(definition.BusinessActions, actionKey);
        if (config is null || !registry.TryResolve(config.Key, out var action))
        {
            return new(DocumentActionStatus.NotFound,
                ErrorCode: DocumentActionErrorCodes.NotFound,
                ErrorMessage: $"模块 {definition.ModuleId} 未提供该操作，或该操作尚未登记实现。");
        }

        var (parameters, parameterErrors) = DocumentActionParams.Read(request.Params, config.Params);
        if (parameterErrors.Count > 0)
        {
            return new(DocumentActionStatus.ValidationFailed, ErrorCode: "VALIDATION_FAILED",
                ErrorMessage: "操作参数校验未通过。", FieldErrors: parameterErrors);
        }

        var probe = config.ConfirmTag && !request.Confirm;

        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        var pkColumns = await WorkbenchSql.GetPrimaryKeyColumnsAsync(connection, null, definition.MasterTable, token);
        if (pkColumns.Count == 0 || pkColumns.Count != request.Key.Count)
        {
            return new(DocumentActionStatus.KeyMismatch, ErrorCode: "RECORD_KEY_MISMATCH",
                ErrorMessage: "主键数量与模块主键不匹配。");
        }
        var keyValues = request.Key.Select(value => value?.Trim() ?? string.Empty).ToArray();
        var recordKey = string.Join(',', keyValues);

        if (!scopeFilter.TryBuildRecordScopePredicate(definition, dataFilter, out var scopePredicate, out var scopeParameters))
        {
            return new(DocumentActionStatus.FilterUnsupported, ErrorCode: "DATA_FILTER_UNSUPPORTED",
                ErrorMessage: "当前数据过滤条件尚不支持，已拒绝执行。");
        }

        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, token);
        try
        {
            // Lock the document row before the handler runs: a concurrent save or a second action on the
            // same document waits here instead of racing the write. Probe runs read-only, so it takes no lock.
            var exists = await ReadMasterRowAsync(connection, transaction, definition.MasterTable, pkColumns, keyValues, !probe, token);
            if (!exists)
            {
                await transaction.RollbackAsync(token);
                return new(DocumentActionStatus.NotFound, ErrorCode: DocumentActionErrorCodes.NotFound,
                    ErrorMessage: "目标单据不存在。");
            }
            if (!string.IsNullOrWhiteSpace(scopePredicate)
                && !await WorkbenchSql.RecordInScopeAsync(connection, transaction, definition.MasterTable, pkColumns, keyValues, scopePredicate, scopeParameters, token))
            {
                await transaction.RollbackAsync(token);
                return new(DocumentActionStatus.OutOfScope, ErrorCode: "RECORD_OUT_OF_SCOPE",
                    ErrorMessage: "目标记录不在当前用户数据范围内。");
            }

            // Configured precondition (e.g. "only a confirmed document may be turned into an adjustment"):
            // evaluated inside the transaction on the stored document state, never on what the client sent.
            if (config.Condition is { } condition)
            {
                var conditionPlan = new ModuleEffectPlan(definition.ModuleId, definition.MasterTable,
                    definition.DetailTable, definition.DefinitionVersion, pkColumns, [], []);
                if (!await effectPipeline.ConditionHoldsAsync(connection, transaction, conditionPlan, condition, keyValues, token))
                {
                    // Checked before the idempotency key is claimed, so a refused click does not burn the key.
                    await transaction.RollbackAsync(token);
                    return new(DocumentActionStatus.Failed, ErrorCode: DocumentActionErrorCodes.Failed,
                        ErrorMessage: $"操作“{Label(config)}”的前置条件不满足，已拒绝执行。");
                }
            }

            if (!probe)
            {
                var claimed = await idempotency.TryClaimAsync(connection, transaction, idempotencyKey, definition.ModuleId, IdempotencyAction, token);
                if (claimed is not null)
                {
                    await transaction.CommitAsync(token);
                    return Replay(claimed.ResultKey, config.Key);
                }
            }

            var context = new DocumentActionContext(
                definition.ModuleId,
                definition,
                form,
                connection,
                transaction,
                recordKey,
                keyValues,
                pkColumns,
                parameters,
                employeeName,
                userId,
                dataFilter,
                idempotencyKey,
                request.Confirm);

            var result = await action.ExecuteAsync(context, token);
            await auditWriter.WriteEventAsync(connection, transaction, definition.ModuleId, recordKey,
                config.Key,
                Describe(config, result, probe),
                employeeName, "WORKBENCH_RECORD", result: 1, fieldChanges: null, token,
                detailJson: Payload(config, parameters, result, probe));

            if (probe)
            {
                // Pre-check: everything the action would do is discarded, only the description travels back.
                await transaction.RollbackAsync(token);
                return new(DocumentActionStatus.Ok, result, RequiresConfirmation: true);
            }

            var stored = Serialize(result);
            if (stored is not null)
            {
                await idempotency.CompleteAsync(connection, transaction, idempotencyKey, stored, false, token);
            }
            await transaction.CommitAsync(token);
            return new(DocumentActionStatus.Ok, result);
        }
        catch (Exception exception) when (!token.IsCancellationRequested)
        {
            await RollbackQuietlyAsync(transaction, token);
            // The audit row of a failed action cannot live in the rolled-back transaction, so it is written
            // on its own connection: "who tried what and why it failed" survives the rollback.
            await auditWriter.WriteBestEffortAsync(definition.ModuleId, recordKey, config.Key,
                DescribeFailure(config, exception), employeeName, "WORKBENCH_RECORD", result: 0, fieldChanges: null, token);
            if (config.FailMode.Equals("WARN", StringComparison.OrdinalIgnoreCase))
            {
                logger.LogWarning(exception, "单据操作按 WARN 继续 module={ModuleId} action={Action}", definition.ModuleId, config.Key);
                return new(DocumentActionStatus.Ok,
                    new DocumentActionResult(DocumentActionOutcome.Message,
                        $"操作“{Label(config)}”未完成：{exception.Message}",
                        Warnings: [new DocumentActionWarning("ACTION_WARNING", exception.Message)]));
            }
            logger.LogError(exception, "单据操作失败 module={ModuleId} action={Action}", definition.ModuleId, config.Key);
            return new(DocumentActionStatus.Failed, ErrorCode: DocumentActionErrorCodes.Failed,
                ErrorMessage: $"操作“{Label(config)}”失败：{exception.Message}");
        }
    }

    /// <summary>Label shown to the user: configuration first, handler default as the last fallback.</summary>
    private string Label(DocumentActionConfig config) =>
        config.Label.Length > 0 ? config.Label : registry.LabelOf(config.Key);

    /// <summary>
    /// Reads the master row and, for a real (non-probe) execution, holds an update lock on it for the
    /// rest of the transaction. The identifier comes from the definition and the key columns from
    /// sys.columns, so nothing here is caller-supplied text.
    /// </summary>
    private static async Task<bool> ReadMasterRowAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string table,
        IReadOnlyList<string> pkColumns,
        IReadOnlyList<string> keyValues,
        bool lockRow,
        CancellationToken token)
    {
        var where = string.Join(" AND ", pkColumns.Select((column, index) => $"[{column}]=@k{index}"));
        var hint = lockRow ? " WITH (UPDLOCK, HOLDLOCK)" : string.Empty;
        await using var command = new SqlCommand($"SELECT 1 FROM dbo.[{table}]{hint} WHERE {where};", connection, transaction);
        for (var index = 0; index < pkColumns.Count; index++)
        {
            command.Parameters.AddWithValue($"@k{index}", keyValues[index]);
        }
        return await command.ExecuteScalarAsync(token) is not null;
    }

    private static async Task RollbackQuietlyAsync(SqlTransaction transaction, CancellationToken token)
    {
        try
        {
            await transaction.RollbackAsync(token);
        }
        catch (Exception exception) when (exception is InvalidOperationException or SqlException)
        {
            // The transaction was already completed by the failing handler; nothing left to roll back.
        }
    }

    private static string Describe(DocumentActionConfig config, DocumentActionResult result, bool probe) =>
        probe
            ? $"操作 {config.Key} 预检（未写入）：{result.Message ?? result.Outcome.ToString()}"
            : $"操作 {config.Key} 执行完成：{result.Message ?? result.Outcome.ToString()}";

    private static string DescribeFailure(DocumentActionConfig config, Exception exception) =>
        $"操作 {config.Key} 失败：{exception.Message}";

    private static string Payload(
        DocumentActionConfig config,
        IReadOnlyDictionary<string, string?> parameters,
        DocumentActionResult result,
        bool probe) =>
        JsonSerializer.Serialize(new
        {
            actionKey = config.Key,
            failMode = config.FailMode,
            confirmTag = config.ConfirmTag,
            probe,
            parameters,
            outcome = result.Outcome.ToString(),
            result.Message,
            result.TargetModuleId,
            result.TargetKey,
            warnings = result.Warnings,
        }, StoredJsonOptions);

    private string? Serialize(DocumentActionResult result)
    {
        var stored = JsonSerializer.Serialize(new StoredOutcome(
            result.Outcome.ToString(), result.Message, result.TargetModuleId, result.TargetKey, result.Warnings),
            StoredJsonOptions);
        return stored.Length <= StoredKeyLimit ? stored : null;
    }

    /// <summary>
    /// A repeated idempotency key means the user (or the client's retry) asked for the same action twice;
    /// the recorded outcome travels back and the action does not run again.
    /// </summary>
    private DocumentActionExecution Replay(string? storedKey, string actionKey)
    {
        if (string.IsNullOrWhiteSpace(storedKey))
        {
            return new(DocumentActionStatus.Ok,
                new DocumentActionResult(DocumentActionOutcome.Message, $"操作 {actionKey} 已执行过，本次重复请求未再次执行。"));
        }
        try
        {
            var stored = JsonSerializer.Deserialize<StoredOutcome>(storedKey, StoredJsonOptions);
            if (stored is null)
            {
                throw new JsonException("empty stored outcome");
            }
            var outcome = Enum.TryParse<DocumentActionOutcome>(stored.Outcome, ignoreCase: true, out var parsed)
                ? parsed
                : DocumentActionOutcome.Message;
            return new(DocumentActionStatus.Ok,
                new DocumentActionResult(outcome, stored.Message, stored.TargetModuleId, stored.TargetKey, stored.Warnings));
        }
        catch (JsonException)
        {
            return new(DocumentActionStatus.Ok,
                new DocumentActionResult(DocumentActionOutcome.Message, $"操作 {actionKey} 已执行过，本次重复请求未再次执行。"));
        }
    }

    private sealed record StoredOutcome(
        string Outcome,
        string? Message,
        int? TargetModuleId,
        IReadOnlyList<string>? TargetKey,
        IReadOnlyList<DocumentActionWarning>? Warnings);
}
