using System.Data;
using System.Text.Json;
using EOS.API.Data;
using EOS.API.Data.Inventory;
using EOS.API.Data.Effects;
using EOS.API.Models;
using EOS.API.Tests.Tools;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 保存期「建议位置」的真库验收（D1-d / WS-16），全程在事务内、结束回滚。
///
/// 口径（ADR 已拍板 ⒝：**保存期解析并写入**）：
///   ① `LOCATION_MODE = 2`「建议」⇒ 明细行没填位置时，**保存时**就填入系统建议的位置（草稿上看得见、改得动）；
///   ② `LOCATION_MODE = 3`「强制」⇒**保存期不填也不拒**——"保存期拒绝会让草稿存不下来"是刻意不做的
///      （ADR D1-a），替它填上则等于把「强制」悄悄降级成「建议」，两条都不可以；
///   ③ `LOCATION_MODE = 0 / 1` ⇒ 位置由人定，系统不替人挑（R1 等价性）；
///   ④ 单据已给位置 ⇒ 不动它（与人填的东西冲突时，人赢）。
///
/// 用的是模块 130103（入库单）的**已发布定义**，因此 `inventory-move` 的 `depotField`/`direction`
/// 都是从真实配置里读出来的，不是测试代码里写死的。
/// </summary>
[Trait("Category", "Integration")]
[Collection("live-database")]
public sealed class DepotLocationSuggestionLiveTests
{
    private static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("MSSQL_ERP_CONN")
        ?? throw new InvalidOperationException(
            "真库集成测试需要 MSSQL_ERP_CONN；未配置即失败（无连接跳过≠已验证）。");

    private const int InboundModule = 130103;
    private const string TestDepot = "ADR20K01";
    private const string TestProduct = "ADR20KPRO1";
    private const string FreeLocation = "ADR20K-A1";
    private const string PrimaryLocation = "ADR20K-B2";
    private const string Sentinel = "-";

    [Fact]
    public async Task 档2建议_保存期把建议位置写进明细行()
    {
        var result = await FillAsync(locationMode: 2, storageMode: "RANDOM", documentLocation: null);
        var row = Assert.Single(result.Details!);
        Assert.Equal(FreeLocation, row["LOCATION_NO"]);
        var warning = Assert.Single(result.Warnings);
        Assert.Equal("LOCATION_SUGGESTED", warning.Code);
        Assert.Contains(FreeLocation, warning.Message);
    }

