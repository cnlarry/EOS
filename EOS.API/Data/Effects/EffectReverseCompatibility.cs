namespace EOS.API.Data.Effects;

/// <summary>
/// Which reverse kinds each effect key actually accepts at run time.
///
/// The catalog (<see cref="EffectStructSchemas.AllReverseKinds"/>) is the closed set of kind
/// values; it is not the set each handler tolerates. Several handlers reject anything outside a
/// narrow subset, and the rejection happens on a real document — far too late. This class is
/// that per-key subset, so the publication gate can refuse an unsupported combination before it
/// ever reaches a document.
///
/// Every entry below points at the code that enforces it; an entry without such evidence would
/// be a guess, and a guess here either blocks working configuration or lets a broken one through.
/// </summary>
public static class EffectReverseCompatibility
{
    /// <summary>Kinds a formula row cannot express (the executor refuses them outright).</summary>
    private const string FormulaUnsupported = "clear-on-deapprove";

    private static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> Constrained =
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            // ServiceEffectHandlers/CarFilloilSyncHandler.cs
            ["car-filloil-sync"] = Set("restore-previous"),
            // ServiceEffectHandlers/DetailFieldSyncHandler.cs
            ["detail-field-sync"] = Set("restore-previous"),
            // ServiceEffectHandlers/ChangeApplyHandler.cs
            ["order-change-apply"] = Set("none", "no-reverse"),
            ["purchase-change-apply"] = Set("none", "no-reverse"),
            ["produce-change-apply"] = Set("none", "no-reverse"),
            // ServiceEffectHandlers/ContractSyncHandler.cs
            ["employee-contract-sync"] = Set("recompute-excluding-self"),
            // ServiceEffectHandlers/DimissionSyncHandler.cs
            ["employee-dimission-sync"] = Set("restore-active"),
            // ServiceEffectHandlers/HalfStockMoveHandler.cs
            ["half-stock-move"] = Set("reverse-flow"),
            // ServiceEffectHandlers/HrUsageSyncHandler.cs
            ["hr-usage-sync"] = Set("auto-reverse"),
            // ServiceEffectHandlers/LinkStampFieldCopyHandlers.cs
            ["link-stamp"] = Set("clear-refs", "clear-refs-unfinish", "no-reverse", "none"),
            ["field-copy"] = Set("none", "no-reverse"),
            // ServiceEffectHandlers/MouldBatchApplyHandler.cs
            ["mould-batch-apply"] = Set("auto-reverse"),
            // ServiceEffectHandlers/PaymentDateCalcHandler.cs
            ["payment-date-calc"] = Set("clear-on-deapprove", "no-reverse"),
            // ServiceEffectHandlers/PriceSyncHandler.cs
            ["client-price-sync"] = Set("no-reverse", "restore-old-price"),
            ["supplier-price-sync"] = Set("no-reverse", "restore-old-price"),
            // ServiceEffectHandlers/SampleEditionBumpHandler.cs
            ["sample-edition-bump"] = Set("none", "no-reverse"),
        };

    /// <summary>
    /// Kinds the given effect key accepts, in catalog order.
    /// <paramref name="hasFormulaRows"/> decides which executor runs: a row carrying formula rows
    /// goes through the formula interpreter, everything else through the service handler.
    /// </summary>
    public static IReadOnlyList<string> AllowedKinds(string effectKey, bool hasFormulaRows)
    {
        var all = EffectStructSchemas.AllReverseKinds();
        if (hasFormulaRows)
        {
            return all.Where(kind => !kind.Equals(FormulaUnsupported, StringComparison.OrdinalIgnoreCase)).ToList();
        }
        if (Constrained.TryGetValue(effectKey.Trim(), out var subset))
        {
            return all.Where(kind => subset.Contains(kind)).ToList();
        }
        return all;
    }

    /// <summary>Whether the configured kind survives execution for this key.</summary>
    public static bool IsSupported(string effectKey, string? kind, bool hasFormulaRows)
    {
        if (string.IsNullOrWhiteSpace(kind))
        {
            return true; // 未配反向结构：由各自的缺 kind 守卫负责，不在本校验范围内
        }
        return AllowedKinds(effectKey, hasFormulaRows)
            .Any(candidate => candidate.Equals(kind, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Keys whose reverse kind is constrained by their handler (publication gate input).</summary>
    public static IReadOnlyList<string> ConstrainedKeys() =>
        Constrained.Keys.OrderBy(key => key, StringComparer.OrdinalIgnoreCase).ToList();

    private static IReadOnlySet<string> Set(params string[] kinds) =>
        new HashSet<string>(kinds, StringComparer.OrdinalIgnoreCase);
}
