using System.Data;
using EOS.API.Data;
using EOS.API.Data.Bom;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 产品 BOM 结构查询（模块 1204）的真库验证：多层展开、毛需求、反查用途。
///
/// 造一棵三层的树：ROOT →(2, 损耗 10%) SUB →(3, 损耗 20%) LEAF。
/// 断言的是三件容易做错的事：
///   ① 层级与累计用量（2、6 而不是 2、3）；
///   ② 损耗沿路径<b>累乘</b>（1.1 × 1.2 = 1.32，不是 1.1 + 1.2 或只取最后一层）；
///   ③ 毛需求只出叶子，且反查能沿父链向上走。
///
/// 造数<b>提交</b>而不是在事务里回滚：<see cref="BomRepository"/> 自己开连接，
/// 读不到未提交的数据。因此用独立前缀 + finally 清理，任何一步失败都不会留下残行。
/// </summary>
[Trait("Category", "Integration")]
[Collection("live-database")]
public sealed class BomExpansionLiveTests
{
    private static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("MSSQL_ERP_CONN")
        ?? throw new InvalidOperationException(
            "真库集成测试需要 MSSQL_ERP_CONN；未配置即失败（无连接跳过≠已验证）。");

    private const string Root = "ADR39BOMROOT";
    private const string Sub = "ADR39BOMSUB";
    private const string Leaf = "ADR39BOMLEAF";
    private const string Edition = "01";

