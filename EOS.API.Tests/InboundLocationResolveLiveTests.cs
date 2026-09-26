using System.Data;
using EOS.API.Data;
using EOS.API.Data.Effects;
using EOS.API.Models;
using EOS.API.Tests.Tools;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 「存放方式」接入移动引擎的真库验收（ADR-014 §3.12 / ADR-020 §10 WS-15），全程在事务内、结束回滚。
///
/// 走的是**真实批核链**：`WorkbenchApprovalService.RunApprovalCoreAsync` + 模块 130103（入库单）的
/// **已发布定义**（含真实的 inventory-move 动作与你库里那套校验），因此验的是接线本身，
/// 不是"把服务单独调一遍"。
///
/// 三条边界（与验收一一对应）：
///   ① `RANDOM` + 单据没给位置 ⇒ 批核后流水落**真实库位**（不是哨兵）；
///   ② `LOCATION_MODE = 0` ⇒ 即使存放方式是 `RANDOM`，**仍落哨兵**（档 0 是"不管位置"，
///      R1 等价性：系统不替人挑位置，否则会凭空改变库存键）；
///   ③ `FIXED` ⇒ 落**物料主货位**（`DEPOT_PRODUCT_LOCATION.IS_PRIMARY = 1`）。
/// </summary>
[Collection("live-database")]
public sealed class InboundLocationResolveLiveTests
{
    private static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("MSSQL_ERP_CONN")
        ?? throw new InvalidOperationException(
            "真库集成测试需要 MSSQL_ERP_CONN；未配置即失败（无连接跳过≠已验证）。");

    private const int InboundModule = 130103;          // 入库单
    private const string OccurType = "ADR20I";
    private const string TestDepot = "ADR20I01";
    private const string TestProduct = "ADR20IPRO1";
    private const string FreeLocation = "ADR20I-A1";   // 空位（SEQ_NO = 1）
    private const string PrimaryLocation = "ADR20I-B2"; // 主货位（SEQ_NO = 2）
    private const string Sentinel = "-";

    [Fact]
    public async Task 随机存放_入库单没给位置_批核落到真实库位()
    {
        var location = await ApproveInboundAsync(locationMode: 2, storageMode: "RANDOM", documentLocation: null);
        Assert.Equal(FreeLocation, location);
    }

    [Fact]
    public async Task 档0不管位置_即使随机存放_仍落哨兵()
    {
        var location = await ApproveInboundAsync(locationMode: 0, storageMode: "RANDOM", documentLocation: null);
        Assert.Equal(Sentinel, location);
    }

    [Fact]
    public async Task 固定存放_落到物料主货位()
    {
        var location = await ApproveInboundAsync(locationMode: 2, storageMode: "FIXED", documentLocation: null);
        Assert.Equal(PrimaryLocation, location);
    }

    /// <summary>单据给了位置就照单据走：解析器不改人填的东西。</summary>
    [Fact]
    public async Task 单据已给位置_解析器不改它()
    {
        var location = await ApproveInboundAsync(locationMode: 2, storageMode: "RANDOM", documentLocation: PrimaryLocation);
        Assert.Equal(PrimaryLocation, location);
    }

    /// <summary>
    /// WS-14 的第二条验收：**主货位表里没有这个物料时，解析回落哨兵且批核不报错**。
    /// 缺配置是配置缺口，不是调用方单据的失败——批核照样成功，只是位置落在"未指定位置"。
    /// （判别性：把回落去掉、改成抛异常 ⇒ 本用例会以 `Blocked`／异常变红。）
    /// </summary>
    [Fact]
    public async Task 主货位表里没有这个料号_回落哨兵且批核不报错()
    {
        var location = await ApproveInboundAsync(
            locationMode: 2, storageMode: "FIXED", documentLocation: null, withPrimary: false);
        Assert.Equal(Sentinel, location);
    }

