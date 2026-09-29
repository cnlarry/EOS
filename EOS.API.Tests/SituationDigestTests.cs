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
        Action<AssistantSituationBudgetOptions>? configure = null)
    {
        var budget = AssistantSituationDoubles.Budget(configure);
        var situation = new AssistantSituationService(
            AssistantSituationDoubles.UserContext(), facts, budget, NullLogger<AssistantSituationService>.Instance);
        return new SituationDigestService(
            gateway, permissions, facts, situation, budget, NullLogger<SituationDigestService>.Instance);
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
