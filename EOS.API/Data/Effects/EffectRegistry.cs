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
        ["stamp-last-activity"] = Status.Service, // formula instances keep their OP rows; parameter instances run the handler
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
        ["order-change-apply"] = Status.Service,
        ["purchase-change-apply"] = Status.Service,
        ["produce-change-apply"] = Status.Service,
        ["payment-date-calc"] = Status.Service,
        ["mrp-plan-alloc"] = Status.Service,
        ["half-stock-move"] = Status.Service,
        ["car-filloil-sync"] = Status.Service,
        ["detail-field-sync"] = Status.Service,
        ["sample-edition-bump"] = Status.Service,
        ["mould-ids-sync"] = Status.Service,
        ["card-sibling-close"] = Status.Service,
        ["fields-metadata-sync"] = Status.Service,
        ["detail-flag-and-rollup"] = Status.Service,
        ["wage-month-doc-prune"] = Status.Service,
        ["doc-orphan-prune"] = Status.Service,
        ["sfc-plan-sync"] = Status.Service,
        ["cus-account-sync"] = Status.Service,
        ["pur-apply-sync"] = Status.Service,
        ["bom-size-backfill"] = Status.Service,
        ["detail-rollup"] = Status.Service,
        ["pur-pay-offset"] = Status.Service,
        ["cop-receipt-offset"] = Status.Service,
        ["cop-send-mo-flag"] = Status.Service,
        ["pur-purchase-sync"] = Status.Service,
        ["location-path-recalc"] = Status.Service,
        ["depot-sentinel-location"] = Status.Service,
        // Placeholder-row + params shapes collected; handlers pending (see docs/plans/服务键形态证据.md)
        ["hr-usage-sync"] = Status.Service,
        ["employee-contract-sync"] = Status.Service,
        ["employee-dimission-sync"] = Status.Service,
        ["mould-batch-apply"] = Status.Service,
        // Catalog reserved keys (no instances)
        ["meta-link"] = Status.Reserved,
        ["flow-trigger"] = Status.Reserved,
        ["job-enqueue"] = Status.Reserved,
    };

    public static bool IsImplemented(string effectKey) =>
        Keys.TryGetValue(effectKey, out var status)
        && status is Status.Formula or Status.Service;
}
