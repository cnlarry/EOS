using EOS.API.Data.Assistant;
using EOS.API.Features.Assistant.Governance;
using EOS.API.Features.Assistant.ModelAccess;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 「型号 → 单价」查找表的取舍规则（<see cref="AssistantUsageView.PriceByCode"/>）。
///
/// <para>
/// 身份含用途之后（迁移 469），同一个型号可以在库里有**两行**（对话 + 判官），而用量与金额都按
/// 消息里记的型号名聚合——那里没有用途这一列。直接 <c>ToDictionary</c> 会在出现第二行时抛
/// "An item with the same key has already been added"，整张用量看板报错；所以这里钉住两点：
/// 一个型号只出一项，且**优先取对话那行**（不随行顺序漂移）。
/// </para>
/// </summary>
public sealed class AssistantUsagePricingTests
{
    private static AssistantModelRow Model(
        int id, string code, AssistantModelKind kind, decimal? input = null, decimal? output = null) => new(
        ModelId: id, ProviderId: 1, ModelCode: code, DisplayName: code,
        ContextWindow: null, MaxOutputTokens: null, DefaultTemperature: null, TimeoutSeconds: null,
        InputPerMillionYuan: input, OutputPerMillionYuan: output,
        SupportsTools: kind == AssistantModelKind.Chat, IsActive: false, Enabled: true,
        SortIdx: 0, Remark: null,
        CreatedAt: DateTimeOffset.UnixEpoch, UpdatedAt: DateTimeOffset.UnixEpoch, Kind: kind);

    [Fact]
    public void Same_Model_Code_In_Two_Purposes_Yields_One_Entry_And_Prefers_Chat()
    {
        var models = new[]
        {
            // 判官排在前面也不该抢先：取舍不依赖行顺序
            Model(31, "deepseek-v4-pro", AssistantModelKind.Judge, input: 10m, output: 20m),
            Model(12, "deepseek-v4-pro", AssistantModelKind.Chat, input: 1m, output: 2m),
            Model(5, "other-model", AssistantModelKind.Chat, input: 3m, output: 4m),
        };

        var prices = AssistantUsageView.PriceByCode(models);

        Assert.Equal(2, prices.Count);
        Assert.Equal(1m, prices["deepseek-v4-pro"].InputPerMillionYuan);
        Assert.Equal(AssistantModelKind.Chat, prices["deepseek-v4-pro"].Kind);
    }

    [Fact]
    public void Without_A_Chat_Row_It_Falls_Back_To_Another_Purpose_Deterministically()
    {
        var models = new[]
        {
            Model(9, "embedding-3", AssistantModelKind.Embedding, input: 0.5m),
            Model(4, "judge-only", AssistantModelKind.Judge, input: 2m),
        };

        var prices = AssistantUsageView.PriceByCode(models);

        // 只有判官那条时就用它（不能因为"库里没有对话行"就让成本算成 0）
        Assert.Equal(2m, prices["judge-only"].InputPerMillionYuan);
        Assert.Equal(AssistantModelKind.Judge, prices["judge-only"].Kind);
        Assert.Equal(0.5m, prices["embedding-3"].InputPerMillionYuan);
    }

    [Fact]
    public void Model_Codes_Are_Matched_Case_Insensitively()
    {
        var prices = AssistantUsageView.PriceByCode(
        [
            Model(1, "DeepSeek-Flash", AssistantModelKind.Chat, input: 1m),
        ]);

        Assert.True(prices.ContainsKey("deepseek-flash"));
    }
}
