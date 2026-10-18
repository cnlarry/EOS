using EOS.API.Data;
using EOS.API.Data.Workbench;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

namespace EOS.API.Tests;

/// <summary>
/// 统一表单能力（`MODULES.UNIFIED_FORM_ENABLED` / `UNIFIED_FORM_EDIT_ENABLED`）的测试替身。
///
/// 生产实现读库（<see cref="UnifiedFormCapabilitySource"/>）；用例里要么用
/// <see cref="TestFormCapability.None"/>（不启用任何模块——与"能力进库之前、用例传空名单"
/// 的行为逐字一致），要么按用例要断言的组合显式构造。
/// </summary>
internal static class TestFormCapability
{
    /// <summary>没有模块启用统一表单（默认替身；与历史用例传空名单等价）。</summary>
    public static IUnifiedFormCapabilitySource None { get; } = new FakeUnifiedFormCapabilitySource();

    /// <summary>指定"表单可用"与"可写"两个集合（可写必是可用集合的子集）。</summary>
    public static IUnifiedFormCapabilitySource For(IEnumerable<int> formEnabled, IEnumerable<int>? writable = null)
        => new FakeUnifiedFormCapabilitySource(
            formEnabled.ToHashSet(),
            (writable ?? formEnabled).ToHashSet());

    /// <summary>单个模块的能力（只读用例与"可写模块"用例用它）。</summary>
    public static IUnifiedFormCapabilitySource Single(int moduleId, bool editEnabled)
        => For([moduleId], editEnabled ? [moduleId] : []);
}

/// <summary>
/// 内存实现：按集合回答。集合之外一律"无表单、不可写"（fail-closed，与生产同口径）。
/// </summary>
internal sealed class FakeUnifiedFormCapabilitySource(
    IReadOnlySet<int>? formEnabled = null,
    IReadOnlySet<int>? writable = null) : IUnifiedFormCapabilitySource
{
    private readonly IReadOnlySet<int> formEnabledIds = formEnabled ?? new HashSet<int>();
    private readonly IReadOnlySet<int> writableIds = writable ?? new HashSet<int>();

    public Task<UnifiedFormCapability> GetAsync(int moduleId, CancellationToken token)
        => Task.FromResult(new UnifiedFormCapability(
            formEnabledIds.Contains(moduleId), writableIds.Contains(moduleId)));

    public Task<UnifiedFormCapability> GetAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        int moduleId,
        CancellationToken token)
        => GetAsync(moduleId, token);

    public Task<IReadOnlySet<int>> GetFormEnabledIdsAsync(CancellationToken token)
        => Task.FromResult(formEnabledIds);

    public Task<IReadOnlySet<int>> GetWritableIdsAsync(CancellationToken token)
        => Task.FromResult(writableIds);
}

/// <summary>
/// 真库替身工厂：给"只想拿真实取值、不打算断言能力"的构造点用（真库用例的装配辅助）。
/// 没有连接串时回落到 <see cref="TestFormCapability.None"/>——那类用例本就会跳过。
/// </summary>
internal static class LiveFormCapability
{
    public static IUnifiedFormCapabilitySource FromEnvironment()
    {
        var connectionString = Environment.GetEnvironmentVariable("MSSQL_ERP_CONN");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return TestFormCapability.None;
        }
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:ErpDatabase"] = connectionString,
            })
            .Build();
        return new UnifiedFormCapabilitySource(new DbConnectionFactory(config));
    }
}
