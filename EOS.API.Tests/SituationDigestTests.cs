using EOS.API.Features.Assistant.Diagnosis;
using EOS.API.Features.Assistant.Situation;
using EOS.API.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 打开即见的结构化摘要：**零模型调用**（结构性断言）、主来源是超期滞留、
/// 规则与列表同源（数据范围与权限门都生效）、覆盖率缺口如实登记。
/// </summary>
public sealed class SituationDigestTests
{
    private static SituationDigestService CreateService(
        AssistantSituationDoubles.FakeGateway gateway,
        AssistantSituationDoubles.FakePermissions permissions,
        AssistantSituationDoubles.FakeFacts facts,
        Action<AssistantSituationBudgetOptions>? configure = null,
        AssistantSituationDoubles.FakeBlockedProbe? blockedProbe = null)
    {
        var budget = AssistantSituationDoubles.Budget(configure);
        var situation = new AssistantSituationService(
            AssistantSituationDoubles.UserContext(), facts, budget, NullLogger<AssistantSituationService>.Instance);
        return new SituationDigestService(
            gateway, permissions, facts, situation,
            blockedProbe ?? new AssistantSituationDoubles.FakeBlockedProbe(),
            budget, NullLogger<SituationDigestService>.Instance);
    }

    private static Dictionary<string, object?> OverdueRow(string key, DateTime created) => new()
    {
        ["DD_NO"] = key,
        ["CREATE_DATE"] = created,
        ["CONFIRM_TAG"] = false,
    };

    [Fact]
    public async Task 主来源是超期滞留且查询走生命周期列与数据范围()
    {
        var gateway = new AssistantSituationDoubles.FakeGateway();
        gateway.Definitions[1606] = AssistantSituationDoubles.Definition(
            1606, "客户订单", ["DD_NO", "CREATE_DATE", "CONFIRM_TAG"], ["DD_NO"]);
        gateway.Rows[1606] = new WorkbenchData(
            [OverdueRow("DD2608001", DateTime.UtcNow.Date.AddDays(-20))], 3, 1, 5);
        var permissions = new AssistantSituationDoubles.FakePermissions();
        permissions.Browsable.Add(1606);
        var facts = new AssistantSituationDoubles.FakeFacts();
        facts.ActivityModules.Add(1606);
        var service = CreateService(gateway, permissions, facts);

        var digest = await service.BuildAsync("u1", SituationContext.Empty, CancellationToken.None);

        var item = Assert.Single(digest.Items);
        Assert.Equal(SituationDigestKinds.Overdue, item.Kind);
        Assert.Equal("客户订单", item.ModuleTitle);
        Assert.Equal("DD2608001", item.Key);
        Assert.Equal(20, item.AgeDays);
        Assert.Contains("已录入 20 天仍未批核", item.Reason);
        Assert.Contains("共 3 条同类滞留", item.Reason);

        // 查询条件来自生命周期列，并按该用户的数据范围下发（与列表同源）
        var query = Assert.Single(gateway.RowQueries);
        Assert.Equal(1606, query.ModuleId);
        Assert.NotNull(query.Query);
        Assert.Contains(query.Query!.Conditions, condition => condition.Field == "CREATE_DATE" && condition.Operator == "lte");
        Assert.Contains(query.Query.Conditions, condition => condition.Field == "CONFIRM_TAG" && condition.Value == "0");
    }

    [Fact]
    public async Task 待办计数为零时摘要不出现空的零条目()
    {
        var gateway = new AssistantSituationDoubles.FakeGateway();
        gateway.Definitions[1606] = AssistantSituationDoubles.Definition(
            1606, "客户订单", ["DD_NO", "CREATE_DATE", "CONFIRM_TAG"], ["DD_NO"]);
        gateway.Rows[1606] = new WorkbenchData([], 0, 1, 5);
        var permissions = new AssistantSituationDoubles.FakePermissions();
        permissions.Browsable.Add(1606);
        var facts = new AssistantSituationDoubles.FakeFacts();
        facts.ActivityModules.Add(1606);
        var service = CreateService(gateway, permissions, facts);

        var digest = await service.BuildAsync("u1", SituationContext.Empty, CancellationToken.None);

        Assert.Empty(digest.Items);
        // 不产生"待我审批 0 条"这类空摘要，而是如实说明待办计数当前为 0 的原因
        Assert.Contains(digest.Caveats, caveat => caveat.Contains("审批流未在运行"));
    }