    private static BomRepository CreateRepository() =>
        new(new DbConnectionFactory(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:ErpDatabase"] = ConnectionString,
            })
            .Build()));

    [Fact]
    public async Task 三层BOM_展开累计用量与损耗累乘()
    {
        var token = CancellationToken.None;
        await CleanupAsync(token);
        try
        {
            await SeedAsync(token);
            var repository = CreateRepository();

            var rows = await repository.ExpandAsync(Root, Edition, BomRepository.DefaultTreeDepth, token);

            // 根本身不出现在结果里：TREE 只装子件。
            Assert.Equal(2, rows.Count);

            var sub = rows[0];
            Assert.Equal(1, sub.Level);
            Assert.Equal(Sub, sub.ProNo);
            Assert.Equal(2m, decimal.Round(sub.QtyPer, 6));
            Assert.Equal(2m, decimal.Round(sub.QtyCum, 6));
            Assert.Equal(1.1m, decimal.Round(sub.LostFactor, 6));
            Assert.True(sub.HasChildren, "SUB 自己维护了 BOM，应被标为可继续展开");
            Assert.False(sub.IsFallback);

            var leaf = rows[1];
            Assert.Equal(2, leaf.Level);
            Assert.Equal(Leaf, leaf.ProNo);
            Assert.Equal(3m, decimal.Round(leaf.QtyPer, 6));
            // 累计用量是逐层相乘：2 × 3 = 6
            Assert.Equal(6m, decimal.Round(leaf.QtyCum, 6));
            // 损耗逐层累乘：1.1 × 1.2 = 1.32
            Assert.Equal(1.32m, decimal.Round(leaf.LostFactor, 6));
            Assert.False(leaf.HasChildren);
        }
        finally
        {
            await CleanupAsync(token);
        }
    }

    [Fact]
    public async Task 三层BOM_毛需求只出叶子且含累乘损耗()
    {
        var token = CancellationToken.None;
        await CleanupAsync(token);
        try
        {
            await SeedAsync(token);
            var repository = CreateRepository();

            var rows = await repository.GrossRequirementAsync(Root, Edition, BomRepository.DefaultTreeDepth, token);

            // 只有 LEAF 是叶子；SUB 自己有 BOM，不作为采购需求出现。
            var row = Assert.Single(rows);
            Assert.Equal(Leaf, row.ProNo);
            Assert.Equal(6m, decimal.Round(row.QtyPerUnit, 6));
            Assert.Equal(7.92m, decimal.Round(row.QtyPerUnitWithLoss, 6));
            Assert.Equal(1.92m, decimal.Round(row.LostQtyPerUnit, 6));
        }
        finally
        {
            await CleanupAsync(token);
        }
    }

    [Fact]
    public async Task 三层BOM_反查用途沿父链向上()
    {
        var token = CancellationToken.None;
        await CleanupAsync(token);
        try
        {
            await SeedAsync(token);
            var repository = CreateRepository();

            // 不给版次：各父件取自身最大版次（造的数据只有 01）。
            var rows = await repository.WhereUsedAsync(Leaf, string.Empty, BomRepository.DefaultWhereUsedDepth, token);

            Assert.Equal(2, rows.Count);
            Assert.Contains(rows, row => row.ParentNo == Sub && row.Level == 0);
            Assert.Contains(rows, row => row.ParentNo == Root && row.Level == 1);
        }
        finally
        {
            await CleanupAsync(token);
        }
    }

    [Fact]
    public async Task 不存在的料号_根解析返回空而不是空树()
    {
        var token = CancellationToken.None;
        var repository = CreateRepository();

        Assert.False(await repository.ProductExistsAsync("ADR39BOMNONE", token));
        // 区分"没有 BOM"与"BOM 是空的"：调用方据此给 404 而不是空数组。
        Assert.Null(await repository.ResolveEditionAsync("ADR39BOMNONE", null, token));
    }

    private static async Task SeedAsync(CancellationToken token)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(token);

        foreach (var (no, name) in new[] { (Root, "三层根"), (Sub, "半成品"), (Leaf, "原料") })
        {
            await using var product = new SqlCommand(
                """
                INSERT INTO dbo.PRODUCT (PRO_NO, PRO_NAME, UNIT_ID)
                VALUES (@no, @name, N'PCS');
                """, connection);
            product.Parameters.Add("@no", SqlDbType.NVarChar, 60).Value = no;
            product.Parameters.Add("@name", SqlDbType.NVarChar, 100).Value = name;
            await product.ExecuteNonQueryAsync(token);
        }

        foreach (var no in new[] { Root, Sub })
        {
            await using var master = new SqlCommand(
                """
                INSERT INTO dbo.BOM_STRU_M (PRO_NO, EDITION, BATCH_QTY)
                VALUES (@no, @edition, 1);
                """, connection);
            master.Parameters.Add("@no", SqlDbType.NVarChar, 60).Value = no;
            master.Parameters.Add("@edition", SqlDbType.NVarChar, 20).Value = Edition;
            await master.ExecuteNonQueryAsync(token);
        }

        // ROOT → SUB：用量 2，损耗 10%；SUB 自己有 01 版 BOM，所以子件版本写 01
        await InsertDetailAsync(connection, Root, 1, Sub, 2m, 10m, token, childEdition: Edition);
        // SUB → LEAF：用量 3，损耗 20%；LEAF 是材料，没有 BOM，子件版本留空
        await InsertDetailAsync(connection, Sub, 1, Leaf, 3m, 20m, token);
    }

    /// <summary>
    /// 插一行明细。<paramref name="childEdition"/> 是**子件自己的** BOM 版次：
    /// 半成品必须给（否则展开到此为止），材料必须留空（它没有 BOM）。
    /// </summary>
    private static async Task InsertDetailAsync(
        SqlConnection connection, string parent, short serial, string child,
        decimal qty, decimal lostRate, CancellationToken token, string? childEdition = null)
    {
        await using var command = new SqlCommand(
            """
            INSERT INTO dbo.BOM_STRU_D
                (PRO_NO, EDITION, SERIAL_NO, ELEMENT_PRO_NO, ELEMENT_EDITION, ELEMENT_QTY, BASE_QTY, LOST_RATE)
            VALUES (@parent, @edition, @serial, @child, @childEdition, @qty, 1, @lost);
            """, connection);
        command.Parameters.Add("@parent", SqlDbType.NVarChar, 60).Value = parent;
        command.Parameters.Add("@edition", SqlDbType.NVarChar, 20).Value = Edition;
        command.Parameters.Add("@serial", SqlDbType.SmallInt).Value = serial;
        command.Parameters.Add("@child", SqlDbType.NVarChar, 60).Value = child;
        command.Parameters.Add("@childEdition", SqlDbType.NVarChar, 20).Value =
            string.IsNullOrWhiteSpace(childEdition) ? DBNull.Value : childEdition;
        command.Parameters.Add("@qty", SqlDbType.Decimal).Value = qty;
        command.Parameters.Add("@lost", SqlDbType.Decimal).Value = lostRate;
        await command.ExecuteNonQueryAsync(token);
    }

    /// <summary>按前缀清理：先明细后主表再产品，任何一步都不依赖上一步成功。</summary>
    private static async Task CleanupAsync(CancellationToken token)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(token);
        const string sql = """
            DELETE FROM dbo.BOM_STRU_D WHERE PRO_NO IN (@root, @sub) OR ELEMENT_PRO_NO IN (@root, @sub, @leaf);
            DELETE FROM dbo.BOM_STRU_M WHERE PRO_NO IN (@root, @sub);
            DELETE FROM dbo.PRODUCT WHERE PRO_NO IN (@root, @sub, @leaf);
            """;
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@root", SqlDbType.NVarChar, 60).Value = Root;
        command.Parameters.Add("@sub", SqlDbType.NVarChar, 60).Value = Sub;
        command.Parameters.Add("@leaf", SqlDbType.NVarChar, 60).Value = Leaf;
        await command.ExecuteNonQueryAsync(token);
    }
}
