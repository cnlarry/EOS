using System.Data;
using System.Text.Json;
using EOS.API.Data.Effects;
using EOS.API.Data.Effects.ServiceEffectHandlers;
using Microsoft.Data.SqlClient;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 库位主档的两条地基约束：
/// ① **物化路径**（`LOCATION_PATH`）——改挂上级后自身与整棵子树的路径必须同步；
///    把节点挂到自己的后代下必须被拒（成环会让按路径前缀的子树查询无限展开）。
/// ② **哨兵行**（`LOCATION_NO='-'`）——新库别必须自动获得它；它不允许被改形态、也不允许被删除。
///
/// 真库用例，需 <c>MSSQL_ERP_CONN</c>；全程在一个事务内建数、断言、回滚。
/// </summary>
[Trait("Category", "live-database")]
[Collection("live-database")]
public sealed class DepotLocationMasterLiveTests
{
    private const string Depot = "ADR26DP";
    private const string ZoneA = "A";       // 根
    private const string RackA = "A-R1";    // A 的子
    private const string BinA = "A-R1-B1";  // A-R1 的子
    private const string ZoneB = "B";       // 另一个根

    private static string RequireConnection()
    {
        var value = Environment.GetEnvironmentVariable("MSSQL_ERP_CONN");
        Assert.False(string.IsNullOrWhiteSpace(value),
            "真库集成测试需要 MSSQL_ERP_CONN；未配置即失败（无连接跳过≠已验证）。");
        return value!;
    }

    // ---------- P2-07 路径重算 ----------

    [Fact]
    public async Task 移动中间节点后自身与整棵子树的路径同步更新()
    {
        var result = await RunPathAsync(locationNo: RackA, parentNo: ZoneB);

        Assert.Equal(2, result.Affected);                     // 自身 + 1 个后代
        Assert.Equal("/B/A-R1", result.PathOf(RackA));
        Assert.Equal("/B/A-R1/A-R1-B1", result.PathOf(BinA));  // 后代跟着走
        Assert.Equal("/A", result.PathOf(ZoneA));              // 原上级不受影响
    }

    [Fact]
    public async Task 路径未变化时不产生更新()
    {
        var result = await RunPathAsync(locationNo: RackA, parentNo: ZoneA);
        Assert.Equal(0, result.Affected);
        Assert.Equal("/A/A-R1", result.PathOf(RackA));
    }

    /// <summary>
    /// 新建行的路径先是列默认值（空串）。旧前缀为空时绝不能用它去匹配后代——
    /// 空串拼上 '/' 会匹配到每一个以 '/' 开头的路径，把整张表的路径全部改写。
    /// </summary>
    [Fact]
    public async Task 新建行路径尚为空串时不得改写其它行()
    {
        var result = await RunPathAsync(locationNo: "NEW", parentNo: ZoneA, insertWithPath: string.Empty);

        Assert.Equal("/A/NEW", result.PathOf("NEW"));   // 自身按上级算出
        Assert.Equal(1, result.Affected);               // 只有自身
        Assert.Equal("/A", result.PathOf(ZoneA));       // 其余行一律不动
        Assert.Equal("/B", result.PathOf(ZoneB));
        Assert.Equal("/A/A-R1", result.PathOf(RackA));
        Assert.Equal("/A/A-R1/A-R1-B1", result.PathOf(BinA));
    }

    [Fact]
    public async Task 把节点挂到自己的下级之下被拒()
    {
        // A-R1 挂到自己的子节点 A-R1-B1 下 ⇒ 成环
        var failure = await Assert.ThrowsAsync<EffectValidationException>(
            () => RunPathAsync(locationNo: RackA, parentNo: BinA));
        Assert.Contains("循环", failure.Message);
    }

    [Fact]
    public async Task 把节点挂到自己之下被拒()
    {
        var failure = await Assert.ThrowsAsync<EffectValidationException>(
            () => RunPathAsync(locationNo: RackA, parentNo: RackA));
        Assert.Contains("循环", failure.Message);
    }

    // ---------- P2-06 哨兵行 ----------

    [Fact]
    public async Task 哨兵行被停用或改父时保存被拒()
    {
        var message = await RunGuardAsync(mutate: "UPDATE dbo.DEPOT_LOCATION SET STATUS=N'I' WHERE DEPOT_ID=@d AND LOCATION_NO=N'-'");
        Assert.NotNull(message);
        Assert.Contains("哨兵行", message!);

        var parentMessage = await RunGuardAsync(
            mutate: "UPDATE dbo.DEPOT_LOCATION SET PARENT_NO=N'A' WHERE DEPOT_ID=@d AND LOCATION_NO=N'-'");
        Assert.NotNull(parentMessage);
        Assert.Contains("上级库位应为空", parentMessage!);
    }

    [Fact]
    public async Task 哨兵行形态未被改动时保存放行()
    {
        var message = await RunGuardAsync(mutate: "UPDATE dbo.DEPOT_LOCATION SET LOCATION_NAME=N'改个名字' WHERE DEPOT_ID=@d AND LOCATION_NO=N'-'");
        Assert.Null(message);
    }