    /// <summary>
    /// 建夹具 → 走真实批核链 → 读回「这一笔记账落在哪个位置」。批核在测试自己的事务里，结束回滚。
    /// </summary>
    private static async Task<string> ApproveInboundAsync(
        int locationMode, string storageMode, string? documentLocation, bool withPrimary = true)
    {
        var connections = Connections();
        var service = CreateService(connections);
        var occurNo = $"ADR20I{locationMode}{storageMode}{Random.Shared.Next(1000, 9999)}";

        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
        try
        {
            await SeedAsync(connection, transaction, locationMode, storageMode, occurNo, documentLocation, withPrimary);

            var definition = await LoadPublishedDefinitionAsync(connection, transaction, InboundModule);
            Assert.True(definition.EffectEngineEnabled, $"模块 {InboundModule} 的快照应已开启效果引擎（本用例前提）。");

            var result = await service.RunApprovalCoreAsync(
                connection, transaction, definition, [OccurType, occurNo],
                approve: true, "tester", "tester", CancellationToken.None);
            Assert.Null(result.Blocked);

            await using var command = new SqlCommand(
                "SELECT TOP 1 ISNULL(NULLIF(LTRIM(RTRIM(LOCATION_NO)), N''), N'-') FROM dbo.INV_DEPOT_LOG "
                + "WHERE MUTUALITY_TYPE = @t AND MUTUALITY_NO = @n AND IN_OUT = 'I';", connection, transaction);
            command.Parameters.Add("@t", SqlDbType.NVarChar, 20).Value = OccurType;
            command.Parameters.Add("@n", SqlDbType.NVarChar, 40).Value = occurNo;
            var value = await command.ExecuteScalarAsync();
            Assert.NotNull(value);
            return Convert.ToString(value)!.Trim();
        }
        finally
        {
            await transaction.RollbackAsync();
        }
    }