    [Fact]
    public async Task 档3强制_保存期既不填也不拒()
    {
        var result = await FillAsync(locationMode: 3, storageMode: "RANDOM", documentLocation: null);
        var row = Assert.Single(result.Details!);
        Assert.True(string.IsNullOrWhiteSpace(row["LOCATION_NO"]), "档 3 保存期不该替人填位置。");
        Assert.Empty(result.Warnings);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task 档0与档1_位置由人定_保存期不填(int locationMode)
    {
        var result = await FillAsync(locationMode, "RANDOM", documentLocation: null);
        Assert.True(string.IsNullOrWhiteSpace(Assert.Single(result.Details!)["LOCATION_NO"]));
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public async Task 单据已给位置_保存期不动它()
    {
        var result = await FillAsync(locationMode: 2, storageMode: "RANDOM", documentLocation: PrimaryLocation);
        Assert.Equal(PrimaryLocation, Assert.Single(result.Details!)["LOCATION_NO"]);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public async Task 档2配固定存放_保存期填物料主货位()
    {
        var result = await FillAsync(locationMode: 2, storageMode: "FIXED", documentLocation: null);
        Assert.Equal(PrimaryLocation, Assert.Single(result.Details!)["LOCATION_NO"]);
    }

    /// <summary>建夹具 → 调保存期的建议位置 → 返回结论（数据都在测试自己的事务里，结束回滚）。</summary>
    private static async Task<DepotLocationSuggestionService.SuggestionResult> FillAsync(
        int locationMode, string storageMode, string? documentLocation)
    {
        var connections = Connections();
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
        try
        {
            await SeedAsync(connection, transaction, locationMode, storageMode);

            var definition = await LoadPublishedDefinitionAsync(connection, transaction, InboundModule);
            var auditWriter = new WorkbenchAuditWriter(
                connections, new Microsoft.AspNetCore.Http.HttpContextAccessor(),
                new WorkbenchDefinitionProvider(connections, NullLogger<WorkbenchDefinitionProvider>.Instance),
                Microsoft.Extensions.Options.Options.Create(new AuditSettings()));
            var policies = new DepotStockPolicyService(connections, auditWriter);

            var details = new List<IReadOnlyDictionary<string, string?>>
            {
                new Dictionary<string, string?>
                {
                    ["SERIAL_NO"] = "1",
                    ["PRO_NO"] = TestProduct,
                    ["DEPOT_ID"] = TestDepot,
                    ["QTY"] = "1",
                    ["LOCATION_NO"] = documentLocation,
                }
            };

            return await DepotLocationSuggestionService.FillAsync(
                connection, transaction, definition, details, policies, CancellationToken.None);
        }
        finally
        {
            await transaction.RollbackAsync();
        }
    }

    private static async Task SeedAsync(
        SqlConnection connection, SqlTransaction transaction, int locationMode, string storageMode)
    {
        await ExecuteAsync(connection, transaction, """
            DELETE FROM dbo.DEPOT_PRODUCT_LOCATION WHERE LTRIM(RTRIM(DEPOT_ID)) = @Depot;
            DELETE FROM dbo.DEPOT_STOCK_POLICY WHERE LTRIM(RTRIM(DEPOT_ID)) = @Depot;
            DELETE FROM dbo.DEPOT_LOCATION WHERE LTRIM(RTRIM(DEPOT_ID)) = @Depot;
            DELETE FROM dbo.PRODUCT WHERE LTRIM(RTRIM(PRO_NO)) = @Pro;
            """, ("@Depot", TestDepot), ("@Pro", TestProduct));

        await ExecuteAsync(connection, transaction, """
            IF NOT EXISTS (SELECT 1 FROM dbo.DEPOT WHERE LTRIM(RTRIM(DEPOT_ID)) = @Depot)
                INSERT INTO dbo.DEPOT (DEPOT_ID, DEPOT_NAME) VALUES (@Depot, N'ADR20K 建议位置测试仓');
            INSERT INTO dbo.PRODUCT (PRO_NO, PRO_NAME, PRO_SPEC, PRO_TYPE) VALUES (@Pro, N'ADR20K 料件', N'规格K', '3');
            INSERT INTO dbo.DEPOT_LOCATION
                (DEPOT_ID, LOCATION_NO, PARENT_NO, LOCATION_PATH, LOCATION_TYPE, LOCATION_NAME, STORAGE_TYPE, SEQ_NO, STATUS)
                VALUES (@Depot, N'-', NULL, N'/-', N'BIN', N'未指定位置（待归位）', NULL, 0, N'A'),
                       (@Depot, @Free, NULL, N'/' + @Free, N'BIN', N'ADR20K 空位', NULL, 1, N'A'),
                       (@Depot, @Primary, NULL, N'/' + @Primary, N'BIN', N'ADR20K 主货位', NULL, 2, N'A');
            INSERT INTO dbo.DEPOT_PRODUCT_LOCATION (DEPOT_ID, PRO_NO, LOCATION_NO, IS_PRIMARY, SEQ_NO)
                VALUES (@Depot, @Pro, @Primary, 1, 1);
            """, ("@Depot", TestDepot), ("@Pro", TestProduct),
            ("@Free", FreeLocation), ("@Primary", PrimaryLocation));

        await ExecuteAsync(connection, transaction, """
            INSERT INTO dbo.DEPOT_STOCK_POLICY (DEPOT_ID, LOCATION_MODE, STORAGE_MODE)
                VALUES (@Depot, @Mode, @Storage);
            """, ("@Depot", TestDepot), ("@Mode", locationMode), ("@Storage", storageMode));
    }

    private static DbConnectionFactory Connections()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:ErpDatabase"] = ConnectionString })
            .Build();
        return new DbConnectionFactory(configuration);
    }

    private static async Task<WorkbenchDefinition> LoadPublishedDefinitionAsync(
        SqlConnection connection, SqlTransaction transaction, int moduleId)
    {
        await using var command = new SqlCommand(
            "SELECT TOP 1 DEFINITION_JSON FROM dbo.WORKBENCH_DEFINITION_SNAPSHOT WHERE M_IDX=@Id AND IS_CURRENT=1;",
            connection, transaction);
        command.Parameters.Add("@Id", SqlDbType.Int).Value = moduleId;
        var json = await command.ExecuteScalarAsync() as string
            ?? throw new InvalidOperationException($"模块 {moduleId} 无已发布快照。");
        return JsonSerializer.Deserialize<WorkbenchDefinition>(json, WorkbenchDefinitionProvider.JsonOptions)!;
    }

    private static async Task ExecuteAsync(
        SqlConnection connection, SqlTransaction transaction, string sql,
        params (string Name, object? Value)[] parameters)
    {
        await using var command = new SqlCommand(sql, connection, transaction);
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        await command.ExecuteNonQueryAsync();
    }
}
