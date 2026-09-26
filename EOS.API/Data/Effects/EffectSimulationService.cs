using System.Data;
using System.Diagnostics;
using Microsoft.Data.SqlClient;

using EOS.API.Models;

namespace EOS.API.Data.Effects;

/// <summary>
/// Effect chain simulation: runs the real approval/deapproval chain — preconditions, the
/// validation gate, the state flip and the effect chain — against one real document inside a
/// transaction and then rolls it back unconditionally, reporting what would have happened.
///
/// Two properties make the report trustworthy rather than decorative: the state flip really
/// happens before the chain runs (an effect condition reading CONFIRM_TAG must see the value it
/// would see), and every exit path rolls back. The audit row the chain writes disappears with
/// the rollback, which is why a simulation leaves no trace in AUDIT_EVENT.
/// </summary>
public sealed class EffectSimulationService(
    DbConnectionFactory connections,
    EffectPlanLoader planLoader,
    WorkbenchApprovalService approvals,
    ILogger<EffectSimulationService> logger)
{
    private const int LockTimeoutMilliseconds = 5000;
    private static readonly TimeSpan CommandBudget = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan TotalBudget = TimeSpan.FromSeconds(30);

    /// <summary>Raised when the simulation exceeds its budget; the transaction is rolled back
    /// before it surfaces.</summary>
    public sealed class TimeoutException : Exception
    {
    }

    /// <summary>
    /// 该模块的这条路径根本不执行效果链（无副作用批核 / 送审 / 未启用引擎）：
    /// 预演会给出与真实路径不符的报告，故拒绝并说明原因。
    /// </summary>
    public sealed class UnsupportedModuleException(string code, string message) : Exception(message)
    {
        public string Code { get; } = code;
    }

    public async Task<EffectSimulationReportDto> SimulateAsync(
        WorkbenchDefinition definition,
        string eventCode,
        IReadOnlyList<string> keyValues,
        bool approve,
        string executor,
        CancellationToken token,
        string configSource = "published")
    {
        var started = Stopwatch.StartNew();
        using var total = CancellationTokenSource.CreateLinkedTokenSource(token);
        total.CancelAfter(TotalBudget);
        try
        {
            return await RunAsync(definition, eventCode, keyValues, approve, executor, started, total.Token, token, configSource);
        }
        catch (Exception exception) when (IsTimeout(exception, total, token))
        {
            throw new TimeoutException();
        }
    }

    private async Task<EffectSimulationReportDto> RunAsync(
        WorkbenchDefinition definition,
        string eventCode,
        IReadOnlyList<string> keyValues,
        bool approve,
        string executor,
        Stopwatch started,
        CancellationToken budget,
        CancellationToken token,
        string configSource)
    {
        var plan = planLoader.Load(definition);
        // Only rows that run on this event describe the steps: a report must not name a step
        // after a configuration row that the chain skips (the same seq may exist for another
        // event). 解批跑的是「批核生效」行与「解批」行的并集，判据与管线同源。
        var executionEvent = approve ? EffectEvent.ApproveEffect : EffectEvent.Deapprove;
        var actions = new Dictionary<int, EffectActionPlan>();
        foreach (var action in plan.Actions.Where(action => EffectEventMapper.AppliesTo(action.EventCode, executionEvent)))
        {
            actions.TryAdd(action.Seq, action);
        }

        IReadOnlyList<EffectStepResult> steps = [];
        var precondition = new EffectSimulationGateDto(true);
        var validation = new EffectSimulationGateDto(true);
        var warnings = new List<string>();

        await using var connection = connections.Create();
        await connection.OpenAsync(budget);

        // 分流判据先于事务：这条路径本就不跑效果链时，连事务都不必开。
        var unsupported = await approvals.CheckSimulationSupportedAsync(connection, definition, approve, budget);
        if (unsupported is not null)
        {
            throw new UnsupportedModuleException(
                unsupported.ErrorCode ?? "SIMULATION_NOT_SUPPORTED",
                unsupported.ErrorMessage ?? "该模块的这条路径不执行效果链，无法预演。");
        }

        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted, budget);
        try
        {
            await using (var lockCommand = new SqlCommand($"SET LOCK_TIMEOUT {LockTimeoutMilliseconds};", connection, transaction))
            {
                await lockCommand.ExecuteNonQueryAsync(budget);
            }

            using var work = CancellationTokenSource.CreateLinkedTokenSource(budget);
            work.CancelAfter(CommandBudget);

            var blocked = await approvals.CheckApprovalPreconditionsAsync(
                connection, definition, keyValues, approve, work.Token, transaction: transaction);
            if (blocked is not null)
            {
                precondition = Gate(blocked);
                return BuildReport(definition, eventCode, keyValues, started, actions,
                    precondition, validation, steps, warnings, configSource);
            }

            var outcome = await approvals.RunApprovalCoreAsync(
                connection, transaction, definition, keyValues, approve, executor, executor,
                work.Token, simulate: true);
            if (outcome.Blocked is { } failure)
            {
                // Three ways the chain can stop, and they belong in different sections of the
                // report: the validation gate (nothing ran), a state guard (the document moved
                // under us), or a failed step inside the chain — which is a report about the
                // chain, so its trace must be carried even though the chain did not finish.
                if (string.Equals(failure.ErrorCode, "BUSINESS_VALIDATION_FAILED", StringComparison.Ordinal))
                {
                    validation = Gate(failure);
                }
                else if (string.Equals(failure.ErrorCode, "WORKFLOW_FAILED", StringComparison.Ordinal))
                {
                    steps = outcome.Steps;
                    CollectWarnings(steps, warnings);
                }
                else
                {
                    precondition = Gate(failure);
                }
                return BuildReport(definition, eventCode, keyValues, started, actions,
                    precondition, validation, steps, warnings, configSource);
            }

            steps = outcome.Steps;
            CollectWarnings(steps, warnings);
            logger.LogInformation(
                "效果链预演完成 module={ModuleId} event={Event} key={Key} steps={Steps} executor={User}",
                definition.ModuleId, eventCode, string.Join(',', keyValues), steps.Count, executor);
            return BuildReport(definition, eventCode, keyValues, started, actions,
                precondition, validation, steps, warnings, configSource);
        }
        finally
        {
            // Unconditional: success, blocked, failed or timed out — nothing survives.
            await transaction.RollbackAsync(CancellationToken.None);
        }
    }

    private static EffectSimulationReportDto BuildReport(
        WorkbenchDefinition definition,
        string eventCode,
        IReadOnlyList<string> keyValues,
        Stopwatch started,
        IReadOnlyDictionary<int, EffectActionPlan> actions,
        EffectSimulationGateDto precondition,
        EffectSimulationGateDto validation,
        IReadOnlyList<EffectStepResult> steps,
        IReadOnlyList<string> warnings,
        string configSource)
    {
        var ran = steps.Count(step => step.Outcome == EffectStepOutcome.Ran);
        var skipped = steps.Count(step => step.Outcome == EffectStepOutcome.Skipped);
        var failed = steps.Count(step => step.Outcome == EffectStepOutcome.Failed);
        started.Stop();
        return new EffectSimulationReportDto(
            definition.ModuleId,
            eventCode,
            definition.DefinitionVersion,
            keyValues.ToList(),
            (int)started.ElapsedMilliseconds,
            RolledBack: true,
            precondition,
            validation,
            steps.Select(step => ToDto(step, actions)).ToList(),
            new EffectSimulationCountsDto(steps.Count, ran, skipped, failed),
            warnings.ToList(),
            ConfigSource: configSource);
    }

    private static EffectSimulationStepDto ToDto(
        EffectStepResult step,
        IReadOnlyDictionary<int, EffectActionPlan> actions)
    {
        var action = actions.TryGetValue(step.Seq, out var configured) ? configured : null;
        return new EffectSimulationStepDto(
            step.Seq,
            step.EffectKey,
            action?.EffectName ?? Label(step.EffectKey),
            action?.Enabled ?? true,
            action?.FailMode ?? "BLOCK",
            step.Outcome switch
            {
                EffectStepOutcome.Ran => "ran",
                EffectStepOutcome.Skipped => "skipped",
                _ => "failed",
            },
            step.ConditionMatched,
            step.RowsAffected,
            step.Ops?.Select(op => new EffectSimulationOpDto(
                op.OpSeq,
                op.TargetTable,
                op.TargetField,
                op.OpCode,
                op.RowsAffected,
                op.Changes.Select(change => new EffectSimulationRowChangeDto(
                    change.Identity,
                    change.Columns.Select(column => new EffectSimulationColumnChangeDto(
                        column.Name, column.Before, column.After)).ToList())).ToList())).ToList() ?? [],
            Condition: action?.Condition?.ToString(),
            SkipReason: step.SkipReason,
            Message: step.Outcome == EffectStepOutcome.Failed ? step.Warning : null);
    }

    private static string Label(string effectKey) =>
        BusinessActionLabels.EffectKeys.TryGetValue(effectKey, out var name) ? name : effectKey;

    private static void CollectWarnings(IReadOnlyList<EffectStepResult> steps, List<string> warnings)
    {
        var truncated = steps
            .Where(step => step.Ops is not null)
            .SelectMany(step => step.Ops!)
            .Any(op => op.RowsAffected > op.Changes.Count);
        if (truncated)
        {
            warnings.Add(
                $"部分公式行的变更行数超过展示上限（每条公式行最多 {EffectSimulationProbe.MaxRowsPerOp} 行），报告已截断。");
        }
        var serviceOnly = steps.Any(step =>
            step.Outcome == EffectStepOutcome.Ran && step.Ops is null or { Count: 0 });
        if (serviceOnly)
        {
            warnings.Add("服务型效果只报告影响行数，不含字段级差异（其写入逻辑在服务内部）。");
        }
    }

    private static EffectSimulationGateDto Gate(RecordSaveResult failure) =>
        new(false, failure.ErrorCode, failure.ErrorMessage);

    /// <summary>
    /// 预演跑不完的几种形态：锁等待超时（1222）与命令超时（-2）各有上限，可能远早于总预算；
    /// 取消令牌可能是被预算触发（预演超时），也可能是调用方主动断开（那是取消，不是超时）。
    /// 前者必须按超时上报——否则用户看到的是 500，而真实原因是"这张单太大/被锁住"。
    /// </summary>
    private static bool IsTimeout(
        Exception exception,
        CancellationTokenSource total,
        CancellationToken token) =>
        exception switch
        {
            SqlException { Number: 1222 or -2 } => true,
            _ => !token.IsCancellationRequested
                && (total.IsCancellationRequested || exception is OperationCanceledException),
        };
}
