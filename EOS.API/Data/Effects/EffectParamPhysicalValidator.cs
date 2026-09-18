using System.Text.Json;
using EOS.API.Data.Effects.ServiceEffectHandlers;

namespace EOS.API.Data.Effects;

/// <summary>
/// Physical validation of service-effect parameters (PARAM_STRUCT) against the column
/// whitelist, without touching the database. Every effect key delegates to the very
/// parser its handler runs at execution time, so a configuration that cannot execute is
/// rejected when it is saved instead of blocking a document later. Keys whose parameters
/// carry no configuration-driven table or column reference are listed in
/// <see cref="NoReferenceKeys"/> and need no check.
/// </summary>
public static class EffectParamPhysicalValidator
{
    /// <summary>
    /// Keys whose parameters hold no configurable table/column reference: their tables and
    /// columns are handler constants, or the parameters are translation-time placeholders
    /// whose semantics live in the formula rows. They are skipped, not silently passed —
    /// <see cref="IsCovered"/> exposes the distinction to callers.
    /// </summary>
    private static readonly IReadOnlySet<string> NoReferenceKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "balance-adjust",
        "completion-close",
        "adjust-projection",
    };

    /// <summary>Effect keys this validator resolves table/column references for.</summary>
    private static readonly IReadOnlySet<string> ReferenceKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "inventory-move",
        "set-state",
        "link-stamp",
        "field-copy",
        "stamp-last-activity",
        "payment-date-calc",
        "mrp-plan-alloc",
        "order-change-apply",
        "purchase-change-apply",
        "produce-change-apply",
        "client-price-sync",
        "supplier-price-sync",
        "quote-parameter-recalc",
        "hr-usage-sync",
        "employee-contract-sync",
        "employee-dimission-sync",
        "mould-batch-apply",
        "callback-reprice",
        "half-stock-move",
        "car-filloil-sync",
        "detail-field-sync",
        "sample-edition-bump",
        "mould-ids-sync",
        "card-sibling-close",
        "fields-metadata-sync",
        "detail-flag-and-rollup",
        "wage-month-doc-prune",
        "doc-orphan-prune",
        "sfc-plan-sync",
        "cus-account-sync",
        "pur-apply-sync",
        "bom-size-backfill",
        "cop-account-rollup",
        "cop-prepay-rollup",
        "purchase-due-rollup",
        "pur-prepay-rollup",
        "pur-pay-offset",
        "cop-receipt-offset",
    };

    /// <summary>True when the key either carries no reference or is resolved by this validator.</summary>
    public static bool IsCovered(string effectKey) =>
        ReferenceKeys.Contains(effectKey) || NoReferenceKeys.Contains(effectKey);

    /// <summary>
    /// Resolves and checks the parameter references. Returns human-readable issues; an
    /// empty list means the parameters reference only existing tables and columns.
    /// </summary>
    public static IReadOnlyList<string> Validate(
        string effectKey,
        string? paramsJson,
        ModuleEffectPlan plan,
        ISet<string> columns)
    {
        var issues = new List<string>();
        if (string.IsNullOrWhiteSpace(paramsJson) || !ReferenceKeys.Contains(effectKey))
            return issues;

        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(paramsJson!);
            root = document.RootElement.Clone();
        }
        catch (JsonException exception)
        {
            issues.Add($"效果 '{effectKey}' 参数不是合法 JSON：{exception.Message}");
            return issues;
        }

        try
        {
            switch (effectKey.ToLowerInvariant())
            {
                case "inventory-move":
                    // The row-set builder performs the whole physical check; the key values
                    // only feed SQL parameters, so placeholders are enough here.
                    _ = InventoryMovePlan.Parse(root).BuildRowSet(
                        plan,
                        Enumerable.Repeat(string.Empty, Math.Max(plan.MasterPkOrder.Count, 2)).ToArray(),
                        columns);
                    break;
                case "set-state":
                    SetStateHandler.ValidateParams(root, plan, columns);
                    break;
                case "link-stamp":
                    _ = LinkStampSpec.Parse(root, plan, columns);
                    break;
                case "field-copy":
                    FieldCopyHandler.ValidateParams(root, plan, columns);
                    break;
                case "stamp-last-activity":
                    _ = StampLastActivityConfig.Parse(root, plan, columns);
                    break;
                case "payment-date-calc":
                    _ = PaymentDateConfig.Parse(root, plan, columns);
                    break;
                case "mrp-plan-alloc":
                    _ = MrpPlanAllocSpec.Parse(root, plan, columns);
                    break;
                case "order-change-apply":
                case "purchase-change-apply":
                case "produce-change-apply":
                    var change = ChangeApplyConfig.Parse(root, plan, columns);
                    if (change.ProjectionMode is not null)
                        ProduceChangeProjection.ValidateColumns(change, columns);
                    break;
                case "client-price-sync":
                case "supplier-price-sync":
                    _ = PriceSyncConfig.Parse(root, plan, columns);
                    break;
                case "quote-parameter-recalc":
                    _ = QuoteParameterConfig.Parse(root, plan, columns);
                    break;
                case "hr-usage-sync":
                    _ = HrUsageSyncSpec.Parse(root, plan, columns);
                    break;
                case "employee-contract-sync":
                    _ = ContractSyncSpec.Parse(root, plan, columns);
                    break;
                case "employee-dimission-sync":
                    _ = DimissionSyncSpec.Parse(root, plan, columns);
                    break;
                case "mould-batch-apply":
                    _ = MouldBatchApplySpec.Parse(root, plan, columns);
                    break;
                case "callback-reprice":
                    _ = CallbackConfig.Parse(root, plan, columns);
                    break;
                case "half-stock-move":
                    HalfStockMovePlan.Parse(root).ValidateColumns(plan, columns);
                    break;
                case "sample-edition-bump":
                    _ = SampleEditionBumpSpec.Parse(root, plan, columns);
                    break;
                case "mould-ids-sync":
                    _ = MouldIdsSyncHandler.Parse(root, plan, columns);
                    break;
                case "card-sibling-close":
                    _ = CardSiblingCloseHandler.Parse(root, plan, columns);
                    break;
                case "fields-metadata-sync":
                    _ = FieldsMetadataSyncHandler.Parse(root, columns);
                    break;
                case "detail-flag-and-rollup":
                    _ = DetailFlagAndRollupHandler.Parse(root, columns);
                    break;
                case "wage-month-doc-prune":
                    _ = WageMonthDocPruneHandler.Parse(root, columns);
                    break;
                case "doc-orphan-prune":
                    _ = DocOrphanPruneHandler.Parse(root, columns);
                    break;
                case "sfc-plan-sync":
                    _ = SfcPlanSyncHandler.Parse(root, columns);
                    break;
                case "cus-account-sync":
                    _ = CusAccountSyncHandler.Parse(root, columns);
                    break;
                case "pur-apply-sync":
                    _ = PurApplySyncHandler.Parse(root, columns);
                    break;
                case "bom-size-backfill":
                    _ = BomSizeBackfillHandler.Parse(root, plan.MasterTable!, columns);
                    break;
                case "cop-account-rollup":
                    _ = CopAccountRollupHandler.Parse(root, plan.MasterTable!, columns);
                    break;
                case "cop-prepay-rollup":
                    _ = CopPrepayRollupHandler.Parse(root, plan.MasterTable!, columns);
                    break;
                case "purchase-due-rollup":
                    _ = PurchaseDueRollupHandler.Parse(root, plan.MasterTable!, columns);
                    break;
                case "pur-prepay-rollup":
                    _ = PurPrepayRollupHandler.Parse(root, plan.MasterTable!, columns);
                    break;
                case "pur-pay-offset":
                case "cop-receipt-offset":
                    _ = PrepayOffsetRunner.Parse(root, plan.MasterTable!, columns, "offset-check");
                    break;
                case "detail-field-sync":
                    _ = DetailFieldSyncSpec.Parse(root, plan, columns);
                    break;
                case "car-filloil-sync":
                    _ = CarFilloilSyncSpec.Parse(root, plan, columns);
                    break;
                default:
                    return issues;
            }
        }
        catch (EffectConfigException exception)
        {
            issues.Add($"效果 '{effectKey}' 参数物理校验未通过：{exception.Message}");
        }
        catch (Exception exception)
        {
            // Any other failure is still a configuration defect; report it instead of
            // letting the save path surface it as an unhandled error.
            issues.Add($"效果 '{effectKey}' 参数校验异常：{exception.Message}");
        }
        return issues;
    }
}
