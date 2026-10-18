using System.Data;
using EOS.API.Data;
using EOS.API.Data.Bom;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 取试算表（导入明细）的真库验证：**子件版本的必填与存在性判据**。
///
/// 这条链路绕过效果引擎（直接 SQL 插入），所以保存期那条 `require-when-referenced`
/// 拦不住它——必须在导入入口自己守。四个用例对应四种行：
///   半成品缺版次 / 半成品版次不存在 / 材料填了版次 / 正常写入。
///
/// 造数<b>提交</b>而不是事务回滚：<see cref="BomRepository"/> 自己开连接，
/// 读不到未提交的数据。用独立前缀 + finally 清理，任何一步失败都不留残行。
/// </summary>
[Trait("Category", "Integration")]
[Collection("live-database")]
public sealed class BomImportLiveTests
{
    private static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("MSSQL_ERP_CONN")
        ?? throw new InvalidOperationException(
            "真库集成测试需要 MSSQL_ERP_CONN；未配置即失败（无连接跳过≠已验证）。");

    /// <summary>导入目标：一条 01 版的主表。</summary>
    private const string Root = "ADR41IMPMAIN";
    /// <summary>半成品：自己有 01 版 BOM，所以引用它时必须指定版次。</summary>
    private const string Sub = "ADR41IMPSUB";
    /// <summary>材料：没有 BOM，引用它时版次必须留空。</summary>
    private const string Raw = "ADR41IMPRAW";
    private const string Edition = "01";

    private static BomRepository CreateRepository() =>
        new(new DbConnectionFactory(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:ErpDatabase"] = ConnectionString,
            })
            .Build()));

    [Fact]
    public async Task 导入_半成品缺少子件版次被拒()
    {
        await ResetAsync();
        try
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                CreateRepository().ImportLinesAsync(Root, Edition, replace: false,
                    [new BomImportLine(Sub, ElementEdition: null, Qty: 2m, BaseQty: 1m, LostRate: 0m, Remark: null)],
                    "test", CancellationToken.None));

            Assert.Contains("有下阶 BOM", error.Message);
            Assert.Contains("必须指定子件版次", error.Message);
        }
        finally
        {
            await CleanupAsync();
        }
    }

    [Fact]
    public async Task 导入_子件版次不存在被拒()
    {
        await ResetAsync();
        try
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                CreateRepository().ImportLinesAsync(Root, Edition, replace: false,
                    [new BomImportLine(Sub, ElementEdition: "07", Qty: 2m, BaseQty: 1m, LostRate: 0m, Remark: null)],
                    "test", CancellationToken.None));

            Assert.Contains("版次 07 下没有 BOM", error.Message);
        }
        finally
        {
            await CleanupAsync();
        }
    }

    [Fact]
    public async Task 导入_材料填了子件版次被拒()
    {
        await ResetAsync();
        try
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                CreateRepository().ImportLinesAsync(Root, Edition, replace: false,
                    [new BomImportLine(Raw, ElementEdition: "01", Qty: 3m, BaseQty: 1m, LostRate: 0m, Remark: null)],
                    "test", CancellationToken.None));

            Assert.Contains("没有下阶 BOM", error.Message);
            Assert.Contains("必须留空", error.Message);
        }
        finally
        {
            await CleanupAsync();
        }
    }

    [Fact]
    public async Task 导入_正常写入并落库子件版次()
    {
        await ResetAsync();
        try
        {
            var imported = await CreateRepository().ImportLinesAsync(Root, Edition, replace: true,
                [
                    // 半成品：带 01 版
                    new BomImportLine(Sub, ElementEdition: "01", Qty: 2m, BaseQty: 1m, LostRate: 10m, Remark: null),
                    // 材料：版次留空
                    new BomImportLine(Raw, ElementEdition: null, Qty: 3m, BaseQty: 1m, LostRate: 0m, Remark: "注"),
                ],
                "test", CancellationToken.None);

            Assert.Equal(2, imported);

            var stored = await ReadStoredEditionsAsync();
            Assert.Equal("01", stored[Sub]);
            Assert.Null(stored[Raw]);
        }
        finally
        {
            await CleanupAsync();
        }
    }

    /// <summary>回读导入落库的子件版本，确认"写了什么就是什么"。</summary>
    private static async Task<Dictionary<string, string?>> ReadStoredEditionsAsync()
    {
        var stored = new Dictionary<string, string?>(StringComparer.Ordinal);
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand("""
            SELECT LTRIM(RTRIM(ELEMENT_PRO_NO)),
                   CASE WHEN ELEMENT_EDITION IS NULL THEN NULL ELSE LTRIM(RTRIM(ELEMENT_EDITION)) END
            FROM dbo.BOM_STRU_D WHERE PRO_NO = @pro AND EDITION = @edition;
            """, connection);
        command.Parameters.Add("@pro", SqlDbType.NVarChar, 60).Value = Root;
        command.Parameters.Add("@edition", SqlDbType.NVarChar, 20).Value = Edition;
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            stored[reader.GetString(0)] = reader.IsDBNull(1) ? null : reader.GetString(1);
        return stored;
    }

    private static async Task ResetAsync()
    {
        await CleanupAsync();
        await ExecuteAsync("""
            INSERT INTO dbo.PRODUCT (PRO_NO, PRO_NAME) VALUES
                (N'ADR41IMPMAIN', N'导入测试产品'),
                (N'ADR41IMPSUB', N'导入测试半成品'),
                (N'ADR41IMPRAW', N'导入测试材料');
            -- 目标 BOM（01 版）：导入往它里面补明细
            INSERT INTO dbo.BOM_STRU_M (PRO_NO, EDITION) VALUES (N'ADR41IMPMAIN', N'01');
            -- 半成品自己有 01 版 → 引用它就必须指定版次 01
            INSERT INTO dbo.BOM_STRU_M (PRO_NO, EDITION) VALUES (N'ADR41IMPSUB', N'01');
            """);
    }

    private static async Task CleanupAsync()
        => await ExecuteAsync("""
            DELETE FROM dbo.BOM_STRU_D WHERE PRO_NO IN (N'ADR41IMPMAIN', N'ADR41IMPSUB');
            DELETE FROM dbo.BOM_STRU_M WHERE PRO_NO IN (N'ADR41IMPMAIN', N'ADR41IMPSUB');
            DELETE FROM dbo.PRODUCT WHERE PRO_NO IN (N'ADR41IMPMAIN', N'ADR41IMPSUB', N'ADR41IMPRAW');
            """);

    private static async Task ExecuteAsync(string sql)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
