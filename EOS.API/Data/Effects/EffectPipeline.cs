using System.Data;
using System.Text;
using System.Text.Json;
using EOS.API.Models;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data.Effects;

/// <summary>Execution context handed to a service effect handler.</summary>
public sealed record ServiceEffectContext(
    SqlConnection Connection,
    SqlTransaction Transaction,
    ModuleEffectPlan Plan,
    EffectActionPlan Action,
    EffectEvent ExecutionEvent,
    string? RecordKey,
    IReadOnlyList<string> MasterKeyValues,
    string Executor);

/// <summary>A C# implementation backing one service-style effect key (parameter-mode).</summary>
public interface IEffectServiceHandler
{
    string EffectKey { get; }

    Task<int> ExecuteAsync(ServiceEffectContext context, CancellationToken token);
}

/// <summary>
/// Unified effect pipeline: executes the configured action chain for one module event
/// inside the caller's transaction. The pipeline knows no modules — behaviour comes
/// entirely from the effect plan (zero moduleId branches). Validation chain for the
/// matching stage runs before the action chain; both share the same transaction so a
/// BLOCK failure rolls the whole event back.
/// </summary>
public sealed class EffectPipeline(
    DbConnectionFactory connections,
    EffectPlanLoader planLoader,
    EffectFormulaExecutor formulaExecutor,
    IEnumerable<IEffectServiceHandler> serviceHandlers,
    EffectValidationExecutor validationExecutor,
    WorkbenchAuditWriter auditWriter,
    ILogger<EffectPipeline> logger)
{
    private readonly Dictionary<string, IEffectServiceHandler> _handlers =
        serviceHandlers.ToDictionary(handler => handler.EffectKey, StringComparer.OrdinalIgnoreCase);

    private readonly EffectConditionCompiler _conditions = new();

    /// <summary>Loads the plan from the current published definition and runs the event.</summary>
    public Task<IReadOnlyList<EffectStepResult>> ExecuteAsync(
        WorkbenchDefinition definition,
        EffectEvent executionEvent,
        string recordKey,
        string executor,
        CancellationToken token,
        IReadOnlyList<string>? masterKeyValues = null)
    {
        return ExecuteLoadedAsync(definition, executionEvent, recordKey, executor, token, masterKeyValues ?? Array.Empty<string>());
    }

    private async Task<IReadOnlyList<EffectStepResult>> ExecuteLoadedAsync(
        WorkbenchDefinition definition,
        EffectEvent executionEvent,
        string recordKey,
        string executor,
        CancellationToken token,
        IReadOnlyList<string> masterKeyValues)
    {
        var plan = planLoader.Load(definition);
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, token);
        try
        {
            var results = await ExecuteWithinTransactionAsync(
                connection, transaction, plan, executionEvent, recordKey, executor, token, masterKeyValues);
            await transaction.CommitAsync(token);
            return results;
        }
        catch
        {
            await transaction.RollbackAsync(token);
            throw;
        }
    }

    /// <summary>
    /// Runs the validation chain (stage matching the event) and then the action chain,
    /// inside the caller's open transaction. Throws EffectValidationException when a
    /// validation fails (BLOCK semantics for all validation failures).
    /// </summary>
    public async Task<IReadOnlyList<EffectStepResult>> ExecuteWithinTransactionAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        ModuleEffectPlan plan,
        EffectEvent executionEvent,
        string recordKey,
        string executor,
        CancellationToken token,
        IReadOnlyList<string>? masterKeyValues = null)
    {
        await validationExecutor.ValidateAsync(
            connection, transaction, plan, StageFor(executionEvent), token, masterKeyValues ?? Array.Empty<string>());

        var results = new List<EffectStepResult>();
        foreach (var action in plan.Actions)
        {
            if (!EffectEventMapper.AppliesTo(action.EventCode, executionEvent))
                continue;
            if (!action.Enabled)
                continue;

            try
            {
                var rows = await ExecuteActionAsync(
                    connection, transaction, plan, action, executionEvent, recordKey, executor,
                    masterKeyValues ?? Array.Empty<string>(), token);
                results.Add(new EffectStepResult(action.Seq, action.EffectKey, Success: true, Warning: null, rows));
                await auditWriter.WriteEventAsync(
                    connection, transaction, plan.ModuleId, recordKey,
                    $"EFFECT:{action.EffectKey}",
                    $"效果 {action.EffectName ?? action.EffectKey} 执行完成（影响 {rows} 行）",
                    executor, "WORKBENCH_RECORD", result: 1, fieldChanges: null, token,
                    detailJson: BuildActionSnapshot(action));
            }
            catch (EffectValidationException)
            {
                throw;
            }
            catch (Exception exception) when (action.FailMode.Equals("WARN", StringComparison.OrdinalIgnoreCase))
            {
                logger.LogWarning(exception,
                    "效果步骤失败但按 WARN 继续 module={ModuleId} seq={Seq} key={Key}",
                    plan.ModuleId, action.Seq, action.EffectKey);
                results.Add(new EffectStepResult(
                    action.Seq, action.EffectKey, Success: false, Warning: exception.Message, RowsAffected: 0));
                await auditWriter.WriteEventAsync(
                    connection, transaction, plan.ModuleId, recordKey,
                    $"EFFECT:{action.EffectKey}",
                    $"效果 {action.EffectName ?? action.EffectKey} 失败（WARN 继续）：{exception.Message}",
                    executor, "WORKBENCH_RECORD", result: 0, fieldChanges: null, token,
                    detailJson: BuildActionSnapshot(action));
            }
        }
        return results;
    }

    /// <summary>
    /// Runs only the validation chain for the event's stage, inside the caller's open
    /// transaction. Validation rules are the module's declarative validation catalog, so
    /// callers run them independently of whether the engine has also taken over the
    /// module's actions (a module may carry validations without any configured action).
    /// </summary>
    public Task ValidateWithinTransactionAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        ModuleEffectPlan plan,
        EffectEvent executionEvent,
        CancellationToken token,
        IReadOnlyList<string>? masterKeyValues = null) =>
        validationExecutor.ValidateAsync(
            connection, transaction, plan, StageFor(executionEvent), token, masterKeyValues ?? Array.Empty<string>());

    private async Task<int> ExecuteActionAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        ModuleEffectPlan plan,
        EffectActionPlan action,
        EffectEvent executionEvent,
        string recordKey,
        string executor,
        IReadOnlyList<string> masterKeyValues,
        CancellationToken token)
    {
        if (action.Condition is { } condition && !await ConditionHoldsAsync(
                connection, transaction, plan, condition, masterKeyValues, token))
            return 0;

        if (action.Ops.Count > 0)
        {
            var rows = 0;
            foreach (var op in action.Ops)
                rows += await formulaExecutor.ExecuteAsync(
                    connection, transaction, plan, op, executionEvent, action.Reverse, masterKeyValues, token);
            return rows;
        }

        if (!EffectRegistry.IsImplemented(action.EffectKey))
            throw new EffectConfigException(
                $"效果键 '{action.EffectKey}' 尚未实现执行（模块 {plan.ModuleId} SEQ={action.Seq}），灰度开启前需补齐 Handler。");
        if (!_handlers.TryGetValue(action.EffectKey, out var handler))
            throw new EffectConfigException(
                $"效果键 '{action.EffectKey}' 未注册服务 Handler（模块 {plan.ModuleId} SEQ={action.Seq}）。");
        return await handler.ExecuteAsync(
            new ServiceEffectContext(connection, transaction, plan, action, executionEvent, recordKey, masterKeyValues, executor),
            token);
    }

    /// <summary>
    /// Evaluates an action-level condition against the current document: MASTER
    /// predicates are scoped by the document master keys (a bare EXISTS would be
    /// true whenever ANY document carries the value, wrongly firing the action for
    /// documents the legacy procedure skips); DETAIL predicates keep the historical
    /// unscoped EXISTS semantics; switches read SYSSS.
    /// </summary>
    private async Task<bool> ConditionHoldsAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        ModuleEffectPlan plan,
        JsonElement condition,
        IReadOnlyList<string> masterKeyValues,
        CancellationToken token)
    {
        if (plan.MasterTable is null)
            throw new EffectConfigException("两表皆空模块禁止携带条件的效果动作。");
        var fragment = _conditions.Compile(
            condition,
            (scope, _) => scope.ToUpperInvariant() switch
            {
                "MASTER" => "M",
                "DETAIL" => "D",
                _ => null,
            },
            _ => true);
        var parameters = new List<EffectSqlParameter>(fragment.Parameters);
        var sql = new StringBuilder("SELECT CASE WHEN EXISTS (SELECT 1 FROM dbo.")
            .Append(EffectConditionCompiler.Identifier(plan.MasterTable)).Append(" M");
        if (plan.DetailTable is not null)
            sql.Append(" LEFT JOIN dbo.").Append(EffectConditionCompiler.Identifier(plan.DetailTable)).Append(" D ON 1=1");
        var keys = Math.Min(plan.MasterPkOrder.Count, masterKeyValues.Count);
        if (keys == 0)
            throw new EffectConfigException("条件求值缺少主表主键值，禁止无单据范围执行。");
        var keyPredicates = new List<string>();
        for (var index = 0; index < keys; index++)
        {
            var name = "@cmk" + index;
            parameters.Add(new EffectSqlParameter(name, masterKeyValues[index]));
            keyPredicates.Add($"M.{EffectConditionCompiler.Identifier(plan.MasterPkOrder[index])} = {name}");
        }
        sql.Append(" WHERE ").Append(fragment.Sql)
            .Append(" AND ").Append(string.Join(" AND ", keyPredicates))
            .Append(") THEN 1 ELSE 0 END;");
        await using var command = new SqlCommand(sql.ToString(), connection, transaction);
        foreach (var parameter in parameters)
            command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        return Convert.ToInt32(await command.ExecuteScalarAsync(token)) == 1;
    }

    internal static string StageFor(EffectEvent executionEvent) => executionEvent switch
    {
        EffectEvent.Save => "SAVE",
        EffectEvent.ApproveEffect => "APPROVE",
        EffectEvent.Deapprove => "DEAPPROVE",
        _ => "SAVE",
    };

    /// <summary>
    /// Snapshots the configured action (parameters and expanded formula rows) into the
    /// audit payload so the rule a document was processed under can be replayed later.
    /// </summary>
    private static string BuildActionSnapshot(EffectActionPlan action)
    {
        var payload = new
        {
            eventCode = action.EventCode,
            action = new
            {
                action.Seq,
                action.EffectKey,
                action.EffectName,
                action.Enabled,
                action.FailMode,
                action.Condition,
                Params = action.Params,
                action.Reverse,
                ops = action.Ops.Select(op => new
                {
                    op.OpSeq,
                    op.TargetTable,
                    op.TargetField,
                    op.OpCode,
                    source = new { op.Source.Scope, op.Source.Table, op.Source.Field },
                    op.SourceAgg,
                    op.Terms,
                    op.Match,
                    op.Remark,
                }),
            },
        };
        return JsonSerializer.Serialize(payload, SnapshotJsonOptions);
    }

    private static readonly JsonSerializerOptions SnapshotJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };
}
