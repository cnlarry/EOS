using System.Text.Json;
using EOS.API.Data;
using EOS.API.Data.ValidationRules;
using EOS.API.Features.Assistant.Catalog;
using EOS.API.Features.Assistant.Tools;
using EOS.API.Models;
using EOS.API.Security;
using Microsoft.Extensions.Options;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 系统能力目录（describe_mechanism）：九个主题全覆盖、配置域主题的权限门、
/// 以及"目录里出现的标识必须来自真值注册表"。
/// </summary>
public sealed class SystemCapabilityCatalogTests
{
    private sealed class FakeSchema(IReadOnlyList<FieldAdminModule> modules) : IAssistantSchemaGateway
    {
        public Task<IReadOnlyList<FieldAdminModule>> ListModulesAsync(CancellationToken token) =>
            Task.FromResult(modules);

        public Task<IReadOnlyList<FieldAdminTable>> ListTablesAsync(string? kind, CancellationToken token) =>
            Task.FromResult<IReadOnlyList<FieldAdminTable>>([new("CLIENT", "客户", "P", "TABLE", 2, 0, 0)]);

        public Task<(FieldAdminTableDetail? Table, IReadOnlyList<FieldAdminFieldSummary> Fields, IReadOnlyList<FieldAdminUnmanagedField> Unmanaged)> DescribeTableAsync(
            string tableId, CancellationToken token) => throw new NotSupportedException();

        public Task<IReadOnlyList<AssistantSchemaView>> ListViewsAsync(string? keyword, CancellationToken token) =>
            Task.FromResult<IReadOnlyList<AssistantSchemaView>>([]);

        public Task<IReadOnlyList<AssistantSchemaProcedure>> ListProceduresAsync(string? keyword, CancellationToken token) =>
            Task.FromResult<IReadOnlyList<AssistantSchemaProcedure>>([]);
    }

    private sealed class FakePermissions(bool canSetup) : IPermissionService
    {
        public Task<ModulePermission> GetAsync(string userId, int moduleId, CancellationToken cancellationToken) =>
            Task.FromResult(new ModulePermission(Rights(canSetup)));

