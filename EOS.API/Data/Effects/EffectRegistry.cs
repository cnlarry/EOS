namespace EOS.API.Data.Effects;

/// <summary>
/// Closed effect registry: per-key execution mode and implementation status. Keys are
/// the single source for "can this action execute"; the pipeline refuses unimplemented
/// keys instead of guessing. Module behaviour never branches on this registry — it only
/// decides whether the configured effect can run.
/// </summary>
public static class EffectRegistry
{
    public enum Status
    {
        /// <summary>Executed by the formula interpreter from MODULE_BUSINESS_ACTION_OP rows.</summary>
        Formula,

        /// <summary>Executed by a registered service handler from PARAM_STRUCT.</summary>
        Service,

        /// <summary>Translation data uses placeholder rows + params; handler not built yet.</summary>
        Pending,

        /// <summary>Registered in the catalog but with no configuration instances.</summary>
        Reserved,
    }

    public static readonly IReadOnlyDictionary<string, Status> Keys =
        new Dictionary<string, Status>(StringComparer.OrdinalIgnoreCase)
        {
        // Formula-covered (interpreter consumes their OP rows)
        ["field-accumulate"] = Status.Formula,
        ["adjust-projection"] = Status.Formula,
        ["stamp-last-activity"] = Status.Formula,
        ["completion-close"] = Status.Formula, // parameter-form instances pending handler
        // Service handlers registered
        ["inventory-move"] = Status.Service,
        ["set-state"] = Status.Service,
        ["balance-adjust"] = Status.Service,
        ["link-stamp"] = Status.Service,
        ["field-copy"] = Status.Service,
        ["callback-reprice"] = Status.Service,
        ["client-price-sync"] = Status.Service,
        ["supplier-price-sync"] = Status.Service,
        ["quote-parameter-recalc"] = Status.Service,
        ["payment-date-calc"] = Status.Service,
        // Placeholder-row + params shapes collected; handlers pending (see docs/plans/服务键形态证据.md)
        ["hr-usage-sync"] = Status.Pending,
        ["employee-contract-sync"] = Status.Pending,
        ["mould-batch-apply"] = Status.Pending,
        ["mrp-plan-alloc"] = Status.Pending,
        ["order-change-apply"] = Status.Pending,
        ["produce-change-apply"] = Status.Pending,
        ["purchase-change-apply"] = Status.Pending,
        // Catalog reserved keys (no instances)
        ["meta-link"] = Status.Reserved,
        ["flow-trigger"] = Status.Reserved,
        ["job-enqueue"] = Status.Reserved,
        ["legacy-sproc"] = Status.Reserved,
    };

    public static bool IsImplemented(string effectKey) =>
        Keys.TryGetValue(effectKey, out var status)
        && status is Status.Formula or Status.Service;
}
