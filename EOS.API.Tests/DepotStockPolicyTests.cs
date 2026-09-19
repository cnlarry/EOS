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

    // ---------- 组合规则（纯逻辑，不需要数据库） ----------

    [Fact]
    public void 未实现档位在保存期被拒()
    {
        var batch3 = new DepotStockPolicy("CP", 3, "FIXED", 3, 0, true, true);
        var (errors, _) = DepotStockPolicyService.Validate(batch3);
        Assert.Contains(errors, message => message.Contains("批次档位 3"));

        var capacity = new DepotStockPolicy("CP", 3, "FIXED", 0, 2, true, true);
        var (errors2, _) = DepotStockPolicyService.Validate(capacity);
        Assert.Contains(errors2, message => message.Contains("容量档位"));
    }

    [Fact]
    public void 随机存放要求位置强制()
    {
        // R-C1：位置档位 0/1（不管 / 可填）配随机或混合存放 ⇒ 货必然丢失
        foreach (var locationMode in new[] { 0, 1 })
        {
            var (errors, _) = DepotStockPolicyService.Validate(new DepotStockPolicy("CP", locationMode, "RANDOM", 0, 0, true, true));
            Assert.Contains(errors, message => message.Contains("随机存放"));
        }

        // 档位 2/3 不触发硬性拒绝；档位 2 只出软性告警
        var (okErrors, okWarnings) = DepotStockPolicyService.Validate(new DepotStockPolicy("CP", 3, "RANDOM", 0, 0, true, true));
        Assert.Empty(okErrors);
        Assert.Empty(okWarnings);

        var (_, warned) = DepotStockPolicyService.Validate(new DepotStockPolicy("CP", 2, "MIXED", 0, 0, true, true));
        Assert.Contains(warned, message => message.Contains("扫码"));
    }

    [Fact]
    public void 容量与效期都以位置为前提()
    {
        // R-C2：位置档位 0 时不能启用容量校验
        var (errors, _) = DepotStockPolicyService.Validate(new DepotStockPolicy("CP", 0, "FIXED", 0, 1, true, true));
        Assert.Contains(errors, message => message.Contains("容量校验"));

        // R-C3：效期追溯要求位置强制（档位 3）
        var (errors2, _) = DepotStockPolicyService.Validate(new DepotStockPolicy("CP", 2, "FIXED", 3, 0, true, true));
        Assert.Contains(errors2, message => message.Contains("效期追溯"));
    }

    [Fact]
    public void 最松配置不产生任何错误或告警()
    {
        var (errors, warnings) = DepotStockPolicyService.Validate(new DepotStockPolicy("*", 0, "FIXED", 0, 0, true, true));
        Assert.Empty(errors);
        Assert.Empty(warnings);
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
