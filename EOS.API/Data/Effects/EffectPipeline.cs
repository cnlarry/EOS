using System.Data;
using System.Text;
using System.Text.Json;
using EOS.API.Models;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data.Effects;

/// <summary>Execution context handed to a service effect handler.</summary>
/// <remarks>
/// <paramref name="Warnings"/> 是**非阻断**的回报通道：处理器放行了、但有话要对用户说时写在这里，
/// 由管线汇入该步骤的 <see cref="EffectStepResult.Warning"/> 并随审批结果回传前端。
/// 与"抛异常"的分工是明确的：异常=这件事没做成（按动作的 FAIL_MODE 决定阻断还是继续），
/// 告警=做成了、但需要人知道（例如某档位配置成"只告警不拦截"）。
/// </remarks>
public sealed record ServiceEffectContext(
    SqlConnection Connection,
    SqlTransaction Transaction,
    ModuleEffectPlan Plan,
    EffectActionPlan Action,
    EffectEvent ExecutionEvent,
    string? RecordKey,
    IReadOnlyList<string> MasterKeyValues,
    string Executor,
    IList<string>? Warnings = null);

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
        IReadOnlyList<string>? masterKeyValues = null,
        // Simulation only: collect the per-formula-row trace (before → after values) and
        // report steps that did not run. Off by default, so the live path does no extra work.
        bool simulate = false)
    {
        var keys = masterKeyValues ?? Array.Empty<string>();
        if (StageFor(executionEvent) is { } actionStage)
        {
            await validationExecutor.ValidateAsync(connection, transaction, plan, actionStage, token, keys);
        }

        var results = new List<EffectStepResult>();
        foreach (var action in plan.Actions)
        {
            if (!EffectEventMapper.AppliesTo(action.EventCode, executionEvent))
                continue;
            if (!action.Enabled)
            {
                if (simulate)
                {
                    results.Add(new EffectStepResult(
                        action.Seq, action.EffectKey, Success: true, Warning: null, RowsAffected: 0,
                        EffectStepOutcome.Skipped, "该动作已停用，不参与本次执行。"));
                }
                continue;
            }

            try
            {
                // 本步骤的告警收集器：处理器放行但有话要说时写它（见 ServiceEffectContext.Warnings）
                var stepWarnings = new List<string>();
                var run = await ExecuteActionAsync(
                    connection, transaction, plan, action, executionEvent, recordKey, executor,
                    keys, token, simulate, stepWarnings);
                results.Add(new EffectStepResult(
                    action.Seq, action.EffectKey, Success: true,
                    Warning: stepWarnings.Count > 0 ? string.Join("\n", stepWarnings) : null, run.Rows,
                    run.Outcome, run.SkipReason, run.ConditionMatched, run.Ops));
                await auditWriter.WriteEventAsync(
                    connection, transaction, plan.ModuleId, recordKey,
                    $"EFFECT:{action.EffectKey}",
                    $"效果 {action.EffectName ?? action.EffectKey} 执行完成（影响 {run.Rows} 行）",
                    executor, "WORKBENCH_RECORD", result: 1, fieldChanges: null, token,
                    detailJson: BuildActionSnapshot(action));
            }
            catch (Exception exception) when (simulate)
            {
                // A simulation is a report, not a failure: the step is recorded as failed with
                // its message, then the chain stops (BLOCK) or carries on (WARN) exactly as the
                // live path would. The caller rolls back either way.
                var failed = new EffectStepResult(
                    action.Seq, action.EffectKey, Success: false, Warning: exception.Message, RowsAffected: 0,
                    EffectStepOutcome.Failed);
                if (action.FailMode.Equals("WARN", StringComparison.OrdinalIgnoreCase))
                {
                    logger.LogWarning(exception,
                        "效果步骤失败但按 WARN 继续 module={ModuleId} seq={Seq} key={Key}",
                        plan.ModuleId, action.Seq, action.EffectKey);
                    results.Add(failed);
                    await auditWriter.WriteEventAsync(
                        connection, transaction, plan.ModuleId, recordKey,
                        $"EFFECT:{action.EffectKey}",
                        $"效果 {action.EffectName ?? action.EffectKey} 失败（WARN 继续）：{exception.Message}",
                        executor, "WORKBENCH_RECORD", result: 0, fieldChanges: null, token,
                        detailJson: BuildActionSnapshot(action));
                    continue;
                }
                results.Add(failed);
                throw new EffectStepFailedException(results);
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
        // 该事件没有校验阶段时直接放行（无阶段即无规则，见 StageFor）。
        StageFor(executionEvent) is { } stage
            ? validationExecutor.ValidateAsync(
                connection, transaction, plan, stage, token, masterKeyValues ?? Array.Empty<string>())
            : Task.CompletedTask;

    private async Task<ActionRun> ExecuteActionAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        ModuleEffectPlan plan,
        EffectActionPlan action,
        EffectEvent executionEvent,
        string recordKey,
        string executor,
        IReadOnlyList<string> masterKeyValues,
        CancellationToken token,
        bool simulate,
        IList<string> warnings)
    {
        var probe = simulate ? new EffectSimulationProbe() : null;
        if (action.Condition is { } condition && !await ConditionHoldsAsync(
                connection, transaction, plan, condition, masterKeyValues, token))
        {
            // A condition that does not hold is not the same thing as a formula row that
            // matched zero rows: the first is a rule about this document, the second is a
            // locating-key problem. Keeping them apart is what makes the report actionable.
            return new ActionRun(0, EffectStepOutcome.Skipped, ConditionSkipReason, ConditionMatched: false, probe?.Take());
        }

        if (action.Ops.Count > 0)
        {
            var total = 0;
            foreach (var op in action.Ops)
                total += await formulaExecutor.ExecuteAsync(
                    connection, transaction, plan, op, executionEvent, action.Reverse, masterKeyValues, token, probe);
            return new ActionRun(total, EffectStepOutcome.Ran, null, ConditionMatched: true, probe?.Take());
        }

        if (!EffectRegistry.IsImplemented(action.EffectKey))
            throw new EffectConfigException(
                $"效果键 '{action.EffectKey}' 尚未实现执行（模块 {plan.ModuleId} SEQ={action.Seq}），灰度开启前需补齐 Handler。");
        if (!_handlers.TryGetValue(action.EffectKey, out var handler))
            throw new EffectConfigException(
                $"效果键 '{action.EffectKey}' 未注册服务 Handler（模块 {plan.ModuleId} SEQ={action.Seq}）。");
        var rows = await handler.ExecuteAsync(
            new ServiceEffectContext(
                connection, transaction, plan, action, executionEvent, recordKey, masterKeyValues, executor, warnings),
            token);
        // Service effects report no field-level diff on purpose: what a handler writes is its
        // own business, and pretending otherwise would be a guess dressed up as a fact.
        return new ActionRun(rows, EffectStepOutcome.Ran, null, ConditionMatched: true, probe?.Take());
    }

    /// <summary>Result of one action step, carrying the simulation trace when one was asked for.</summary>
    private sealed record ActionRun(
        int Rows,
        EffectStepOutcome Outcome,
        string? SkipReason,
        bool ConditionMatched,
        IReadOnlyList<EffectOpTrace>? Ops);

    private const string ConditionSkipReason = "条件未命中（该动作的条件对本单不成立）。";

    /// <summary>
    /// Evaluates an action-level condition against the current document: MASTER
    /// predicates are scoped by the document master keys (a bare EXISTS would be
    /// true whenever ANY document carries the value, wrongly firing the action for
    /// documents the baseline procedure skips); DETAIL predicates keep the historical
    /// unscoped EXISTS semantics; switches read SYSSS.
    ///
    /// Public because a user-triggered document action reads its CONDITION_STRUCT
    /// through this same evaluator: one condition dialect, one evaluator, so a
    /// precondition means the same thing whether the effect chain or a button asks.
    /// </summary>
    public async Task<bool> ConditionHoldsAsync(
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

    /// <summary>
    /// 事件 → 校验阶段的显式映射，**逐成员覆盖** <see cref="EffectEvent"/> 的每一个成员。
    /// 校验阶段是闭集（SAVE / APPROVE / DEAPPROVE / DELETE），事件闭集比它大，
    /// 故落不进阶段的事件显式映射为 null（不带校验闸）。
    /// </summary>
    /// <remarks>
    /// 不给"未登记"留默认值：新增事件成员而忘记在这里登记时，覆盖率测试（集合相等）会红，
    /// 而不是让新事件静默继承某个阶段的规则。
    /// </remarks>
    internal static readonly IReadOnlyDictionary<EffectEvent, string?> ValidationStagesByEvent =
        new Dictionary<EffectEvent, string?>
        {
            [EffectEvent.Save] = "SAVE",
            [EffectEvent.ApproveEffect] = "APPROVE",
            [EffectEvent.Deapprove] = "DEAPPROVE",
            [EffectEvent.Delete] = "DELETE",
            // 用户点击不是校验阶段的成员（配置侧也登记不进来），映射成 null 才与语义一致。
            [EffectEvent.Manual] = null,
            // 结案 / 取消结案同样不是校验阶段：它们只跑动作链。
            [EffectEvent.Endcase] = null,
            [EffectEvent.Unendcase] = null,
        };

    /// <summary>
    /// 该事件对应的校验阶段；返回 null 表示这条事件**不带校验闸**（不跑任何阶段的校验规则）。
    /// 映射表查不到（未登记的成员、越界取值）同样返回 null 且不抛：兜底是"不校验"，
    /// 不能是"默认按保存期校验"——后者会让"点结案"顺带跑一遍保存期规则，把保存的行为套到结案上。
    /// </summary>
    internal static string? StageFor(EffectEvent executionEvent) =>
        ValidationStagesByEvent.TryGetValue(executionEvent, out var stage) ? stage : null;

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
