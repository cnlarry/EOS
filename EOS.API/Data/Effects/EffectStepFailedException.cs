namespace EOS.API.Data.Effects;

/// <summary>
/// Raised by a simulated chain when a BLOCK step fails: the steps collected so far travel with
/// the exception so the simulation can still report what happened (and then roll back), instead
/// of surfacing the failure as an opaque error.
/// </summary>
public sealed class EffectStepFailedException(IReadOnlyList<EffectStepResult> steps) : Exception
{
    public IReadOnlyList<EffectStepResult> Steps { get; } = steps;
}