    [Fact]
    public async Task 无权浏览的模块被跳过且不进入查询()
    {
        var gateway = new AssistantSituationDoubles.FakeGateway();
        gateway.Definitions[1606] = AssistantSituationDoubles.Definition(
            1606, "客户订单", ["DD_NO", "CREATE_DATE", "CONFIRM_TAG"], ["DD_NO"]);
        var permissions = new AssistantSituationDoubles.FakePermissions(); // 无权
        var facts = new AssistantSituationDoubles.FakeFacts();
        facts.ActivityModules.Add(1606);
        var service = CreateService(gateway, permissions, facts);

        var digest = await service.BuildAsync("u1", SituationContext.Empty, CancellationToken.None);

        Assert.Empty(digest.Items);
        Assert.Empty(gateway.RowQueries);
    }

    [Fact]
    public async Task 最近被拒事件补上模块名并计入摘要()
    {
        var gateway = new AssistantSituationDoubles.FakeGateway();
        gateway.Modules.Add(new SystemKnowledgeModule(1606, "客户订单"));
        var permissions = new AssistantSituationDoubles.FakePermissions();
        var facts = new AssistantSituationDoubles.FakeFacts();
        facts.Failures.Add(new SituationFailureFact(
            DateTime.UtcNow.AddHours(-1), "DELETEv", 1606, "无按钮授权", "PERMISSION|DENY"));
        var service = CreateService(gateway, permissions, facts);

        var digest = await service.BuildAsync("u1", SituationContext.Empty, CancellationToken.None);

        var item = Assert.Single(digest.Items);
        Assert.Equal(SituationDigestKinds.Rejected, item.Kind);
        Assert.Equal("客户订单", item.ModuleTitle);
        Assert.Contains("无按钮授权", item.Reason);
        Assert.Contains(digest.Sources, source => source.StartsWith("rejected:"));
    }

    [Fact]
    public async Task 摘要条目总数不超过上限()
    {
        var gateway = new AssistantSituationDoubles.FakeGateway();
        gateway.Definitions[1606] = AssistantSituationDoubles.Definition(
            1606, "客户订单", ["DD_NO", "CREATE_DATE", "CONFIRM_TAG"], ["DD_NO"]);
        gateway.Rows[1606] = new WorkbenchData(
            [
                OverdueRow("DD1", DateTime.UtcNow.Date.AddDays(-30)),
                OverdueRow("DD2", DateTime.UtcNow.Date.AddDays(-30)),
                OverdueRow("DD3", DateTime.UtcNow.Date.AddDays(-30)),
            ], 3, 1, 5);
        var permissions = new AssistantSituationDoubles.FakePermissions();
        permissions.Browsable.Add(1606);
        var facts = new AssistantSituationDoubles.FakeFacts();
        facts.ActivityModules.Add(1606);
        var service = CreateService(gateway, permissions, facts, options => options.DigestMaxItems = 2);

        var digest = await service.BuildAsync("u1", SituationContext.Empty, CancellationToken.None);

        Assert.Equal(2, digest.Items.Count);
    }

    [Fact]
    public async Task 滞留取数按建立日期倒序并带年龄上界()
    {
        var gateway = new AssistantSituationDoubles.FakeGateway();
        gateway.Definitions[1606] = AssistantSituationDoubles.Definition(
            1606, "客户订单", ["DD_NO", "CREATE_DATE", "CONFIRM_TAG"], ["DD_NO"]);
        gateway.Rows[1606] = new WorkbenchData(
            [OverdueRow("DD2608001", DateTime.UtcNow.Date.AddDays(-20))], 1, 1, 5);
        var permissions = new AssistantSituationDoubles.FakePermissions();
        permissions.Browsable.Add(1606);
        var facts = new AssistantSituationDoubles.FakeFacts();
        facts.ActivityModules.Add(1606);
        var service = CreateService(gateway, permissions, facts, options => options.OverdueMaxAgeDays = 90);

        await service.BuildAsync("u1", SituationContext.Empty, CancellationToken.None);

        var query = Assert.Single(gateway.RowQueries);
        // 取数口径：新→旧（否则按模块默认排序会取到多年前的遗留单，实测 3120 天前）
        Assert.Equal("CREATE_DATE", query.SortField);
        Assert.Equal("desc", query.SortDirection);
        // 年龄上界生效：早于上界的单据不进摘要
        var lowerBound = Assert.Single(query.Query!.Conditions,
            condition => condition.Field == "CREATE_DATE" && condition.Operator == "gte");
        Assert.Equal(DateTime.UtcNow.Date.AddDays(-90).ToString("yyyy-MM-dd"), lowerBound.Value);
    }

