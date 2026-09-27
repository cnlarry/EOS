using EOS.API.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 批次选择器（P2）：默认次序必须是"**最早到期在前、空效期排最后**"，
/// 且列/排序只能来自服务端注册表（未注册列一律回退，不报错也不进 SQL）。
///
/// 与其余真库用例的差别：本用例的夹具**必须提交**——选择器仓储自带连接（`DbConnectionFactory`），
/// 读不到未提交的行。因此建数只用自己的料号、收尾按料号精确清干净，并断言残留为 0。
/// </summary>
[Trait("Category", "live-database")]
[Collection("live-database")]
public sealed class BatchChooserLiveTests
{
    private const string Product = "ADR25CHPRO";
    private const string ExpiredBatch = "ADR25CHLOT_EXPIRED";
    private const string SoonBatch = "ADR25CHLOT_SOON";
    private const string LaterBatch = "ADR25CHLOT_LATER";
    private const string NoExpiryBatch = "ADR25CHLOT_NONE";

    private static string RequireConnection()
    {
        var value = Environment.GetEnvironmentVariable("MSSQL_ERP_CONN");
        Assert.False(string.IsNullOrWhiteSpace(value),
            "真库集成测试需要 MSSQL_ERP_CONN；未配置即失败（无连接跳过≠已验证）。");
        return value!;
    }

    [Fact]
    public async Task 默认次序是最早到期在前且空效期排最后()
    {
        await WithBatchesAsync(async (repository, _) =>
        {
            var result = await QueryAsync(repository, "ADR25CHPRO");
            Assert.NotNull(result);

            // 三个有效期的批次按到期日升序，最后才是不受管控的那个
            Assert.Equal(
                new[] { ExpiredBatch, SoonBatch, LaterBatch, NoExpiryBatch },
                result!.Rows.Select(row => row["BATCH_NO"] as string).ToList());
            Assert.Equal(4, result.Total);

            var remaining = result.Rows
                .ToDictionary(row => (string)row["BATCH_NO"]!, row => row["REMAINING_DAYS"]);
            Assert.Equal(-1, Convert.ToInt32(remaining[ExpiredBatch]));
            Assert.Equal(1, Convert.ToInt32(remaining[SoonBatch]));
            Assert.Equal(10, Convert.ToInt32(remaining[LaterBatch]));
            // 空效期的剩余天数是 NULL，不是 0 —— 0 会被读成"今天到期"
            Assert.Null(remaining[NoExpiryBatch]);

            var states = result.Rows
                .ToDictionary(row => (string)row["BATCH_NO"]!, row => row["EXPIRY_STATE"] as string);
            Assert.Equal("已过期", states[ExpiredBatch]);
            Assert.Equal("正常", states[SoonBatch]);
            Assert.Equal("不受管控", states[NoExpiryBatch]);
        });
    }

    /// <summary>未注册的排序列不能进 SQL：回退默认次序，且不抛异常（fail-closed 而非报错）。</summary>
    [Fact]
    public async Task 未注册的排序列回退默认次序()
    {
        await WithBatchesAsync(async (repository, _) =>
        {
            var fallback = await QueryAsync(repository, "ADR25CHPRO", sortField: "BATCH_NO; DROP TABLE dbo.PRODUCT--");
            Assert.NotNull(fallback);
            Assert.Equal(NoExpiryBatch, fallback!.Rows[^1]["BATCH_NO"] as string);
            Assert.Equal(ExpiredBatch, fallback.Rows[0]["BATCH_NO"] as string);
        });
    }

    /// <summary>关键字过滤只走注册过的列（料号在过滤值里参数化，不进 SQL 文本）。</summary>
    [Fact]
    public async Task 按料号过滤与关键字均走服务端白名单()
    {
        await WithBatchesAsync(async (repository, _) =>
        {
            var other = await QueryAsync(repository, "ADR25CH-NOT-EXIST");
            Assert.NotNull(other);
            Assert.Empty(other!.Rows);

            var byKeyword = await QueryAsync(repository, "ADR25CHPRO", keyword: "LOT_SOON");
            Assert.NotNull(byKeyword);
            Assert.Equal(new[] { SoonBatch }, byKeyword!.Rows.Select(row => row["BATCH_NO"] as string).ToList());
        });
    }

