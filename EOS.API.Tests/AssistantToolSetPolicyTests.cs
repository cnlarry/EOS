using EOS.API.Features.Assistant.Tools;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 工具子集只决定"这一页把哪些工具摆到模型面前"，不是权限判定；
/// 页面类型未知时不裁剪，避免在不确定时让模型少一条路。
/// </summary>
public sealed class AssistantToolSetPolicyTests
{
    [Theory]
    [InlineData("config-fields")]
    [InlineData("config-datasource")]
    [InlineData("config-buttons")]
    [InlineData("config-effect")]
    public void Config_Page_Hides_Record_And_Document_Action_Tools(string pageType)
    {
        Assert.False(AssistantToolSetPolicy.IsOffered(ApplyRecordActionTool.ToolName, pageType));
        Assert.False(AssistantToolSetPolicy.IsOffered(PreviewRecordActionTool.ToolName, pageType));
        Assert.False(AssistantToolSetPolicy.IsOffered(ApplyDocumentActionTool.ToolName, pageType));
        Assert.False(AssistantToolSetPolicy.IsOffered(PreviewBatchDecisionTool.ToolName, pageType));

        Assert.True(AssistantToolSetPolicy.IsOffered(ApplyConfigChangeTool.ToolName, pageType));
        Assert.True(AssistantToolSetPolicy.IsOffered(DescribeModuleTool.ToolName, pageType));
        Assert.True(AssistantToolSetPolicy.IsOffered(SearchRecordsTool.ToolName, pageType));
    }

    [Theory]
    [InlineData("list")]
    [InlineData("view")]
    [InlineData("edit")]
    [InlineData("new")]
    [InlineData("copy")]
    public void Business_Page_Hides_Config_And_Schema_Admin_Tools(string pageType)
    {
        Assert.False(AssistantToolSetPolicy.IsOffered(ApplyConfigChangeTool.ToolName, pageType));
        Assert.False(AssistantToolSetPolicy.IsOffered(CloneModuleConfigTool.ToolName, pageType));
        Assert.False(AssistantToolSetPolicy.IsOffered(ListProceduresTool.ToolName, pageType));
        Assert.False(AssistantToolSetPolicy.IsOffered(ApplyChangeSetTool.ToolName, pageType));

        Assert.True(AssistantToolSetPolicy.IsOffered(ApplyRecordActionTool.ToolName, pageType));
        Assert.True(AssistantToolSetPolicy.IsOffered(ApplyDocumentActionTool.ToolName, pageType));
        Assert.True(AssistantToolSetPolicy.IsOffered(SearchRecordsTool.ToolName, pageType));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("dashboard")]
    [InlineData("unknown-page")]
    public void Unknown_Page_Does_Not_Trim(string? pageType)
    {
        Assert.True(AssistantToolSetPolicy.IsOffered(ApplyConfigChangeTool.ToolName, pageType));
        Assert.True(AssistantToolSetPolicy.IsOffered(ApplyRecordActionTool.ToolName, pageType));
        Assert.True(AssistantToolSetPolicy.IsOffered(ListProceduresTool.ToolName, pageType));
    }
}
