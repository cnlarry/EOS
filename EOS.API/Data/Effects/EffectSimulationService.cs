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

    public async Task<EffectSimulationReportDto> SimulateAsync(
        WorkbenchDefinition definition,
        string eventCode,
        IReadOnlyList<string> keyValues,
        bool approve,
        string executor,
        CancellationToken token)
    {
        var started = Stopwatch.StartNew();
        using var total = CancellationTokenSource.CreateLinkedTokenSource(token);
        total.CancelAfter(TotalBudget);
        try
        {
            return await RunAsync(definition, eventCode, keyValues, approve, executor, started, total.Token, token);
        }
        catch (Exception exception) when (IsBudgetExceeded(exception, total, token))
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
        CancellationToken token)
    {
        var plan = planLoader.Load(definition);
        var actions = new Dictionary<int, EffectActionPlan>();
        foreach (var action in plan.Actions)
        {
            actions.TryAdd(action.Seq, action);
        }

        IReadOnlyList<EffectStepResult> steps = [];
        var precondition = new EffectSimulationGateDto(true);
        var validation = new EffectSimulationGateDto(true);
        var warnings = new List<string>();

        await using var connection = connections.Create();
        await connection.OpenAsync(budget);
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
                    precondition, validation, steps, warnings);
            }

            var outcome = await approvals.RunApprovalCoreAsync(
                connection, transaction, definition, keyValues, approve, executor, executor,
                work.Token, simulate: true);
            if (outcome.Blocked is { } failure)
            {
                // The validation gate is the only blocked outcome reached after the
                // preconditions; a state flip that hit zero rows means someone else moved the
                // document first and belongs to the precondition section.
                if (string.Equals(failure.ErrorCode, "BUSINESS_VALIDATION_FAILED", StringComparison.Ordinal))
                {
                    validation = Gate(failure);
                }
                else
                {
                    precondition = Gate(failure);
                }
                return BuildReport(definition, eventCode, keyValues, started, actions,
                    precondition, validation, steps, warnings);
            }

            steps = outcome.Steps;
            CollectWarnings(steps, warnings);
            logger.LogInformation(
                "效果链预演完成 module={ModuleId} event={Event} key={Key} steps={Steps} executor={User}",
                definition.ModuleId, eventCode, string.Join(',', keyValues), steps.Count, executor);
            return BuildReport(definition, eventCode, keyValues, started, actions,
                precondition, validation, steps, warnings);
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
        IReadOnlyList<string> warnings)
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
            warnings.ToList());
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

    private static bool IsBudgetExceeded(
        Exception exception,
        CancellationTokenSource total,
        CancellationToken token) =>
        total.IsCancellationRequested
        && !token.IsCancellationRequested
        && exception is OperationCanceledException or TimeoutException or SqlException { Number: -2 };
}