        public Task<ModulePermission> RequireAsync(string userId, int moduleId, PermissionAction action, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private static ModuleRights Rights(bool canSetup) => new(
        CanBrowse: true, CanViewCost: false, CanViewSecrecy: false, CanSetup: canSetup,
        DeniedMasterFields: new HashSet<string>(), DeniedDetailFields: new HashSet<string>(),
        CanAddNew: false, CanEdit: false, CanDelete: false, CanApprove: false, CanDeapprove: false,
        CanEndCase: false, CanUnEndCase: false, CanFileView: false, CanFileUpda: false,
        CanFileEdit: false, CanFileDele: false, DenyNewMasterFields: new HashSet<string>(),
        DenyNewDetailFields: new HashSet<string>(), DenyModiMasterFields: new HashSet<string>(),
        DenyModiDetailFields: new HashSet<string>(), DataFilter: string.Empty, ExecuteTag: "A");

    private static readonly IReadOnlyList<FieldAdminModule> Modules =
        [new(1201, "产品"), new(2302, "实施配置"), new(2306, "运维配置")];

    private static SystemCapabilityCatalog Catalog(IReadOnlyList<FieldAdminModule>? modules = null) =>
        new(new FakeSchema(modules ?? Modules), Options.Create(new UnifiedFormEditorSettings
        {
            EnabledModuleIds = [1201],
            ReadOnlyModuleIds = [2302, 2306],
        }));

    private static DescribeMechanismTool Tool(bool canSetup) => new(Catalog(), new FakePermissions(canSetup));

    private static JsonElement Args(string topic) =>
        JsonSerializer.SerializeToElement(new { topic });

    public static TheoryData<CapabilityTopic> AllTopics() => new()
    {
        CapabilityTopic.Workbench,
        CapabilityTopic.Form,
        CapabilityTopic.Fields,
        CapabilityTopic.Chooser,
        CapabilityTopic.Report,
        CapabilityTopic.Module,
        CapabilityTopic.Effect,
        CapabilityTopic.Validation,
        CapabilityTopic.Endpoint,
    };

    [Theory]
    [MemberData(nameof(AllTopics))]
    public async Task 九个主题都能给出机制解释并标注真值来源(CapabilityTopic topic)
    {
        var result = await Tool(canSetup: true).ExecuteAsync("u1", Args(topic.ToString()), CancellationToken.None);

        Assert.True(result.Ok, $"{topic} 应当可解释：{result.ContentForModel}");
        Assert.Contains("真值来源", result.ContentForModel, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(AllTopics))]
    public async Task 每个主题都产出一份可核对的标识或如实说明无标识(CapabilityTopic topic)
    {
        var explanation = await Catalog().DescribeAsync(topic, CancellationToken.None);

        Assert.Equal(topic, explanation.Topic);
        Assert.False(string.IsNullOrWhiteSpace(explanation.Text));
        // 工作台主题讲的是"界面形态怎么来的"，没有可枚举的实例标识；其余主题必须带事实清单。
        if (topic == CapabilityTopic.Workbench) Assert.Empty(explanation.Facts);
        else Assert.NotEmpty(explanation.Facts);
    }

    public static TheoryData<CapabilityTopic> SetupGatedTopics() => new()
    {
        CapabilityTopic.Effect,
        CapabilityTopic.Validation,
        CapabilityTopic.Endpoint,
    };

    public static TheoryData<CapabilityTopic> OpenTopics() => new()
    {
        CapabilityTopic.Workbench,
        CapabilityTopic.Form,
        CapabilityTopic.Fields,
        CapabilityTopic.Chooser,
        CapabilityTopic.Report,
        CapabilityTopic.Module,
    };

    [Theory]
    [MemberData(nameof(SetupGatedTopics))]
    public async Task 配置域主题_无CanSetup一律拒绝(CapabilityTopic topic)
    {
        var result = await Tool(canSetup: false).ExecuteAsync("u1", Args(topic.ToString()), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("配置维护权限", result.ContentForModel, StringComparison.Ordinal);
        Assert.Contains("没有该权限", result.ContentForModel, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(SetupGatedTopics))]
    public async Task 配置域主题_有CanSetup可查(CapabilityTopic topic)
    {
        var result = await Tool(canSetup: true).ExecuteAsync("u1", Args(topic.ToString()), CancellationToken.None);

        Assert.True(result.Ok);
    }

    [Theory]
    [MemberData(nameof(OpenTopics))]
    public async Task 机制类主题_不需要配置维护权限(CapabilityTopic topic)
    {
        var result = await Tool(canSetup: false).ExecuteAsync("u1", Args(topic.ToString()), CancellationToken.None);

        Assert.True(result.Ok);
    }

    [Fact]
    public void 配置域主题集合恰好是效果链与校验规则与端点()
    {
        Assert.Equal(
            new[] { CapabilityTopic.Effect, CapabilityTopic.Validation, CapabilityTopic.Endpoint },
            SystemCapabilityCatalog.Topics.Where(SystemCapabilityCatalog.RequiresSetup).ToArray());
    }

    [Fact]
    public async Task 未知主题如实拒绝并列出可选项()
    {
        var result = await Tool(canSetup: true).ExecuteAsync("u1", Args("payroll"), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("未知的机制主题", result.ContentForModel, StringComparison.Ordinal);
        Assert.Contains("workbench", result.ContentForModel, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 主题大小写不敏感()
    {
        var result = await Tool(canSetup: true).ExecuteAsync("u1", Args("WORKBENCH"), CancellationToken.None);

        Assert.True(result.Ok);
    }

    [Fact]
    public async Task 效果链事实逐条落在效果键注册表内()
    {
        var explanation = await Catalog().DescribeAsync(CapabilityTopic.Effect, CancellationToken.None);

        var keys = explanation.Facts.Where(fact => fact.Kind == CapabilityFactKind.EffectKey).ToList();
        Assert.NotEmpty(keys);
        Assert.All(keys, fact => Assert.True(
            BusinessActionCatalog.IsKnownEffectKey(fact.Value),
            $"效果键 {fact.Value} 不在效果键闭集内"));
        Assert.All(keys, fact => Assert.True(
            EOS.API.Data.Effects.EffectRegistry.Keys.ContainsKey(fact.Value),
            $"效果键 {fact.Value} 不在执行注册表内"));
    }

    [Fact]
    public async Task 校验规则事实逐条落在校验注册表内()
    {
        var explanation = await Catalog().DescribeAsync(CapabilityTopic.Validation, CancellationToken.None);

        var keys = explanation.Facts.Where(fact => fact.Kind == CapabilityFactKind.ValidationKey).ToList();
        Assert.NotEmpty(keys);
        Assert.All(keys, fact => Assert.True(
            ValidationRuleRegistry.IsKnownKey(fact.Value), $"校验键 {fact.Value} 不在校验模板闭集内"));
    }

    [Fact]
    public async Task 选择器数据源事实逐条落在服务端注册表内()
    {
        var explanation = await Catalog().DescribeAsync(CapabilityTopic.Chooser, CancellationToken.None);

        var keys = explanation.Facts.Where(fact => fact.Kind == CapabilityFactKind.SourceKey).ToList();
        Assert.NotEmpty(keys);
        Assert.All(keys, fact => Assert.True(
            ChooserRepository.IsRegistered(fact.Value), $"数据源键 {fact.Value} 未在服务端注册"));
    }

    [Fact]
    public async Task 表单主题的模块事实只含模块元数据里存在的模块()
    {
        var explanation = await Catalog().DescribeAsync(CapabilityTopic.Form, CancellationToken.None);

        var known = Modules.Select(module => module.Id.ToString()).ToHashSet(StringComparer.Ordinal);
        var facts = explanation.Facts.Where(fact => fact.Kind == CapabilityFactKind.Module).ToList();
        Assert.NotEmpty(facts);
        Assert.All(facts, fact => Assert.Contains(fact.Value, known));
    }

    [Fact]
    public async Task 模块主题的模块事实含代码具名常量与元数据模块()
    {
        var explanation = await Catalog().DescribeAsync(CapabilityTopic.Module, CancellationToken.None);

        var facts = explanation.Facts.Where(fact => fact.Kind == CapabilityFactKind.Module).ToList();
        Assert.Contains(Modules[0].Id.ToString(), facts.Select(fact => fact.Value));
        Assert.Contains(ModuleIds.BomStructure.ToString(), facts.Select(fact => fact.Value));
    }

    [Fact]
    public async Task 报表聚合编号符合报表编号规范()
    {
        var explanation = await Catalog().DescribeAsync(CapabilityTopic.Report, CancellationToken.None);

        var facts = explanation.Facts.Where(fact => fact.Kind == CapabilityFactKind.ReportId).ToList();
        Assert.NotEmpty(facts);
        Assert.All(facts, fact => Assert.Matches("^[A-Za-z0-9_-]{3,40}$", fact.Value));
    }

    [Fact]
    public async Task 端点事实由程序集反射装配且都是真实路由()
    {
        var explanation = await Catalog().DescribeAsync(CapabilityTopic.Endpoint, CancellationToken.None);

        var routes = explanation.Facts.Where(fact => fact.Kind == CapabilityFactKind.Route)
            .Select(fact => fact.Value).ToList();
        Assert.NotEmpty(routes);
        Assert.All(routes, route => Assert.Matches(@"^[A-Z|]+ /api/v1/", route));
        Assert.Contains(routes, route => route.Contains("/api/v1/assistant", StringComparison.Ordinal));
    }
}