    [Fact]
    public void 注册项与权限门与来源映射都已就位()
    {
        Assert.True(ChooserRepository.IsRegistered("inventory.batches"));
        Assert.False(ChooserRepository.IsRegistered("inventory.unknown"));
        // 权限门挂 1303 料件库存资料；数据源本身不鉴权，控制器按它取浏览权
        Assert.Equal(1303, ChooserRepository.PermissionModuleId("inventory.batches"));
        // 表名 → 优选数据源键的映射在服务端：前端不按表名硬编码
        Assert.Equal("inventory.batches", ChooserRepository.PreferredSourceKey("INV_BATCH_M"));
        Assert.Equal("inventory.batches", ChooserRepository.PreferredSourceKey(" inv_batch_m "));
        Assert.Null(ChooserRepository.PreferredSourceKey("PRODUCT"));
        // 料号过滤值只作参数：超长形态一律当作"不过滤"，不会拼进 SQL
        Assert.Equal(string.Empty, ChooserRepository.ResolveBatchProductFilter(
            new Dictionary<string, string> { ["proNo"] = new string('X', 61) }));
        Assert.Equal("P001", ChooserRepository.ResolveBatchProductFilter(
            new Dictionary<string, string> { ["proNo"] = " P001 " }));
    }

    private static async Task<UnifiedChooserResultView?> QueryAsync(
        ChooserRepository repository, string productNo, string? sortField = null, string? keyword = null)
    {
        var request = new EOS.API.Models.UnifiedChooserQueryRequest(
            SourceKey: "inventory.batches",
            Args: new Dictionary<string, string> { ["proNo"] = productNo },
            Keyword: keyword,
            SortField: sortField,
            Page: 1,
            PageSize: 50);
        var result = await repository.QueryAsync(request, CancellationToken.None);
        return result is null
            ? null
            : new UnifiedChooserResultView(
                result.Total,
                result.Rows
                    .Select(row => (IDictionary<string, object?>)row.ToDictionary(
                        pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase))
                    .ToList());
    }

    /// <summary>把结果收成才便于断言：只留本用例关心的两列形状。</summary>
    private sealed record UnifiedChooserResultView(int Total, IReadOnlyList<IDictionary<string, object?>> Rows);

    /// <summary>
    /// 提交式夹具：只写 `INV_BATCH_M`（自己造的料号），跑完按料号精确删除并断言零残留。
    /// 不碰余额表 / 流水表 / 审计——选择器只读批次账，造数也只造它。
    /// </summary>
    private static async Task WithBatchesAsync(Func<ChooserRepository, DateTime, Task> body)
    {
        var connectionString = RequireConnection();
        var asOf = DateTime.Today;
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        try
        {
            await ExecuteAsync(connection,
                "DELETE FROM dbo.INV_BATCH_M WHERE PRO_NO = @Pro;",
                ("@Pro", Product));
            // 三个有效期（过期 / 明天 / 十天后）+ 一个不受管控；都有余量，才会出现在候选里
            await ExecuteAsync(connection,
                "INSERT INTO dbo.INV_BATCH_M (BATCH_NO, PRO_NO, IN_SUM, OUT_SUM, EFFECT_DATE, CREATE_PERSON, CREATE_DATE, CONFIRM_TAG, FINISHED_TAG, CI) "
                + "VALUES (@B1, @Pro, 10, 0, @D1, N'ADR25CH', SYSDATETIME(), 0, 0, N''), "
                + "       (@B2, @Pro, 10, 0, @D2, N'ADR25CH', SYSDATETIME(), 0, 0, N''), "
                + "       (@B3, @Pro, 10, 0, @D3, N'ADR25CH', SYSDATETIME(), 0, 0, N''), "
                + "       (@B4, @Pro, 10, 0, NULL, N'ADR25CH', SYSDATETIME(), 0, 0, N''), "
                // 无余量的批次不该出现在候选里（与既有批次来源的过滤口径一致）
                + "       (N'ADR25CHLOT_EMPTY', @Pro, 5, 5, @D1, N'ADR25CH', SYSDATETIME(), 0, 0, N'');",
                ("@Pro", Product),
                ("@B1", ExpiredBatch), ("@D1", asOf.AddDays(-1)),
                ("@B2", SoonBatch), ("@D2", asOf.AddDays(1)),
                ("@B3", LaterBatch), ("@D3", asOf.AddDays(10)),
                ("@B4", NoExpiryBatch));

            var repository = new ChooserRepository(
                PolicyServiceFactory.Connections(connectionString), NullLogger<ChooserRepository>.Instance);
            await body(repository, asOf);
        }
        finally
        {
            await ExecuteAsync(connection, "DELETE FROM dbo.INV_BATCH_M WHERE PRO_NO = @Pro;", ("@Pro", Product));
            await using var command = new SqlCommand(
                "SELECT COUNT(*) FROM dbo.INV_BATCH_M WHERE PRO_NO = @Pro;", connection);
            command.Parameters.AddWithValue("@Pro", Product);
            Assert.Equal(0, Convert.ToInt32(await command.ExecuteScalarAsync()));
        }
    }

    private static async Task ExecuteAsync(
        SqlConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        await using var command = new SqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        await command.ExecuteNonQueryAsync();
    }
}
