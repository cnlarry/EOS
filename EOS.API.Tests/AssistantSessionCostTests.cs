using EOS.API.Data.Assistant;
using EOS.API.Features.Assistant.Governance;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 会话金额折算（3101 会话管理列表的「金额」列）。只测折算这一层，不连库。
///
/// <para>
/// 这里钉住三件容易做错的事：**按模型分别计价再相加**（不是拿会话总 token 乘某一个模型单价）、
/// **单价找不回时退全局兜底价**（不是按 0 元算）、以及**不同会话互不串**。
/// 计价本身仍是 <see cref="AssistantCost"/>，所以断言直接对着它算出来的数写——
/// 这两处的口径必须一致，否则"列表上的金额之和"与"用量看板的总成本"会分成两个数。
/// </para>
/// </summary>
public sealed class AssistantSessionCostTests
{
    private static readonly AssistantCostOptions Cost = new()
    {
        InputPerMillionYuan = 1,
        OutputPerMillionYuan = 2,
    };

    private static AssistantModelRow Model(string code, decimal? input, decimal? output) => new(
        ModelId: 1, ProviderId: 1, ModelCode: code, DisplayName: code,
        ContextWindow: null, MaxOutputTokens: null, DefaultTemperature: null, TimeoutSeconds: null,
        InputPerMillionYuan: input, OutputPerMillionYuan: output,
        SupportsTools: true, IsActive: true, Enabled: true, SortIdx: 0, Remark: null,
        CreatedAt: DateTimeOffset.UnixEpoch, UpdatedAt: DateTimeOffset.UnixEpoch);

    [Fact]
    public void Prices_Each_Model_Separately_Then_Adds_Up()
    {
        var prices = new Dictionary<string, AssistantModelRow>(StringComparer.OrdinalIgnoreCase)
        {
            ["cheap"] = Model("cheap", 1m, 2m),
            ["pricey"] = Model("pricey", 10m, 20m),
        };
        var rows = new[]
        {
            new SessionModelUsage(1, "cheap", 1_000_000, 0),   // 1 元
            new SessionModelUsage(1, "pricey", 0, 1_000_000),  // 20 元
        };

        var cost = AssistantUsageView.PerSessionCost(rows, prices, Cost);

        // 会话总 token 是 2,000,000，乘任何一个单价都得不到 21 元——先分组再相加才是这个会话的钱
        Assert.Equal(21d, cost[1], 4);
    }

    [Fact]
    public void Falls_Back_To_Global_Price_When_Model_Is_Gone()
    {
        var rows = new[] { new SessionModelUsage(7, "deleted-model", 500_000, 500_000) };

        var cost = AssistantUsageView.PerSessionCost(rows, new Dictionary<string, AssistantModelRow>(), Cost);

        // 兜底价 1 / 2 元每百万 = 0.5 + 1.0；按 0 元算会把"花过钱"显示成"没花钱"
        Assert.Equal(1.5d, cost[7], 4);
    }

    [Fact]
    public void Keeps_Sessions_Apart_And_Rounds_To_Four_Decimals()
    {
        var prices = new Dictionary<string, AssistantModelRow>(StringComparer.OrdinalIgnoreCase)
        {
            ["m"] = Model("m", 3m, 6m),
        };
        var rows = new[]
        {
            new SessionModelUsage(1, "m", 1, 0),     // 0.000003 → 0.0000
            new SessionModelUsage(2, "m", 0, 1),     // 0.000006 → 0.0000
            new SessionModelUsage(3, "m", 1_000, 0), // 0.003
        };

        var cost = AssistantUsageView.PerSessionCost(rows, prices, Cost);

        Assert.Equal(3, cost.Count);
        Assert.Equal(0d, cost[1]);
        Assert.Equal(0.003d, cost[3], 4);
    }

    [Fact]
    public void Empty_Input_Produces_No_Cost()
    {
        Assert.Empty(AssistantUsageView.PerSessionCost(
            [], new Dictionary<string, AssistantModelRow>(), Cost));
    }
}
