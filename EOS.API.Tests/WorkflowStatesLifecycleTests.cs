using EOS.API.Data;
using Xunit;

namespace EOS.API.Tests;

public class WorkflowStatesLifecycleTests
{
    [Fact]
    public void LifecycleColumns_ContainFullTenColumnSet()
    {
        Assert.Equal(
            new[]
            {
                "CREATE_PERSON", "CREATE_DATE", "LAST_UPDATE_BY", "LAST_UPDATE_DATE",
                "CONFIRM_TAG", "CONFIRM_PERSON", "CONFIRM_DATE",
                "FINISHED_TAG", "FINISHED_PERSON", "FINISHED_DATE",
            },
            WorkflowStates.LifecycleColumns);
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
        // 载荷持有列 = 经办列（单点定义）
        Assert.Equal(
            WorkflowStates.LifecycleActorColumns.OrderBy(column => column),
            RecordPayloadValidator.AuditColumns.OrderBy(column => column));
    }

    [Theory]
    [InlineData("CONFIRM_TAG", true)]
    [InlineData("finished_date", true)]
    [InlineData("OWNER", false)]
    [InlineData("OWNER_G", false)]
    [InlineData("CI", false)]
    [InlineData("REMARK", false)]
    [InlineData("CLIENT_ID", false)]
    public void IsLifecycleColumn_MatchesColumnSet(string fieldId, bool expected)
    {
        Assert.Equal(expected, WorkflowStates.IsLifecycleColumn(fieldId));
    }

    [Theory]
    [InlineData(true, false, false, null, true)]
    [InlineData(false, true, false, null, true)]
    [InlineData(false, false, true, null, true)]
    [InlineData(false, false, false, new[] { "APPROVE_EFFECT" }, true)]
    [InlineData(false, false, false, new[] { "DEAPPROVE" }, true)]
    [InlineData(false, false, false, new[] { "approve_effect" }, true)]
    [InlineData(false, false, false, new[] { "SAVE" }, false)]
    [InlineData(false, false, false, new[] { "ENDCASE" }, false)]
    [InlineData(false, false, false, new[] { "UNKNOWN_EVENT" }, false)]
    [InlineData(false, false, false, null, false)]
    [InlineData(false, false, false, new string[] { }, false)]
    public void NeedsApproveColumn_FollowsBackendCapability(
        bool approveEnabled, bool autoApprove, bool hasWorkflow, string[]? events, bool expected)
    {
        Assert.Equal(expected, WorkflowStates.NeedsApproveColumn(approveEnabled, autoApprove, hasWorkflow, events));
    }

    [Theory]
    [InlineData(true, false, false, true)]   // 模块声明「启用批核」（ADR-035：替代"效果引擎接管"那一维）
    [InlineData(false, true, false, true)]   // 自动批核
    [InlineData(false, false, true, true)]   // 已配置流程
    [InlineData(false, false, false, false)] // 三者皆无
    public void HasApproveCapability_KeepsFlowConfigurationEntry(
        bool approveEnabled, bool autoApprove, bool hasFlow, bool expected)
    {
        Assert.Equal(expected, WorkflowStates.HasApproveCapability(approveEnabled, autoApprove, hasFlow));
    }

    // 判据只认"无流程、无批核效果链"：**与 AUTO_APPROVE 无关，也与引擎开关无关**。
    // 曾经把它绑在自动批核上（关掉自动批核会让手工批核入口一起消失，服务端 404
    // WORKFLOW_NOT_SUPPORTED）；ADR-035 再把"引擎开关"那一维换成"有没有批核效果链"——
    // 批核本事就是翻状态位，有副作用才是特例。
    [Theory]
    [InlineData(false, false, true)]   // 无流程、无批核效果链 ⇒ 纯状态翻转可批核
    [InlineData(false, true, false)]   // 有流程 ⇒ 走送审
    [InlineData(true, false, false)]   // 有批核效果链 ⇒ 走效果链，不属"无副作用"
    [InlineData(true, true, false)]
    public void IsStatelessApproveCapable_OnlyRequiresNoFlowNoEffects(
        bool hasApproveEffects, bool hasFlow, bool expected)
    {
        Assert.Equal(expected, WorkflowStates.IsStatelessApproveCapable(hasApproveEffects, hasFlow));
    }
}
