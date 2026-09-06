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
        if (!IsEnabledFor(definition))
            return (false, null);

        var plan = planLoader.Load(definition);
        var hasActions = plan.Actions.Any(action =>
            EffectEventMapper.TryParse(action.EventCode, out var actionEvent) && actionEvent == executionEvent);
        if (!hasActions)
            return (false, null);

        var recordKey = string.Join(',', keyValues);
        try
        {
            if (transaction is not null)
            {
                await pipeline.ExecuteWithinTransactionAsync(
                    connection, transaction, plan, executionEvent, recordKey, executor, token, keyValues);
            }
            else
            {
                await pipeline.ExecuteAsync(definition, executionEvent, recordKey, executor, token);
            }
            logger.LogInformation(
                "效果引擎接管事件 module={ModuleId} event={Event} version={Version}",
                definition.ModuleId, executionEvent, definition.DefinitionVersion);
            return (true, null);
        }
        catch (EffectValidationException exception)
        {
            return (true, exception.Message);
        }
        catch (EffectConfigException exception)
        {
            logger.LogError(exception,
                "效果引擎配置错误 module={ModuleId} event={Event}", definition.ModuleId, executionEvent);
            return (true, "效果配置错误：" + exception.Message);
        }
    }
}