    private static async Task SeedAsync(
        SqlConnection connection, SqlTransaction transaction, int locationMode, string storageMode,
        string occurNo, string? documentLocation, bool withPrimary = true)
    {
        await ExecuteAsync(connection, transaction, """
            DELETE FROM dbo.INV_DEPOT_LOG WHERE MUTUALITY_TYPE = @Lt;
            DELETE FROM dbo.INV_OCCUR_IN_D WHERE OCCUR_TYPE = @Lt;
            DELETE FROM dbo.INV_OCCUR_IN_M WHERE OCCUR_TYPE = @Lt;
            DELETE FROM dbo.INV_PRO_DEPOT WHERE LTRIM(RTRIM(DEPOT_ID)) = @Depot OR LTRIM(RTRIM(PRO_NO)) = @Pro;
            DELETE FROM dbo.DEPOT_PRODUCT_LOCATION WHERE LTRIM(RTRIM(DEPOT_ID)) = @Depot;
            DELETE FROM dbo.DEPOT_STOCK_POLICY WHERE LTRIM(RTRIM(DEPOT_ID)) = @Depot;
            DELETE FROM dbo.DEPOT_LOCATION WHERE LTRIM(RTRIM(DEPOT_ID)) = @Depot;
            DELETE FROM dbo.PRODUCT WHERE LTRIM(RTRIM(PRO_NO)) = @Pro;
            """, ("@Lt", OccurType), ("@Depot", TestDepot), ("@Pro", TestProduct));

        await ExecuteAsync(connection, transaction, """
            IF NOT EXISTS (SELECT 1 FROM dbo.DEPOT WHERE LTRIM(RTRIM(DEPOT_ID)) = @Depot)
                INSERT INTO dbo.DEPOT (DEPOT_ID, DEPOT_NAME) VALUES (@Depot, N'ADR20I 存放方式测试仓');
            INSERT INTO dbo.PRODUCT (PRO_NO, PRO_NAME, PRO_SPEC, PRO_TYPE) VALUES (@Pro, N'ADR20I 料件', N'规格I', '3');
            -- 两个启用位置：空位在前（SEQ 1）、主货位在后（SEQ 2），顺序刻意错开以便区分"取第一个空位"与"取主货位"；
            -- 再加上每个库别都该有的哨兵位置行（"未指定位置"）：不加它，档 0 的单据连哨兵都过不了位置存在性校验
            INSERT INTO dbo.DEPOT_LOCATION
                (DEPOT_ID, LOCATION_NO, PARENT_NO, LOCATION_PATH, LOCATION_TYPE, LOCATION_NAME, STORAGE_TYPE, SEQ_NO, STATUS)
                VALUES (@Depot, N'-', NULL, N'/-', N'BIN', N'未指定位置（待归位）', NULL, 0, N'A'),
                       (@Depot, @Free, NULL, N'/' + @Free, N'BIN', N'ADR20I 空位', NULL, 1, N'A'),
                       (@Depot, @Primary, NULL, N'/' + @Primary, N'BIN', N'ADR20I 主货位', NULL, 2, N'A');
            """, ("@Depot", TestDepot), ("@Pro", TestProduct),
            ("@Free", FreeLocation), ("@Primary", PrimaryLocation));

        if (withPrimary)
        {
            await ExecuteAsync(connection, transaction, """
                INSERT INTO dbo.DEPOT_PRODUCT_LOCATION (DEPOT_ID, PRO_NO, LOCATION_NO, IS_PRIMARY, SEQ_NO)
                    VALUES (@Depot, @Pro, @Primary, 1, 1);
                """, ("@Depot", TestDepot), ("@Pro", TestProduct), ("@Primary", PrimaryLocation));
        }

        await ExecuteAsync(connection, transaction, """
            INSERT INTO dbo.DEPOT_STOCK_POLICY (DEPOT_ID, LOCATION_MODE, STORAGE_MODE)
                VALUES (@Depot, @Mode, @Storage);
            """, ("@Depot", TestDepot), ("@Mode", locationMode), ("@Storage", storageMode));

        await ExecuteAsync(connection, transaction, """
            INSERT INTO dbo.INV_OCCUR_IN_M
                (OCCUR_TYPE, OCCUR_NO, OCCUR_DATE, CONFIRM_TAG, CONFIRM_PERSON, CONFIRM_DATE,
                 CREATE_PERSON, CREATE_DATE, LAST_UPDATE_BY, LAST_UPDATE_DATE, CI)
                VALUES (@Lt, @No, CONVERT(datetime, '2026-09-01', 120), 0, NULL, NULL,
                        N'ADR20I', GETDATE(), N'ADR20I', GETDATE(), 'ADR20I');
            INSERT INTO dbo.INV_OCCUR_IN_D
                (OCCUR_TYPE, OCCUR_NO, SERIAL_NO, PRO_NO, DEPOT_ID, QTY, PRICE, LOCATION_NO)
                VALUES (@Lt, @No, 1, @Pro, @Depot, 1, 0, @Location);
            """, ("@Lt", OccurType), ("@No", occurNo), ("@Pro", TestProduct), ("@Depot", TestDepot),
            ("@Location", documentLocation));
    }

    private static DbConnectionFactory Connections()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:ErpDatabase"] = ConnectionString })
            .Build();
        return new DbConnectionFactory(configuration);
    }

    /// <summary>与既有真库用例同款：真实审计写入器 + 真实引擎（管线取自影子对拍 runner）。</summary>
    private static WorkbenchApprovalService CreateService(DbConnectionFactory connections)
    {
        var provider = new WorkbenchDefinitionProvider(connections, NullLogger<WorkbenchDefinitionProvider>.Instance);
        var auditWriter = new WorkbenchAuditWriter(
            connections, new Microsoft.AspNetCore.Http.HttpContextAccessor(), provider,
            Microsoft.Extensions.Options.Options.Create(new AuditSettings { FieldChangesEnabled = true }));
        var engine = new EffectEngineInvoker(
            new EffectEngineSettings { Enabled = true },
            new EffectPlanLoader(),
            EffectShadowRunner.BuildPipelineFor(ConnectionString),
            NullLogger<EffectEngineInvoker>.Instance);
        var workflow = new WorkflowEngine(connections, auditWriter, provider, engine, NullLogger<WorkflowEngine>.Instance);
        return new WorkbenchApprovalService(
            connections, auditWriter, workflow, engine, new WorkbenchIdempotency(),
            NullLogger<WorkbenchApprovalService>.Instance);
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
