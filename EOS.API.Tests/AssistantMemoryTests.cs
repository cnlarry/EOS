using System.Text.Json;
using EOS.API.Features.Assistant.Memory;
using EOS.API.Features.Assistant.Tools;
using Xunit;

namespace EOS.API.Tests;

public sealed class AssistantMemoryTests
{
    private static AssistantMemoryItem Item(
        long id, string key, string value, DateTimeOffset accessed) =>
        new(id, "fact", key, value, "manual", null, "active",
            accessed, accessed);

    private sealed class FakeStore(string? preferences, IReadOnlyList<AssistantMemoryItem> memories)
        : IAssistantMemoryStore
    {
        public Task<string?> GetPreferencesAsync(string userId, CancellationToken token) =>
            Task.FromResult(preferences);

        public Task SetPreferencesAsync(string userId, string? preferencesJson, CancellationToken token) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<AssistantMemoryItem>> ListMemoriesAsync(string userId, CancellationToken token) =>
            Task.FromResult(memories);

        public Task<AssistantMemoryItem> AddMemoryAsync(
            string userId, string memoryType, string memoryKey, string memoryValue,
            long? sourceMessageId, CancellationToken token) => throw new NotSupportedException();

        public Task<AssistantMemoryItem> AddPendingAsync(
            string userId, string memoryType, string memoryKey, string memoryValue,
            long? sourceMessageId, int confidence, CancellationToken token) => throw new NotSupportedException();

        public Task<IReadOnlyList<AssistantMemoryItem>> ListPendingAsync(string userId, CancellationToken token) =>
            Task.FromResult<IReadOnlyList<AssistantMemoryItem>>([]);

        public Task<string> ResolvePendingAsync(string userId, long memoryId, bool confirm, CancellationToken token) =>
            Task.FromResult("confirmed");

        public Task ForgetMeAsync(string userId, CancellationToken token) => Task.CompletedTask;

        public Task<bool> DeleteMemoryAsync(string userId, long memoryId, CancellationToken token) =>
            Task.FromResult(true);

        public Task<string> BuildMemoryPrefixAsync(string userId, string? keyword, CancellationToken token) =>
            Task.FromResult(AssistantMemoryStore.BuildPrefix(preferences, null,
                AssistantMemoryStore.SelectMemories(memories, keyword, AssistantMemoryStore.InjectionTopK)));
    }

    [Fact]
    public void SelectMemories_PrefersKeywordHit_AndCapsTopK()
    {
        var now = DateTimeOffset.UtcNow;
        var items = new[]
        {
            Item(1, "旧事项", " unrelated", now.AddHours(-1)),
            Item(2, "送货单查询", "常用送货单", now.AddHours(-3)),
            Item(3, "更新事项", "unrelated", now),
        };

        var selected = AssistantMemoryStore.SelectMemories(items, "送货单", 2);

        Assert.Equal(2, selected.Count);
        Assert.Equal(2, selected[0].Id);
    }

    [Fact]
    public void FilterByReference_DropsMemories_OutsideBrowseScope()
    {
        var now = DateTimeOffset.UtcNow;
        var items = new[]
        {
            Item(1, "偏好", "深色主题", now),
            Item(2, "跟进 module=1405 的订单", "查 module=1405", now),
        };

        var visible = AssistantMemoryStore.FilterByReference(
            items, new Dictionary<int, bool> { [1405] = false });

        Assert.Single(visible);
        Assert.Equal(1, visible[0].Id);
    }

    [Fact]
    public void BuildPrefix_MarksReferenceOnly()
    {
        var now = DateTimeOffset.UtcNow;
        var prefix = AssistantMemoryStore.BuildPrefix(
            """{"theme":"dark"}""", null, [Item(1, "常用模块", "先看送货单", now)]);

        Assert.Contains("仅供参考", prefix);
        Assert.Contains("dark", prefix);
        Assert.Contains("送货单", prefix);
    }

    [Fact]
    public void BuildPrefix_RendersDerivedProfile()
    {
        var now = DateTimeOffset.UtcNow;
        var prefix = AssistantMemoryStore.BuildPrefix(null,
            new DerivedProfile(["管理员组"], [(1405, "客户订单")]),
            [Item(1, "常用模块", "先看送货单", now)]);

        Assert.Contains("管理员组", prefix);
        Assert.Contains("1405 客户订单", prefix);
    }

    [Fact]
    public async Task FilterByRowAccess_DropsUnverifiableReferences()
    {
        var now = DateTimeOffset.UtcNow;
        var items = new[]
        {
            Item(1, "偏好", "深色主题", now),
            Item(2, "跟进单据", "看 module=1405 _keys=[\"DD\",\"1\"]", now),
        };

        var visible = await AssistantMemoryStore.FilterByRowAccessAsync(items, reference =>
            Task.FromResult(reference.ModuleId != 1405));

        Assert.Single(visible);
        Assert.Equal(1, visible[0].Id);
    }

    [Fact]
    public void Validators_RejectBadInput_AndDetectSensitivePattern()
    {
        Assert.Throws<ArgumentException>(() => AssistantMemoryStore.ValidateType("nope"));
        Assert.Throws<ArgumentException>(() => AssistantMemoryStore.ValidateKey("  "));
        Assert.Throws<ArgumentException>(() => AssistantMemoryStore.ValidateValue(""));
        Assert.True(AssistantMemoryStore.ContainsSensitivePattern("电话 13800138000"));
        Assert.True(AssistantMemoryStore.ContainsSensitivePattern("证件 110101199001011234"));
        Assert.False(AssistantMemoryStore.ContainsSensitivePattern("深色主题，先看送货单"));
    }

    [Fact]
    public async Task DigestTool_ReturnsUserScopedSummary()
    {
        var now = DateTimeOffset.UtcNow;
        var store = new FakeStore("""{"theme":"dark"}""",
            [Item(1, "常用模块", "先看送货单", now)]);
        var tool = new GetMyDigestTool(store);

        var result = await tool.ExecuteAsync("u1", JsonDocument.Parse("{}").RootElement, CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Contains("仅你本人可见", result.ContentForModel);
        Assert.Contains("送货单", result.ContentForModel);
    }
}
