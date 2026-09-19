using EOS.API.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 库存策略的求值口径与组合规则。求值必须收口在 <see cref="DepotStockPolicyService"/>：
/// 库别行**整行覆盖**部署级默认行，不做列级逐字段继承。
/// 组合校验里未实现档位（批次 3 / 容量）的拒绝放在服务端——界面灰显只是体验，
/// 直连 API 仍可复现"保存成功却不生效"。
/// </summary>
[Collection("live-database")]
public sealed class DepotStockPolicyTests
{
    private static string RequireConnection()
    {
        var value = Environment.GetEnvironmentVariable("EOS_ERP_TEST_CONNECTION");
        Assert.False(string.IsNullOrWhiteSpace(value),
            "真库集成测试需要 EOS_ERP_TEST_CONNECTION；未配置即失败（无连接跳过≠已验证）。");
        return value!;
    }

    private static DepotStockPolicyService CreateService()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:ErpDatabase"] = RequireConnection(),
            })
            .Build();
        return new DepotStockPolicyService(new DbConnectionFactory(configuration));
    }

    /// <summary>测试用构造：六个维度 + 月结两维（默认取部署级口径）。</summary>
    private static DepotStockPolicy Policy(
        string depot, int locationMode, string storageMode, int batchMode, int capacityMode,
        bool mixProduct = true, bool mixBatch = true,
        bool monthCloseByBatch = true, bool monthCloseByLocation = false)
        => new(depot, locationMode, storageMode, batchMode, capacityMode, mixProduct, mixBatch,
            monthCloseByBatch, monthCloseByLocation);

    // ---------- 组合规则（纯逻辑，不需要数据库） ----------

    [Fact]
    public void 未实现档位在保存期被拒()
    {
        var batch3 = Policy("CP", 3, "FIXED", 3, 0, true, true);
        var (errors, _) = DepotStockPolicyService.Validate(batch3);
        Assert.Contains(errors, message => message.Contains("批次档位 3"));

        var capacity = Policy("CP", 3, "FIXED", 0, 2, true, true);
        var (errors2, _) = DepotStockPolicyService.Validate(capacity);
        Assert.Contains(errors2, message => message.Contains("容量档位"));
    }

    [Fact]
    public void 随机存放要求位置强制()
    {
        // R-C1：位置档位 0/1（不管 / 可填）配随机或混合存放 ⇒ 货必然丢失
        foreach (var locationMode in new[] { 0, 1 })
        {
            var (errors, _) = DepotStockPolicyService.Validate(Policy("CP", locationMode, "RANDOM", 0, 0, true, true));
            Assert.Contains(errors, message => message.Contains("随机存放"));
        }

        // 档位 2/3 不触发硬性拒绝；档位 2 只出软性告警
        var (okErrors, okWarnings) = DepotStockPolicyService.Validate(Policy("CP", 3, "RANDOM", 0, 0, true, true));
        Assert.Empty(okErrors);
        Assert.Empty(okWarnings);

        var (_, warned) = DepotStockPolicyService.Validate(Policy("CP", 2, "MIXED", 0, 0, true, true));
        Assert.Contains(warned, message => message.Contains("扫码"));
    }

    [Fact]
    public void 容量与效期都以位置为前提()
    {
        // R-C2：位置档位 0 时不能启用容量校验
        var (errors, _) = DepotStockPolicyService.Validate(Policy("CP", 0, "FIXED", 0, 1, true, true));
        Assert.Contains(errors, message => message.Contains("容量校验"));

        // R-C3：效期追溯要求位置强制（档位 3）
        var (errors2, _) = DepotStockPolicyService.Validate(Policy("CP", 2, "FIXED", 3, 0, true, true));
        Assert.Contains(errors2, message => message.Contains("效期追溯"));
    }

    [Fact]
    public void 最松配置不产生任何错误或告警()
    {
        var (errors, warnings) = DepotStockPolicyService.Validate(Policy("*", 0, "FIXED", 0, 0, true, true));
        Assert.Empty(errors);
        Assert.Empty(warnings);
    }

    /// <summary>
    /// 取值域越界必须在服务端拦下（400），而不是让数据库以"字符串将被截断"/"违反 CHECK"
    /// 抛 SqlException 冒成 500——调用方看到"服务器内部错误"就无从知道是自己传错了参数。
    /// </summary>
    [Fact]
    public void 取值域越界在保存期被拒而非由数据库抛异常()
    {
        var tooLong = Policy("ADR14POLTOOLONG", 0, "FIXED", 0, 0);
        Assert.Contains(DepotStockPolicyService.Validate(tooLong).Errors, message => message.Contains("库别代号"));

        foreach (var storage in new[] { "LIFO", "" })
        {
            var bad = Policy("CP", 0, storage, 0, 0);
            Assert.Contains(DepotStockPolicyService.Validate(bad).Errors, message => message.Contains("存放方式"));
        }

        // 边界内不报错（10 字符正好等于列宽）
        Assert.Empty(DepotStockPolicyService.Validate(Policy("0123456789", 0, "fixed", 0, 0)).Errors);
    }

    [Fact]
    public void 月结维度不接受库别覆盖()
    {
        var deployment = Policy("*", 0, "FIXED", 0, 0, true, true, true, false);

        // 部署级自身：任何取值都合法（它就是基准）
        Assert.Null(DepotStockPolicyService.ValidateMonthCloseScope(
            Policy("*", 0, "FIXED", 0, 0, true, true, false, true), deployment));

        // 库别行与部署级一致：放行
        Assert.Null(DepotStockPolicyService.ValidateMonthCloseScope(
            Policy("CP", 3, "RANDOM", 2, 0, false, false, true, false), deployment));

        // 库别行试图改月结粒度：拒绝（跨仓粒度不一致会让汇总重复计数或漏计）
        Assert.NotNull(DepotStockPolicyService.ValidateMonthCloseScope(
            Policy("CP", 3, "RANDOM", 2, 0, false, false, false, false), deployment));
        Assert.NotNull(DepotStockPolicyService.ValidateMonthCloseScope(
            Policy("CP", 3, "RANDOM", 2, 0, false, false, true, true), deployment));
    }

    /// <summary>
    /// 保存期拒存：硬性规则与月结作用域违规一律 fail-closed（服务端拒绝，不靠界面拦截），
    /// 合规配置正常落库。本用例会真实写入，故收尾显式清理。
    /// </summary>
    [Fact]
    public async Task 保存期拒绝未实现档位与月结库别覆盖()
    {
        const string depot = "ADR14POLSV";
        var service = CreateService();
        try
        {
            // 未实现档位：直接提交（等价于绕过界面 POST）必须被拒
            var unimplemented = await service.SaveAsync(Policy(depot, 3, "FIXED", 3, 0), "adr14-test");
            Assert.False(unimplemented.Saved);
            Assert.Contains(unimplemented.Errors, message => message.Contains("批次档位 3"));

            var capacity = await service.SaveAsync(Policy(depot, 3, "FIXED", 0, 2), "adr14-test");
            Assert.False(capacity.Saved);
            Assert.Contains(capacity.Errors, message => message.Contains("容量档位"));

            // 月结维度：库别行与部署级不一致即拒
            var scopeOverride = await service.SaveAsync(Policy(depot, 0, "FIXED", 0, 0, true, true, false, true), "adr14-test");
            Assert.False(scopeOverride.Saved);
            Assert.Contains(scopeOverride.Errors, message => message.Contains("月结维度"));

            // 取值域越界：走真实保存路径也必须是被拒（而不是抛 SqlException）
            var tooLong = await service.SaveAsync(Policy("ADR14POLTOOLONG", 0, "FIXED", 0, 0), "adr14-test");
            Assert.False(tooLong.Saved);
            Assert.Contains(tooLong.Errors, message => message.Contains("库别代号"));
            var badStorage = await service.SaveAsync(Policy(depot, 0, "LIFO", 0, 0), "adr14-test");
            Assert.False(badStorage.Saved);
            Assert.Contains(badStorage.Errors, message => message.Contains("存放方式"));

            // 合规配置：落库，且月结维度被写成部署级取值（库别行不得携带自己的粒度）
            var saved = await service.SaveAsync(Policy(depot, 3, "FIXED", 2, 0, false, false, true, false), "adr14-test");
            Assert.True(saved.Saved);
            Assert.Empty(saved.Errors);

            var stored = (await service.ListAsync()).Single(row => row.DepotId == depot);
            Assert.Equal(3, stored.LocationMode);
            Assert.Equal(2, stored.BatchMode);
            Assert.False(stored.MixProduct);
        }
        finally
        {
            await using var connection = new SqlConnection(RequireConnection());
            await connection.OpenAsync();
            await using var cleanup = new SqlCommand("DELETE FROM dbo.DEPOT_STOCK_POLICY WHERE DEPOT_ID=@d", connection);
            cleanup.Parameters.AddWithValue("@d", depot);
            await cleanup.ExecuteNonQueryAsync();
        }
    }

    // ---------- 两跳求值（真库） ----------

    [Fact]
    public async Task 库别无行时回落部署级默认()
    {
        var service = CreateService();
        var policy = await service.ResolveAsync("ADR14POLNONE");

        Assert.Equal(DepotStockPolicyService.DeploymentScope, policy.DepotId);
        Assert.Equal(0, policy.LocationMode);
        Assert.Equal("FIXED", policy.StorageMode);
        Assert.Equal(0, policy.BatchMode);
        Assert.Equal(0, policy.CapacityMode);
        Assert.True(policy.MixProduct);
        Assert.True(policy.MixBatch);
    }

    [Fact]
    public async Task 库别行整行覆盖而不做列级继承()
    {
        const string depot = "ADR14POLDP";
        var connectionString = RequireConnection();
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
        try
        {
            await using (var insert = new SqlCommand(
                "DELETE FROM dbo.DEPOT_STOCK_POLICY WHERE DEPOT_ID=@d; "
                + "INSERT INTO dbo.DEPOT_STOCK_POLICY (DEPOT_ID, LOCATION_MODE, STORAGE_MODE, BATCH_MODE, CAPACITY_MODE, MIX_PRODUCT, MIX_BATCH) "
                + "VALUES (@d, 3, N'RANDOM', 2, 0, 0, 0);", connection, transaction))
            {
                insert.Parameters.AddWithValue("@d", depot);
                await insert.ExecuteNonQueryAsync();
            }

            var service = CreateService();
            // 在调用方事务内求值：另开连接会被这里的未提交写锁住（这本身也是
            // 策略求值在保存校验路径里的正确用法）。
            var policy = await service.ResolveAsync(depot, connection, transaction);

            // 库别行存在 ⇒ 整行采用：六个维度全部来自库别行，没有任何一个继承自部署级默认
            // （部署级是 0 / FIXED / 0 / 0 / 1 / 1）。
            Assert.Equal(depot, policy.DepotId);
            Assert.Equal(3, policy.LocationMode);
            Assert.Equal("RANDOM", policy.StorageMode);
            Assert.Equal(2, policy.BatchMode);
            Assert.False(policy.MixProduct);
            Assert.False(policy.MixBatch);
        }
        finally
        {
            await transaction.RollbackAsync(CancellationToken.None);
        }
    }
}
