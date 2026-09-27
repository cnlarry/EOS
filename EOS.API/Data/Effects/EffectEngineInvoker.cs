using System.Text.Json;
using EOS.API.Models;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data.Effects;

public sealed class EffectEngineSettings
{
    public bool Enabled { get; set; }
}

/// <summary>
/// Entry point the legacy-bridge call sites consult before invoking a workflow
/// stored procedure. Gated by the global switch (appsettings EffectEngine:Enabled)
/// AND the module flag inside the published definition (effectEngine.enabled). When
/// either is off the caller falls back to the stored procedure unchanged; when on,
/// the effect pipeline runs instead and errors surface as user-facing messages.
/// </summary>
public sealed class EffectEngineInvoker(
    EffectEngineSettings settings,
    EffectPlanLoader planLoader,
    EffectPipeline pipeline,
    ILogger<EffectEngineInvoker> logger)
{
    public bool IsEnabledFor(WorkbenchDefinition definition) =>
        settings.Enabled && definition.EffectEngineEnabled;

    /// <summary>
    /// True when the module's SAVE chain derives detail rows itself (e.g. a stocktake expanding
    /// its zone scope into lines). The save path must not reject a document merely because the
    /// caller submitted no details then: the authoritative question is whether the detail table
    /// still has no rows once the effect chain has run.
    /// </summary>
    public bool GeneratesDetailRows(WorkbenchDefinition definition) =>
        IsEnabledFor(definition) && DeclaresDetailGenerator(definition.BusinessActions);

    /// <summary>
    /// Whether the effect catalog of a module declares a save-time detail generator. Split out as
    /// a pure function so the rule can be asserted without standing up the whole pipeline.
    /// </summary>
    internal static bool DeclaresDetailGenerator(JsonElement? businessActions)
    {
        if (businessActions is not { ValueKind: JsonValueKind.Array } actions)
            return false;
        foreach (var action in actions.EnumerateArray())
        {
            if (action.ValueKind != JsonValueKind.Object)
                continue;
            if (action.TryGetProperty("enabled", out var enabled) && enabled.ValueKind == JsonValueKind.False)
                continue;
            // 用户点击行不是保存期行为：它不参与"保存自带明细生成"的判定，
            // 否则"用户点的操作"会被当成"保存时自动产生的东西"参与空明细放行。
            if (action.TryGetProperty("eventCode", out var declaredEvent) && declaredEvent.ValueKind == JsonValueKind.String
                && BusinessActionCatalog.IsManualEvent(declaredEvent.GetString()))
                continue;
            if (!action.TryGetProperty("effectKey", out var effectKey) || effectKey.ValueKind != JsonValueKind.String
                || !BusinessActionCatalog.IsDetailGenerator(effectKey.GetString()!))
                continue;
            if (!action.TryGetProperty("eventCode", out var eventCode) || eventCode.ValueKind != JsonValueKind.String
                || !EffectEventMapper.AppliesTo(eventCode.GetString()!, EffectEvent.Save))
                continue;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Runs the effect chain for the event. Returns (ran=false) when the engine is off
    /// for this module, so the caller keeps its legacy path. With an ambient transaction
    /// it executes inside it; otherwise it manages its own transaction.
    /// </summary>
    public async Task<(bool Ran, string? Error)> TryRunAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        WorkbenchDefinition definition,
        EffectEvent executionEvent,
        IReadOnlyList<string> keyValues,
        string executor,
        CancellationToken token)
    {
        var result = await RunCoreAsync(
            connection, transaction, definition, executionEvent, keyValues, executor, token, simulate: false);
        return (result.Ran, result.Error);
    }

    /// <summary>
    /// 与 <see cref="TryRunAsync"/> **同一条链**（非预演），但把逐步结果一并返回：
    /// 批核路径要把处理器写下的**非阻断告警**回传给用户（例如某档位配成"只告警不拦截"）。
    /// 与预演的区别只在 <c>simulate</c>——预演会额外收集公式行轨迹并回滚。
    /// </summary>
    public Task<EffectRunResult> TryRunWithStepsAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        WorkbenchDefinition definition,
        EffectEvent executionEvent,
        IReadOnlyList<string> keyValues,
        string executor,
        CancellationToken token)
        => RunCoreAsync(
            connection, transaction, definition, executionEvent, keyValues, executor, token, simulate: false);

    /// <summary>
    /// Same chain as <see cref="TryRunAsync"/>, but returns the per-step trace so a simulation
    /// can report what each step did. Steps are only collected in this mode.
    /// </summary>
    public Task<EffectRunResult> TryRunSimulatedAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        WorkbenchDefinition definition,
        EffectEvent executionEvent,
        IReadOnlyList<string> keyValues,
        string executor,
        CancellationToken token)
        => RunCoreAsync(
            connection, transaction, definition, executionEvent, keyValues, executor, token, simulate: true);

    /// <summary>Outcome of one chain run: whether the engine handled it, the blocking message
    /// and the per-step trace (empty unless a simulation asked for one).</summary>
    public sealed record EffectRunResult(bool Ran, string? Error, IReadOnlyList<EffectStepResult> Steps);

    private async Task<EffectRunResult> RunCoreAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        WorkbenchDefinition definition,
        EffectEvent executionEvent,
        IReadOnlyList<string> keyValues,
        string executor,
        CancellationToken token,
        bool simulate)
    {
        if (!IsEnabledFor(definition))
            return new EffectRunResult(false, null, []);

        var recordKey = string.Join(',', keyValues);
        try
        {
            var plan = planLoader.Load(definition);
            var hasActions = plan.Actions.Any(action =>
                EffectEventMapper.AppliesTo(action.EventCode, executionEvent));
            if (!hasActions)
                return new EffectRunResult(false, null, []);
            IReadOnlyList<EffectStepResult> steps;
            if (transaction is not null)
            {
                steps = await pipeline.ExecuteWithinTransactionAsync(
                    connection, transaction, plan, executionEvent, recordKey, executor, token, keyValues, simulate);
            }
            else
            {
                steps = await pipeline.ExecuteAsync(definition, executionEvent, recordKey, executor, token, keyValues);
            }
            logger.LogInformation(
                "效果引擎接管事件 module={ModuleId} event={Event} version={Version}",
                definition.ModuleId, executionEvent, definition.DefinitionVersion);
            return new EffectRunResult(true, null, steps);
        }
        catch (EffectStepFailedException exception)
        {
            return new EffectRunResult(true, FailureMessage(exception), exception.Steps);
        }
        catch (EffectValidationException exception)
        {
            return new EffectRunResult(true, exception.Message, []);
        }
        catch (EffectConfigException exception)
        {
            logger.LogError(exception,
                "效果引擎配置错误 module={ModuleId} event={Event}", definition.ModuleId, executionEvent);
            return new EffectRunResult(true, "效果配置错误：" + exception.Message, []);
        }
    }

    private static string FailureMessage(EffectStepFailedException exception) =>
        exception.Steps.LastOrDefault(step => step.Warning is not null)?.Warning ?? exception.Message;

    /// <summary>
    /// Runs the validation chain of the given stage without executing any action, and
    /// returns the blocking message (null when everything passes). Validation rules are a
    /// declarative catalog of the module's save-time rules, so they are NOT gated by the
    /// effect-engine switch or by the module takeover flag: gating them there would make an
    /// enabled rule silently do nothing. The caller supplies its open transaction so a
    /// failure rolls the whole save back.
    /// </summary>
    public async Task<string?> ValidateStageAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        WorkbenchDefinition definition,
        EffectEvent executionEvent,
        IReadOnlyList<string> keyValues,
        CancellationToken token)
    {
        var plan = planLoader.Load(definition);
        var stage = EffectPipeline.StageFor(executionEvent);
        var applicable = plan.Rules.Any(rule =>
            rule.Enabled && rule.Stage.Equals(stage, StringComparison.OrdinalIgnoreCase));
        if (!applicable)
            return null;

        try
        {
            await pipeline.ValidateWithinTransactionAsync(
                connection, transaction, plan, executionEvent, token, keyValues);
            return null;
        }
        catch (EffectValidationException exception)
        {
            return exception.Message;
        }
        catch (EffectConfigException exception)
        {
            logger.LogError(exception,
                "效果引擎配置错误 module={ModuleId} event={Event}", definition.ModuleId, executionEvent);
            return "效果配置错误：" + exception.Message;
        }
    }
}
