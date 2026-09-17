using EOS.API.Data;
using Xunit;

namespace EOS.API.Tests;

public class WorkflowStatesLifecycleTests
{
    [Fact]
    public void LifecycleColumns_ContainFullThirteenColumnSet()
    {
        Assert.Equal(
            new[]
            {
                "CREATE_PERSON", "CREATE_DATE", "LAST_UPDATE_BY", "LAST_UPDATE_DATE",
                "CONFIRM_TAG", "CONFIRM_PERSON", "CONFIRM_DATE",
                "FINISHED_TAG", "FINISHED_PERSON", "FINISHED_DATE",
                "OWNER", "OWNER_G", "CI",
            },
            WorkflowStates.LifecycleColumns);
        Assert.Equal(["CI", "OWNER", "OWNER_G"], WorkflowStates.OwnershipColumns);
    }

    [Fact]
    public void LifecycleSubsets_AreConsistent()
    {
        Assert.Equal(["CONFIRM_TAG", "FINISHED_TAG"], WorkflowStates.LifecycleTagColumns);
        Assert.Equal(8, WorkflowStates.LifecycleActorColumns.Length);
        Assert.Equal(
            ["CONFIRM_TAG", "CONFIRM_PERSON", "CONFIRM_DATE", "FINISHED_TAG", "FINISHED_PERSON", "FINISHED_DATE"],
            WorkflowStates.RecordStatusColumns);
        // 读取契约是全集的子集；状态位与经办列不相交
        foreach (var column in WorkflowStates.RecordStatusColumns)
        {
            Assert.Contains(column, WorkflowStates.LifecycleColumns);
        }
        Assert.Empty(WorkflowStates.LifecycleTagColumns.Intersect(WorkflowStates.LifecycleActorColumns));
        // 载荷持有列 = 经办列 + 归属三列（单点定义）
        Assert.Equal(
            WorkflowStates.LifecycleActorColumns.Concat(WorkflowStates.OwnershipColumns).OrderBy(column => column),
            RecordPayloadValidator.AuditColumns.OrderBy(column => column));
    }

    [Theory]
    [InlineData("CONFIRM_TAG", true)]
    [InlineData("finished_date", true)]
    [InlineData("OWNER", true)]
    [InlineData("OWNER_G", true)]
    [InlineData("REMARK", false)]
    [InlineData("CLIENT_ID", false)]
    public void IsLifecycleColumn_MatchesColumnSet(string fieldId, bool expected)
    {
        Assert.Equal(expected, WorkflowStates.IsLifecycleColumn(fieldId));
    }

    [Theory]
    [InlineData(true, false, false, false, null, true)]
    [InlineData(false, true, false, false, null, true)]
    [InlineData(false, false, true, false, null, true)]
    [InlineData(false, false, false, true, null, true)]
    [InlineData(false, false, false, false, new[] { "APPROVE_EFFECT" }, true)]
    [InlineData(false, false, false, false, new[] { "DEAPPROVE" }, true)]
    [InlineData(false, false, false, false, new[] { "approve_effect" }, true)]
    [InlineData(false, false, false, false, new[] { "SAVE" }, false)]
    [InlineData(false, false, false, false, new[] { "ENDCASE" }, false)]
    [InlineData(false, false, false, false, new[] { "UNKNOWN_EVENT" }, false)]
    [InlineData(false, false, false, false, null, false)]
    [InlineData(false, false, false, false, new string[] { }, false)]
    public void NeedsApproveColumn_FollowsBackendCapability(
        bool autoApprove, bool hasSproc, bool effectEnabled, bool hasWorkflow, string[]? events, bool expected)
    {
        Assert.Equal(expected, WorkflowStates.NeedsApproveColumn(autoApprove, hasSproc, effectEnabled, hasWorkflow, events));
    }

    [Theory]
    [InlineData(true, false, null, false, false, true)]   // 自动批核
    [InlineData(false, true, null, false, false, true)]   // 静态登记批核过程
    [InlineData(false, false, "P_WF_X", false, false, true)] // MODULES.UPDATE_SP 登记
    [InlineData(false, false, "   ", false, false, false)]   // 空白不算登记
    [InlineData(false, false, null, true, false, true)]   // 效果引擎接管
    [InlineData(false, false, null, false, true, true)]   // 已配置流程
    [InlineData(false, false, null, false, false, false)] // 四者皆无
    public void HasApproveCapability_KeepsFlowConfigurationEntry(
        bool autoApprove, bool hasSproc, string? updateSproc, bool effectEnabled, bool hasFlow, bool expected)
    {
        Assert.Equal(expected, WorkflowStates.HasApproveCapability(autoApprove, hasSproc, updateSproc, effectEnabled, hasFlow));
    }

    [Theory]
    [InlineData(true, false, false, false, true)]
    [InlineData(true, true, false, false, false)]
    [InlineData(true, false, true, false, false)]
    [InlineData(true, false, false, true, false)]
    [InlineData(false, false, false, false, false)]
    public void IsStatelessApproveCapable_RequiresAutoWithoutSideEffects(
        bool autoApprove, bool hasSproc, bool effectEnabled, bool hasFlow, bool expected)
    {
        Assert.Equal(expected, WorkflowStates.IsStatelessApproveCapable(autoApprove, hasSproc, effectEnabled, hasFlow));
    }
}
