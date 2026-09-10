using EOS.API.Data.Effects;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// The save-time parameter check must reject the same configurations the handlers refuse
/// at execution time, so a broken reference is caught when the configuration is edited.
/// </summary>
public class EffectParamPhysicalValidatorTests
{
    private static readonly ISet<string> Columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "CUS_EXPORT_M", "CUS_EXPORT_M.EXPORT_TYPE", "CUS_EXPORT_M.EXPORT_NO",
        "CUS_EXPORT_D", "CUS_EXPORT_D.EXPORT_TYPE", "CUS_EXPORT_D.EXPORT_NO", "CUS_EXPORT_D.PRO_NO",
        "CUS_PRODUCT", "CUS_PRODUCT.PRO_ID", "CUS_PRODUCT.INSIDE_NAME",
        "MOU_APPLY_M", "MOU_APPLY_M.APPLY_TYPE", "MOU_APPLY_M.APPLY_NO", "MOU_APPLY_M.FINISHED_TAG",
        "INV_OCCUR_OUT_M", "INV_OCCUR_OUT_M.OCCUR_TYPE", "INV_OCCUR_OUT_M.OCCUR_NO",
        "INV_OCCUR_OUT_D", "INV_OCCUR_OUT_D.OCCUR_TYPE", "INV_OCCUR_OUT_D.OCCUR_NO",
        "INV_OCCUR_OUT_D.PRO_NO", "INV_OCCUR_OUT_D.DEPOT_ID", "INV_OCCUR_OUT_D.QTY",
    };

    private static ModuleEffectPlan CustomsPlan() => new(
        300301, "CUS_EXPORT_M", "CUS_EXPORT_D", "module-300301-v3",
        new[] { "EXPORT_TYPE", "EXPORT_NO" },
        Array.Empty<EffectActionPlan>(), Array.Empty<EffectValidationPlan>());

    [Fact]
    public void Field_copy_sourcing_a_column_missing_from_the_master_is_rejected()
    {
        var issues = EffectParamPhysicalValidator.Validate(
            "field-copy",
            """{"targetTable":"CUS_PRODUCT","field":"INSIDE_NAME","sourceField":"PRO_DESC"}""",
            CustomsPlan(), Columns);

        Assert.Single(issues);
        Assert.Contains("CUS_EXPORT_M.PRO_DESC", issues[0]);
    }

    [Fact]
    public void Field_copy_sourcing_an_existing_column_passes()
    {
        var issues = EffectParamPhysicalValidator.Validate(
            "field-copy",
            """{"targetTable":"CUS_PRODUCT","field":"INSIDE_NAME","sourceField":"EXPORT_NO"}""",
            CustomsPlan(), Columns);

        Assert.Empty(issues);
    }

    [Fact]
    public void Set_state_rejects_a_missing_state_column()
    {
        var issues = EffectParamPhysicalValidator.Validate(
            "set-state",
            """{"targetTable":"MOU_APPLY_M","stateField":"NOT_A_COLUMN","stateValue":1}""",
            CustomsPlan(), Columns);

        Assert.Single(issues);
        Assert.Contains("MOU_APPLY_M.NOT_A_COLUMN", issues[0]);
    }

    [Fact]
    public void Set_state_accepts_the_targets_shape()
    {
        var issues = EffectParamPhysicalValidator.Validate(
            "set-state",
            """{"targets":[{"table":"MOU_APPLY_M","refs":[{"target":"APPLY_NO","source":"EXPORT_NO"}]}],"state":{"FINISHED_TAG":1}}""",
            CustomsPlan(), Columns);

        Assert.Empty(issues);
    }

    [Fact]
    public void Malformed_parameters_are_reported_instead_of_thrown()
    {
        var issues = EffectParamPhysicalValidator.Validate("field-copy", "{not json", CustomsPlan(), Columns);

        Assert.Single(issues);
        Assert.Contains("不是合法 JSON", issues[0]);
    }

    [Fact]
    public void Parameters_without_configurable_references_are_covered_but_skipped()
    {
        Assert.True(EffectParamPhysicalValidator.IsCovered("balance-adjust"));
        Assert.True(EffectParamPhysicalValidator.IsCovered("field-copy"));
        Assert.False(EffectParamPhysicalValidator.IsCovered("completion-close-unregistered"));
        Assert.Empty(EffectParamPhysicalValidator.Validate(
            "balance-adjust", """{"bank":{"direction":"OUT"}}""", CustomsPlan(), Columns));
    }
}
