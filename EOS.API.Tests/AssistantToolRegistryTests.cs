using System.Text.Json;
using EOS.API.Features.Assistant.ModelAccess;
using EOS.API.Features.Assistant.Parameters;
using EOS.API.Features.Assistant.Tools;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 能力面关闭的**双向性**（ADR-030 §7.3）：关掉一个工具，既不能把它发给模型，
/// 也不能让它被调用。
///
/// <para>
/// 只做前一半的话，开关是装饰——模型"幻觉"出一个已关闭的工具名仍然能执行到它。
/// 这条测试专门盯那一半。
/// </para>
/// </summary>
public sealed class AssistantToolRegistryTests
{
    private sealed class StubTool(string name) : AssistantToolBase
    {
        public override string Name { get; } = name;

        public override AssistantToolRisk Risk => AssistantToolRisk.Read;

        public override string Description => $"stub {Name}";

        public override string ParametersJson => """{"type":"object","properties":{}}""";

        public override Task<ToolExecutionResult> ExecuteAsync(
            string userId, JsonElement arguments, CancellationToken token) =>
            Task.FromResult(ToolExecutionResult.Deny("stub：本用例不执行工具"));
    }

    private sealed class StubRuntime(AssistantPolicyValues policy) : IAssistantRuntimeConfig
    {
        public AssistantRuntimeSnapshot Current { get; } = new(null, new AssistantSettings()) { Policy = policy };
    }

    private static AssistantPolicyValues WithDisabled(params string[] toolNames)
    {
        var capability = new AssistantCapabilityOptions();
        foreach (var name in toolNames) capability.DisabledTools.Add(name);
        return AssistantPolicyValues.Default with { Capability = capability };
    }

    private static AssistantToolRegistry Registry(params string[] disabled) =>
        new(
            [new StubTool("search_records"), new StubTool("describe_module")],
            new StubRuntime(WithDisabled(disabled)));

    [Fact]
    public void Disabled_Tool_Is_Not_Offered_To_The_Model()
    {
        var registry = Registry("search_records");

        Assert.DoesNotContain(registry.Definitions, definition => definition.Name == "search_records");
        // 没被关的照常下发
        Assert.Contains(registry.Definitions, definition => definition.Name == "describe_module");
    }

    [Fact]
    public void Disabled_Tool_Cannot_Be_Invoked()
    {
        var registry = Registry("search_records");

        Assert.False(registry.TryGet("search_records", out _));
        // 但它是"管理员关掉的"，不是"没人认得"——调用方要能分辨这两件事
        Assert.True(registry.IsDisabledByParameter("search_records"));
        Assert.False(registry.IsDisabledByParameter("never_heard_of_it"));
        Assert.False(registry.IsDisabledByParameter("describe_module"));
    }

    [Fact]
    public void All_Tools_Are_Available_Without_Capability_Parameters()
    {
        // 单测里通常直接构造注册表（不注入运行期配置）：等于"没有关过任何东西"
        var registry = new AssistantToolRegistry([new StubTool("search_records")]);

        Assert.Single(registry.Definitions);
        Assert.True(registry.TryGet("search_records", out _));
        Assert.False(registry.IsDisabledByParameter("search_records"));
    }
}