    [Fact]
    public async Task 非哨兵行不受哨兵守卫影响()
    {
        var message = await RunGuardAsync(
            mutate: "UPDATE dbo.DEPOT_LOCATION SET STATUS=N'I' WHERE DEPOT_ID=@d AND LOCATION_NO=@bin",
            subjectLocation: BinA);
        Assert.Null(message);
    }

    [Fact]
    public async Task 删除哨兵行被拒()
    {
        var connectionString = RequireConnection();
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
        try
        {
            await SeedAsync(connection, transaction, withSentinel: true);
            var message = await DepotLocationDeleteGuard.CheckAsync(
                new CustomValidationContext(connection, transaction, LocationPlan(), [Depot, "-"]),
                GuardParams(), CancellationToken.None);
            Assert.NotNull(message);
            Assert.Contains("不允许删除", message!);
        }
        finally
        {
            await transaction.RollbackAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task 新建库别自动产生哨兵行且重复执行不重复插入()
    {
        var connectionString = RequireConnection();
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
        try
        {
            // 建库别但不建哨兵行（模拟"新增库别"这一时刻）
            await SeedAsync(connection, transaction, withSentinel: false);
            Assert.Equal(0, await CountSentinelAsync(connection, transaction));

            var handler = new DepotSentinelLocationHandler();
            var action = SentinelActionPlan();
            var depotPlan = new ModuleEffectPlan(110306, "DEPOT", null, "adr26", ["DEPOT_ID"], [action], []);
            var context = new ServiceEffectContext(connection, transaction, depotPlan, action,
                EffectEvent.Save, Depot, [Depot], "adr26-test");

            Assert.Equal(1, await handler.ExecuteAsync(context, CancellationToken.None));
            Assert.Equal(1, await CountSentinelAsync(connection, transaction));
            // 幂等：再跑一次不新增
            Assert.Equal(0, await handler.ExecuteAsync(context, CancellationToken.None));
            Assert.Equal(1, await CountSentinelAsync(connection, transaction));

            var row = await ReadSentinelShapeAsync(connection, transaction);
            Assert.Equal("/-", row.Path);
            Assert.Equal("BIN", row.Type);
            Assert.Equal("A", row.Status);
        }
        finally
        {
            await transaction.RollbackAsync(CancellationToken.None);
        }
    }

    // ---------- 夹具 ----------

    private sealed record PathResult(int Affected, IReadOnlyDictionary<string, string> Paths)
    {
        public string PathOf(string location) => Paths[location];
    }

    private static async Task<PathResult> RunPathAsync(
        string locationNo, string parentNo, string? insertWithPath = null)
    {
        var connectionString = RequireConnection();
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
        try
        {
            await SeedAsync(connection, transaction, withSentinel: true);
            if (insertWithPath is null)
            {
                await ExecuteAsync(connection, transaction,
                    "UPDATE dbo.DEPOT_LOCATION SET PARENT_NO=@parent WHERE DEPOT_ID=@d AND LOCATION_NO=@self",
                    ("@parent", parentNo), ("@d", Depot), ("@self", locationNo));
            }
            else
            {
                // 模拟"刚插入、路径还是列默认值"的那一行
                await ExecuteAsync(connection, transaction,
                    "INSERT INTO dbo.DEPOT_LOCATION (DEPOT_ID, LOCATION_NO, PARENT_NO, LOCATION_PATH, LOCATION_TYPE, STATUS) "
                    + "VALUES (@d, @self, @parent, @path, N'BIN', N'A')",
                    ("@d", Depot), ("@self", locationNo), ("@parent", parentNo), ("@path", insertWithPath));
            }

            var affected = await new LocationPathRecalcHandler().ExecuteAsync(
                new ServiceEffectContext(connection, transaction, LocationPlan(), PathActionPlan(),
                    EffectEvent.Save, $"{Depot},{locationNo}", [Depot, locationNo], "adr26-test"),
                CancellationToken.None);

            var paths = new Dictionary<string, string>();
            await using var command = new SqlCommand(
                "SELECT LOCATION_NO, LOCATION_PATH FROM dbo.DEPOT_LOCATION WHERE DEPOT_ID=@d", connection, transaction);
            command.Parameters.AddWithValue("@d", Depot);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                paths[reader.GetString(0).Trim()] = reader.GetString(1).Trim();
            return new PathResult(affected, paths);
        }
        finally
        {
            await transaction.RollbackAsync(CancellationToken.None);
        }
    }

    private static async Task<string?> RunGuardAsync(string mutate, string? subjectLocation = null)
    {
        var connectionString = RequireConnection();
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
        try
        {
            await SeedAsync(connection, transaction, withSentinel: true);
            await ExecuteAsync(connection, transaction, mutate, ("@d", Depot), ("@bin", BinA));
            return await DepotLocationGuard.CheckAsync(
                new CustomValidationContext(connection, transaction, LocationPlan(), [Depot, subjectLocation ?? "-"]),
                GuardParams(), CancellationToken.None);
        }
        finally
        {
            await transaction.RollbackAsync(CancellationToken.None);
        }
    }

    private static ModuleEffectPlan LocationPlan() =>
        new(110309, "DEPOT_LOCATION", null, "adr26", ["DEPOT_ID", "LOCATION_NO"], [PathActionPlan()], []);

    private static EffectActionPlan PathActionPlan() =>
        new(1, "SAVE", "location-path-recalc", "库位路径重算", true, "BLOCK", null, PathAction(), null,
            Array.Empty<EffectOpPlan>());

    private static EffectActionPlan SentinelActionPlan() =>
        new(1, "SAVE", "depot-sentinel-location", "库位哨兵行自动创建", true, "BLOCK", null, SentinelAction(), null,
            Array.Empty<EffectOpPlan>());

    private static JsonElement PathAction() => JsonSerializer.SerializeToElement(new
    {
        table = "DEPOT_LOCATION", depotField = "DEPOT_ID", locationField = "LOCATION_NO",
        parentField = "PARENT_NO", pathField = "LOCATION_PATH",
    });

    private static JsonElement SentinelAction() => JsonSerializer.SerializeToElement(new
    {
        table = "DEPOT_LOCATION", depotField = "DEPOT_ID", locationField = "LOCATION_NO",
        parentField = "PARENT_NO", pathField = "LOCATION_PATH", typeField = "LOCATION_TYPE",
        nameField = "LOCATION_NAME", seqField = "SEQ_NO", sentinelName = "未指定位置（待归位）",
    });

    private static JsonElement GuardParams() => JsonSerializer.SerializeToElement(new
    {
        table = "DEPOT_LOCATION", depotField = "DEPOT_ID", locationField = "LOCATION_NO",
        message = "未指定位置（哨兵行）必须保持原样：", deleteMessage = "未指定位置（哨兵行）不允许删除。",
    });

    private static async Task<int> CountSentinelAsync(SqlConnection connection, SqlTransaction transaction)
    {
        await using var command = new SqlCommand(
            "SELECT COUNT(*) FROM dbo.DEPOT_LOCATION WHERE DEPOT_ID=@d AND LOCATION_NO=N'-'", connection, transaction);
        command.Parameters.AddWithValue("@d", Depot);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static async Task<(string Path, string Type, string Status)> ReadSentinelShapeAsync(
        SqlConnection connection, SqlTransaction transaction)
    {
        await using var command = new SqlCommand(
            "SELECT LOCATION_PATH, LOCATION_TYPE, STATUS FROM dbo.DEPOT_LOCATION WHERE DEPOT_ID=@d AND LOCATION_NO=N'-'",
            connection, transaction);
        command.Parameters.AddWithValue("@d", Depot);
        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        return (reader.GetString(0).Trim(), reader.GetString(1).Trim(), reader.GetString(2).Trim());
    }

    private static async Task SeedAsync(SqlConnection connection, SqlTransaction transaction, bool withSentinel)
    {
        await ExecuteAsync(connection, transaction, """
            DELETE FROM dbo.INV_PRO_DEPOT WHERE DEPOT_ID=@d;
            DELETE FROM dbo.DEPOT_LOCATION WHERE DEPOT_ID=@d;
            DELETE FROM dbo.DEPOT WHERE DEPOT_ID=@d;
            INSERT INTO dbo.DEPOT (DEPOT_ID, DEPOT_NAME) VALUES (@d, N'ADR26 库位测试仓');
            """, ("@d", Depot));

        if (withSentinel)
            await ExecuteAsync(connection, transaction,
                "INSERT INTO dbo.DEPOT_LOCATION (DEPOT_ID, LOCATION_NO, PARENT_NO, LOCATION_PATH, LOCATION_TYPE, LOCATION_NAME, SEQ_NO, STATUS) "
                + "VALUES (@d, N'-', NULL, N'/-', N'BIN', N'未指定位置（待归位）', 0, N'A')", ("@d", Depot));

        // 三层树：A（区）→ A-R1（架）→ A-R1-B1（位），另有独立根 B
        await ExecuteAsync(connection, transaction, """
            INSERT INTO dbo.DEPOT_LOCATION (DEPOT_ID, LOCATION_NO, PARENT_NO, LOCATION_PATH, LOCATION_TYPE, SEQ_NO, STATUS)
                VALUES (@d, N'A',      NULL,     N'/A',            N'ZONE', 1, N'A'),
                       (@d, N'B',      NULL,     N'/B',            N'ZONE', 2, N'A'),
                       (@d, N'A-R1',   N'A',     N'/A/A-R1',       N'RACK', 1, N'A'),
                       (@d, N'A-R1-B1',N'A-R1',  N'/A/A-R1/A-R1-B1', N'BIN', 1, N'A');
            """, ("@d", Depot));
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