    [Fact]
    public async Task 第三条主来源_此刻办不下去进入摘要且计入来源()
    {
        var gateway = new AssistantSituationDoubles.FakeGateway();
        gateway.Definitions[1606] = AssistantSituationDoubles.Definition(
            1606, "客户订单", ["DD_NO", "CREATE_DATE", "CONFIRM_TAG"], ["DD_NO"]);
        gateway.Rows[1606] = new WorkbenchData([], 0, 1, 5);
        var permissions = new AssistantSituationDoubles.FakePermissions();
        permissions.Browsable.Add(1606);
        var facts = new AssistantSituationDoubles.FakeFacts();
        facts.ActivityModules.Add(1606);
        var probe = new AssistantSituationDoubles.FakeBlockedProbe();
        probe.Blocked[1606] = [new DiagnosisBlockedRecord(
            ["DD2608009"], "DD2608009", "以下序号项产品编号不存在 {ROWS}", "validation:SAVE/reference-exists#1")];
        var service = CreateService(gateway, permissions, facts, blockedProbe: probe);

        var digest = await service.BuildAsync("u1", SituationContext.Empty, CancellationToken.None);

        var item = Assert.Single(digest.Items);
        Assert.Equal(SituationDigestKinds.BlockedNow, item.Kind);
        Assert.Equal("DD2608009", item.Key);
        Assert.StartsWith("此刻：", item.Reason);
        // {ROWS} 的明细行由 diagnose_record 逐单展开，摘要不显示占位符
        Assert.DoesNotContain("{ROWS}", item.Reason);
        Assert.Contains(digest.Sources, source => source.StartsWith("blocked-now:"));
        // 实际问了哪些模块（代价可度量：逐单求值只发生在候选模块上）
        Assert.Equal([1606], probe.Probed);
        Assert.Contains(digest.Caveats, caveat => caveat.Contains("只判可只读定论的判据"));
    }

    [Fact]
    public async Task 摘要条目上限对第三条主来源同样生效()
    {
        var gateway = new AssistantSituationDoubles.FakeGateway();
        gateway.Definitions[1606] = AssistantSituationDoubles.Definition(
            1606, "客户订单", ["DD_NO", "CREATE_DATE", "CONFIRM_TAG"], ["DD_NO"]);
        gateway.Rows[1606] = new WorkbenchData([OverdueRow("DD1", DateTime.UtcNow.Date.AddDays(-20))], 1, 1, 5);
        var permissions = new AssistantSituationDoubles.FakePermissions();
        permissions.Browsable.Add(1606);
        var facts = new AssistantSituationDoubles.FakeFacts();
        facts.ActivityModules.Add(1606);
        var probe = new AssistantSituationDoubles.FakeBlockedProbe();
        probe.Blocked[1606] =
        [
            new DiagnosisBlockedRecord(["DD2"], "DD2", "此刻过不了校验", "validation"),
            new DiagnosisBlockedRecord(["DD3"], "DD3", "此刻过不了校验", "validation"),
        ];
        var service = CreateService(gateway, permissions, facts,
            options => options.DigestMaxItems = 1, probe);

        var digest = await service.BuildAsync("u1", SituationContext.Empty, CancellationToken.None);

        Assert.Single(digest.Items);
    }

    [Fact]
    public async Task 主动巡检可停_相关阈值配零即停该来源()
    {
        var gateway = new AssistantSituationDoubles.FakeGateway();
        gateway.Definitions[1606] = AssistantSituationDoubles.Definition(
            1606, "客户订单", ["DD_NO", "CREATE_DATE", "CONFIRM_TAG"], ["DD_NO"]);
        gateway.Rows[1606] = new WorkbenchData(
            [OverdueRow("DD2608001", DateTime.UtcNow.Date.AddDays(-20))], 1, 1, 5);
        var permissions = new AssistantSituationDoubles.FakePermissions();
        permissions.Browsable.Add(1606);
        var facts = new AssistantSituationDoubles.FakeFacts();
        facts.ActivityModules.Add(1606);
        var probe = new AssistantSituationDoubles.FakeBlockedProbe();
        probe.Blocked[1606] = [new DiagnosisBlockedRecord(["DD2"], "DD2", "此刻过不了校验", "validation")];
        var service = CreateService(gateway, permissions, facts, options => options.OverdueDays = 0, probe);

        var digest = await service.BuildAsync("u1", SituationContext.Empty, CancellationToken.None);

        Assert.Empty(digest.Items);
        Assert.Empty(gateway.RowQueries);
        Assert.Empty(probe.Probed);
        Assert.Contains(digest.Caveats, caveat => caveat.Contains("已按阈值关闭", StringComparison.Ordinal));
    }

