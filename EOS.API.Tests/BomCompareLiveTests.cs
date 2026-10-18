using System.Data;
using EOS.API.Data;
using EOS.API.Data.Bom;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 产品 BOM 版本对比（模块 1204）的真库验证。
///
/// 这个用例钉住的是一处真实的 500：明细表 <c>BASE_QTY</c> 是 int，
/// 对比读取时按 decimal 取会抛 <c>InvalidCastException</c>，而版本对比页
/// 正是从这条路径读数的。断言同时覆盖新增行与未变行，避免修完只对一半。
/// </summary>
[Trait("Category", "Integration")]
[Collection("live-database")]
public sealed class BomCompareLiveTests
{
    private static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("MSSQL_ERP_CONN")
        ?? throw new InvalidOperationException(
            "真库集成测试需要 MSSQL_ERP_CONN；未配置即失败（无连接跳过≠已验证）。");

    private const string Root = "ADR39BOMCMPROOT";
    private const string Sub = "ADR39BOMCMPSUB";
    private const string New = "ADR39BOMCMPNEW";
    private const string EditionA = "01";
    private const string EditionB = "02";

    private static BomRepository CreateRepository() =>
        new(new DbConnectionFactory(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:ErpDatabase"] = ConnectionString,
            })
            .Build()));

    [Fact]
    public async Task 版本对比_新增子件标为added_未变子件标为same()
    {
        var token = CancellationToken.None;
        await CleanupAsync(token);
        try
        {
            await SeedAsync(token);
            var repository = CreateRepository();

            var result = await repository.CompareAsync(Root, EditionA, EditionB, token);

            Assert.Equal(2, result.Rows.Count);
            var sub = Assert.Single(result.Rows, row => row.ProNo == Sub);
            Assert.Equal("same", sub.Status);
            var added = Assert.Single(result.Rows, row => row.ProNo == New);
            Assert.Equal("added", added.Status);
            Assert.Null(added.SerialNoA);
            Assert.Equal((short)2, added.SerialNoB);
        }
        finally
        {
            await CleanupAsync(token);
        }
    }

    private static async Task SeedAsync(CancellationToken token)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(token);

        foreach (var (no, name) in new[] { (Root, "对比根"), (Sub, "对比半成品"), (New, "对比新材料") })
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

        foreach (var (no, edition) in new[] { (Root, EditionA), (Root, EditionB), (Sub, EditionA) })
        {
            await using var master = new SqlCommand(
                """
                INSERT INTO dbo.BOM_STRU_M (PRO_NO, EDITION, BATCH_QTY)
                VALUES (@no, @edition, 1);
                """, connection);
            master.Parameters.Add("@no", SqlDbType.NVarChar, 60).Value = no;
            master.Parameters.Add("@edition", SqlDbType.NVarChar, 20).Value = edition;
            await master.ExecuteNonQueryAsync(token);
        }

        // 01 版只有 SUB；02 版在 01 的基础上新增 NEW。SUB 自己有 01 版 BOM，
        // 所以它作为子件时必须带子件版本；NEW 是材料，子件版本留空。
        await InsertDetailAsync(connection, Root, EditionA, 1, Sub, EditionA, 1m, token);
        await InsertDetailAsync(connection, Root, EditionB, 1, Sub, EditionA, 1m, token);
        await InsertDetailAsync(connection, Root, EditionB, 2, New, null, 2m, token);
        await InsertDetailAsync(connection, Sub, EditionA, 1, New, null, 1m, token);
    }

    private static async Task InsertDetailAsync(
        SqlConnection connection, string parent, string edition, short serial,
        string child, string? childEdition, decimal qty, CancellationToken token)
    {
        await using var command = new SqlCommand(
            """
            INSERT INTO dbo.BOM_STRU_D
                (PRO_NO, EDITION, SERIAL_NO, ELEMENT_PRO_NO, ELEMENT_EDITION, ELEMENT_QTY, BASE_QTY, LOST_RATE)
            VALUES (@parent, @edition, @serial, @child, @childEdition, @qty, 1, 0);
            """, connection);
        command.Parameters.Add("@parent", SqlDbType.NVarChar, 60).Value = parent;
        command.Parameters.Add("@edition", SqlDbType.NVarChar, 20).Value = edition;
        command.Parameters.Add("@serial", SqlDbType.SmallInt).Value = serial;
        command.Parameters.Add("@child", SqlDbType.NVarChar, 60).Value = child;
        command.Parameters.Add("@childEdition", SqlDbType.NVarChar, 20).Value =
            string.IsNullOrWhiteSpace(childEdition) ? DBNull.Value : childEdition;
        command.Parameters.Add("@qty", SqlDbType.Decimal).Value = qty;
        await command.ExecuteNonQueryAsync(token);
    }

    private static async Task CleanupAsync(CancellationToken token)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(token);
        const string sql = """
            DELETE FROM dbo.BOM_STRU_D WHERE PRO_NO IN (@root, @sub) OR ELEMENT_PRO_NO IN (@root, @sub, @new);
            DELETE FROM dbo.BOM_STRU_M WHERE PRO_NO IN (@root, @sub);
            DELETE FROM dbo.PRODUCT WHERE PRO_NO IN (@root, @sub, @new);
            """;
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@root", SqlDbType.NVarChar, 60).Value = Root;
        command.Parameters.Add("@sub", SqlDbType.NVarChar, 60).Value = Sub;
        command.Parameters.Add("@new", SqlDbType.NVarChar, 60).Value = New;
        await command.ExecuteNonQueryAsync(token);
    }
}
