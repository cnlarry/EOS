using System.Text.Json;
using EOS.API.Data;
using EOS.API.Data.Effects;
using EOS.API.Data.Effects.ServiceEffectHandlers;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// Unit (no DB) tests for the employee-contract-sync service handler:
/// fail-closed parameter validation, the clear-then-backfill statements with
/// include/exclude-self membership, and the recompute-excluding-self reverse
/// kind registration (decision H4).
/// </summary>
public class ContractSyncHandlerTests
{
    private static ModuleEffectPlan Plan() => new(
        180106, "HR_CONTRACT_M", "HR_CONTRACT_D", "v-test",
        new[] { "CONT_TYPE", "CONT_NO" },
        Array.Empty<EffectActionPlan>(), Array.Empty<EffectValidationPlan>());

    private static ISet<string> Columns() => new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "HR_EMPLOYEE", "HR_EMPLOYEE.EMP_ID", "HR_EMPLOYEE.CONTRACT_DATE",
        "HR_EMPLOYEE.CONTRACT_BEGIN_DATE", "HR_EMPLOYEE.CONTRACT_NO",
        "HR_CONTRACT_M", "HR_CONTRACT_M.CONT_TYPE", "HR_CONTRACT_M.CONT_NO", "HR_CONTRACT_M.CONFIRM_TAG",
        "HR_CONTRACT_D", "HR_CONTRACT_D.CONT_TYPE", "HR_CONTRACT_D.CONT_NO", "HR_CONTRACT_D.EMP_ID",
        "HR_CONTRACT_D.BEGIN_DATE", "HR_CONTRACT_D.END_DATE", "HR_CONTRACT_D.CONTRACT_NO",
    };

    private const string LandedParamsJson =
        """{"targetTable":"HR_EMPLOYEE","fields":["CONTRACT_DATE","CONTRACT_BEGIN_DATE","CONTRACT_NO"],"source":"HR_CONTRACT_D/M","includeSelfOnApprove":true}""";

    private static ContractSyncSpec ParseLanded() =>
        ContractSyncSpec.Parse(JsonDocument.Parse(LandedParamsJson).RootElement, Plan(), Columns());

    [Fact]
    public void EffectKey_RegisteredAsService()
    {
        Assert.Equal("employee-contract-sync", new ContractSyncHandler().EffectKey);
        Assert.True(EffectRegistry.IsImplemented("employee-contract-sync"));
    }

    [Fact]
    public void ReverseKind_RecomputeExcludingSelf_IsRegistered()
    {
        Assert.Empty(EffectStructSchemas.ValidateReverse("""{"kind":"recompute-excluding-self"}"""));
    }

    [Fact]
    public void Parse_AcceptsLandedParams()
    {
        var spec = ParseLanded();
        Assert.Equal("HR_EMPLOYEE", spec.TargetTable, ignoreCase: true);
        Assert.Equal("HR_CONTRACT_M", spec.MasterTable, ignoreCase: true);
        Assert.Equal("HR_CONTRACT_D", spec.DetailTable, ignoreCase: true);
    }

    [Fact]
    public void Parse_RejectsBadConfigurations()
    {
        var plan = Plan();
        var columns = Columns();
        // Unknown target / source shapes.
        Assert.Throws<EffectConfigException>(() => ContractSyncSpec.Parse(JsonDocument.Parse(
            """{"targetTable":"HR_CONTRACT_M","fields":["CONTRACT_DATE","CONTRACT_BEGIN_DATE","CONTRACT_NO"],"source":"HR_CONTRACT_D/M","includeSelfOnApprove":true}""").RootElement, plan, columns));
        Assert.Throws<EffectConfigException>(() => ContractSyncSpec.Parse(JsonDocument.Parse(
            """{"targetTable":"HR_EMPLOYEE","fields":["CONTRACT_DATE","CONTRACT_BEGIN_DATE"],"source":"HR_CONTRACT_D/M","includeSelfOnApprove":true}""").RootElement, plan, columns));
        Assert.Throws<EffectConfigException>(() => ContractSyncSpec.Parse(JsonDocument.Parse(
            """{"targetTable":"HR_EMPLOYEE","fields":["CONTRACT_DATE","CONTRACT_BEGIN_DATE","CONTRACT_NO"],"source":"HR_CONTRACT_M","includeSelfOnApprove":true}""").RootElement, plan, columns));
        // Approve must include self (legacy semantics).
        Assert.Throws<EffectConfigException>(() => ContractSyncSpec.Parse(JsonDocument.Parse(
            """{"targetTable":"HR_EMPLOYEE","fields":["CONTRACT_DATE","CONTRACT_BEGIN_DATE","CONTRACT_NO"],"source":"HR_CONTRACT_D/M","includeSelfOnApprove":false}""").RootElement, plan, columns));
        // Missing physical objects fail closed.
        Assert.Throws<EffectConfigException>(() => ContractSyncSpec.Parse(JsonDocument.Parse(
            LandedParamsJson).RootElement, plan,
            new HashSet<string>(columns.Where(item => item != "HR_EMPLOYEE.CONTRACT_NO"), StringComparer.OrdinalIgnoreCase)));
        Assert.Throws<EffectConfigException>(() => ContractSyncSpec.Parse(JsonDocument.Parse(
            LandedParamsJson).RootElement, plan,
            new HashSet<string>(columns.Where(item => item != "HR_CONTRACT_M.CONFIRM_TAG"), StringComparer.OrdinalIgnoreCase)));
    }

    [Fact]
    public void BuildStatements_Approve_IncludesSelfInConfirmedSet()
    {
        var statements = ContractSyncHandler.BuildStatements(ParseLanded(), includeSelf: true);
        Assert.Equal(2, statements.Count);
        // Clear only this document's employees.
        Assert.Contains("[CONTRACT_DATE] = NULL", statements[0]);
        Assert.Contains("HR_CONTRACT_D", statements[0]);
        // Backfill aggregates max() over confirmed contracts including this one.
        Assert.Contains("MAX(d.[END_DATE])", statements[1]);
        Assert.Contains("MAX(d.[BEGIN_DATE])", statements[1]);
        Assert.Contains("MAX(RTRIM(d.[CONTRACT_NO]))", statements[1]);
        Assert.Contains("m.CONFIRM_TAG = 1 OR (m.CONT_TYPE = @ct AND m.CONT_NO = @cn)", statements[1]);
        Assert.Contains("GROUP BY d.[EMP_ID]", statements[1]);
    }

    [Fact]
    public void BuildStatements_Deapprove_ExcludesSelfFromConfirmedSet()
    {
        var statements = ContractSyncHandler.BuildStatements(ParseLanded(), includeSelf: false);
        Assert.Equal(2, statements.Count);
        // Same clear step; backfill excludes this document.
        Assert.Contains("[CONTRACT_DATE] = NULL", statements[0]);
        Assert.Contains("m.CONFIRM_TAG = 1 AND NOT (m.CONT_TYPE = @ct AND m.CONT_NO = @cn)", statements[1]);
    }
}