    [Fact]
    public async Task 主动巡检可停_只停被关掉的那一条来源()
    {
        var gateway = new AssistantSituationDoubles.FakeGateway();
        gateway.Definitions[1606] = AssistantSituationDoubles.Definition(
            1606, "客户订单", ["DD_NO", "CREATE_DATE", "CONFIRM_TAG"], ["DD_NO"]);
        gateway.Rows[1606] = new WorkbenchData(
            [OverdueRow("DD2608001", DateTime.UtcNow.Date.AddDays(-20))], 1, 1, 5);
        var permissions = new AssistantSituationDoubles.FakePermissions();
        permissions.Browsable.Add(1606);
        var facts = new AssistantSituationDoubles.FakeFacts();
        facts.ActivityModules.Add(1606);
        facts.Failures.Add(new SituationFailureFact(
            DateTime.UtcNow.AddHours(-1), "master-field-write", 1606, "字段 IN_SUM 由引擎维护。", "BUSINESS_VALIDATION_FAILED"));
        var probe = new AssistantSituationDoubles.FakeBlockedProbe();
        probe.Blocked[1606] = [new DiagnosisBlockedRecord(["DD2"], "DD2", "此刻过不了校验", "validation")];
        var service = CreateService(gateway, permissions, facts, options => options.BlockedNowProbeRules = 0, probe);

        var digest = await service.BuildAsync("u1", SituationContext.Empty, CancellationToken.None);

        // 关掉的是"逐单求值"这一条来源：滞留与被拒照旧，逐单探针一次都不跑
        Assert.Empty(probe.Probed);
        Assert.NotEmpty(gateway.RowQueries);
        Assert.Contains(digest.Items, item => item.Kind == SituationDigestKinds.Overdue);
        Assert.Contains(digest.Items, item => item.Kind == SituationDigestKinds.Rejected);
    }

    [Fact]
    public async Task 主动巡检可停_最近被拒的阈值配零即停该来源()
    {
        var gateway = new AssistantSituationDoubles.FakeGateway();
        var permissions = new AssistantSituationDoubles.FakePermissions();
        var facts = new AssistantSituationDoubles.FakeFacts();
        facts.Failures.Add(new SituationFailureFact(
            DateTime.UtcNow.AddHours(-1), "DELETEv", 1606, "无按钮授权", "PERMISSION|DENY"));
        var service = CreateService(gateway, permissions, facts, options => options.RecentFailureDays = 0);

        var digest = await service.BuildAsync("u1", SituationContext.Empty, CancellationToken.None);

        Assert.DoesNotContain(digest.Items, item => item.Kind == SituationDigestKinds.Rejected);
        Assert.Contains(digest.Caveats, caveat => caveat.Contains("已按阈值关闭", StringComparison.Ordinal));
    }

    [Fact]
    public void 摘要链路的构造参数里不存在模型依赖()
    {
        // "打开即见不烧额度"是结构性保证：这条链路上根本没有 IChatModel，
        // 所以不靠纪律——只有显式改构造函数才可能引入模型调用。
        var forbidden = new[] { typeof(EOS.API.Features.Assistant.ModelAccess.IChatModel) };
        Type[] services =
        [
            typeof(SituationDigestService),
            typeof(AssistantSituationService),
            typeof(SituationFactsReader),
            typeof(EOS.API.Features.Assistant.Diagnosis.RecordDiagnosisService),
            typeof(EOS.API.Features.Assistant.Diagnosis.RecordDiagnosisReader),
        ];

        foreach (var service in services)
        {
            var parameterTypes = service.GetConstructors()
                .SelectMany(constructor => constructor.GetParameters().Select(parameter => parameter.ParameterType))
                .ToArray();
            foreach (var type in forbidden)
            {
                Assert.DoesNotContain(type, parameterTypes);
            }
        }
    }
}
