namespace EOS.API.Data.Effects;

/// <summary>
/// Collects the per-formula-row trace of a simulated effect chain: which rows a formula row
/// located and how the target value moved. Created only while a simulation runs, so the
/// normal execution path carries none of the cost.
/// </summary>
public sealed class EffectSimulationProbe
{
    /// <summary>Upper bound on the rows snapshotted per formula row (a document can touch a
    /// large target set; the report is a sample, not a dump).</summary>
    public const int MaxRowsPerOp = 50;

    private readonly List<EffectOpTrace> _ops = [];

    public void Add(EffectOpTrace trace) => _ops.Add(trace);

    public IReadOnlyList<EffectOpTrace> Take() => _ops.ToArray();
}
