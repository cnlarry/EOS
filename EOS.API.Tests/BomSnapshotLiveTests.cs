using System.Data;
using System.Text.Json;
using EOS.API.Data.Effects;
using EOS.API.Data.Effects.ServiceEffectHandlers;
using Microsoft.Data.SqlClient;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 产品 BOM → 工单 BOM 快照（ADR-039 D7）的真库验证。
///
/// 造一棵三层树，断言四件事：
///   ① 展开成"每个父件一行、其直接子件在明细"的形态（两层父件、两条边）；
///   ② 版次被显式记录（工单 BOM 的 EDITION = 抄的那一版）；
///   ③ **只快照一次**——第二次执行返回 0，已有行不被覆盖（人工调整必须能留下）；
///   ④ 产品没有 BOM 时返回 0 而不抛异常（先下制令、后补 BOM 是正常流程）。
///
/// 处理器用调用方事务，所以造数与断言都在同一事务里，结束回滚。
/// </summary>
[Trait("Category", "Integration")]
[Collection("live-database")]
public sealed class BomSnapshotLiveTests
{
    private static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("MSSQL_ERP_CONN")
        ?? throw new InvalidOperationException(
            "真库集成测试需要 MSSQL_ERP_CONN；未配置即失败（无连接跳过≠已验证）。");

    private const string Root = "ADR39SNPROOT";
    private const string Sub = "ADR39SNPSUB";
    private const string Leaf = "ADR39SNPLEAF";
    private const string Bare = "ADR39SNPBARE";   // 没有 BOM 的产品
    private const string Type = "ADR39SNP";
    private const string No = "ADR39SNP001";
    private const string Edition = "01";

    private static readonly string ParamJson = """
        {
          "orderProductField": "PRO_NO",
          "orderEditionField": "EDITION",
          "targetMasterTable": "MOC_BOM_STRU_M",
          "targetDetailTable": "MOC_BOM_STRU_D",
          "sourceMasterTable": "BOM_STRU_M",
          "sourceDetailTable": "BOM_STRU_D",
          "maxDepth": 20
        }
        """;

    [Fact]
    public async Task 制令单首次保存_把产品BOM整棵快照到工单BOM()
    {
        var token = CancellationToken.None;
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);

        await SeedSourceAsync(connection, transaction, token);
        await SeedOrderAsync(connection, transaction, Root, token);

        var affected = await RunAsync(connection, transaction, token);

        // 主表两行：ROOT 与 SUB（各是一个父件）；明细两条边。
        Assert.Equal(4, affected);
        var master = await ReadAsync(connection, transaction,
            "SELECT PRO_NO, EDITION FROM dbo.MOC_BOM_STRU_M WHERE PRODUCE_TYPE=@type AND PRODUCE_NO=@no ORDER BY PRO_NO;",
            token);
        Assert.Equal(2, master.Count);
        Assert.Equal($"{Root}|{Edition}", master[0]);
        Assert.Equal($"{Sub}|{Edition}", master[1]);

        var detail = await ReadAsync(connection, transaction,
            "SELECT PRO_NO, ELEMENT_PRO_NO FROM dbo.MOC_BOM_STRU_D WHERE PRODUCE_TYPE=@type AND PRODUCE_NO=@no ORDER BY PRO_NO, SERIAL_NO;",
            token);
        Assert.Equal(2, detail.Count);
        Assert.Equal($"{Root}|{Sub}", detail[0]);
        Assert.Equal($"{Sub}|{Leaf}", detail[1]);

        await transaction.RollbackAsync(token);
    }

    [Fact]
    public async Task 已有工单BOM时_不覆盖人工调整()
    {
        var token = CancellationToken.None;
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);

        await SeedSourceAsync(connection, transaction, token);
        await SeedOrderAsync(connection, transaction, Root, token);
        await RunAsync(connection, transaction, token);

        // 模拟计划员手工把用量改成 9，然后再保存一次制令单
        await using (var edit = new SqlCommand(
            "UPDATE dbo.MOC_BOM_STRU_D SET ELEMENT_QTY=9 WHERE PRODUCE_TYPE=@type AND PRODUCE_NO=@no;",
            connection, transaction))
        {
            edit.Parameters.Add("@type", SqlDbType.NVarChar, 20).Value = Type;
            edit.Parameters.Add("@no", SqlDbType.NVarChar, 40).Value = No;
            await edit.ExecuteNonQueryAsync(token);
        }

        var second = await RunAsync(connection, transaction, token);
        Assert.Equal(0, second);

        var qty = await ReadAsync(connection, transaction,
            "SELECT CONVERT(nvarchar(20), ELEMENT_QTY) FROM dbo.MOC_BOM_STRU_D WHERE PRODUCE_TYPE=@type AND PRODUCE_NO=@no;",
            token);
        Assert.All(qty, value => Assert.Equal("9.000000", value));

        await transaction.RollbackAsync(token);
    }

    [Fact]
    public async Task 产品没有BOM时_返回零而不阻断保存()
    {
        var token = CancellationToken.None;
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);

        // 只造产品档案，不造任何 BOM
        await InsertProductAsync(connection, transaction, Bare, "裸料", token);
        await SeedOrderAsync(connection, transaction, Bare, token);

        var affected = await RunAsync(connection, transaction, token);
        Assert.Equal(0, affected);

        var master = await ReadAsync(connection, transaction,
            "SELECT PRO_NO FROM dbo.MOC_BOM_STRU_M WHERE PRODUCE_TYPE=@type AND PRODUCE_NO=@no;", token);
        Assert.Empty(master);

        await transaction.RollbackAsync(token);
    }

    private static async Task<int> RunAsync(SqlConnection connection, SqlTransaction transaction, CancellationToken token)
    {
        using var document = JsonDocument.Parse(ParamJson);
        var action = new EffectActionPlan(
            Seq: 2,
            EventCode: "SAVE",
            EffectKey: "bom-snapshot-to-order",
            EffectName: "产品BOM快照到工单BOM",
            Enabled: true,
            FailMode: "BLOCK",
            Condition: null,
            Params: document.RootElement,
            Reverse: null,
            Ops: []);
        var plan = new ModuleEffectPlan(
            ModuleId: 1502,
            MasterTable: "MOC_PRODUCE_M",
            DetailTable: "MOC_PRODUCE_D",
            DefinitionVersion: "live-test",
            MasterPkOrder: ["PRODUCE_TYPE", "PRODUCE_NO"],
            Actions: [action],
            Rules: []);
        var context = new ServiceEffectContext(
            connection, transaction, plan, action, EffectEvent.Save,
            $"{Type}/{No}", [Type, No], "live-test");
        return await new BomSnapshotToOrderHandler().ExecuteAsync(context, token);
    }

    private static async Task SeedSourceAsync(SqlConnection connection, SqlTransaction transaction, CancellationToken token)
    {
        foreach (var (no, name) in new[] { (Root, "三层根"), (Sub, "半成品"), (Leaf, "原料") })
        {
            await InsertProductAsync(connection, transaction, no, name, token);
        }
        foreach (var no in new[] { Root, Sub })
        {
            await using var master = new SqlCommand(
                "INSERT INTO dbo.BOM_STRU_M (PRO_NO, EDITION, BATCH_QTY) VALUES (@no, @edition, 1);",
                connection, transaction);
            master.Parameters.Add("@no", SqlDbType.NVarChar, 60).Value = no;
            master.Parameters.Add("@edition", SqlDbType.NVarChar, 20).Value = Edition;
            await master.ExecuteNonQueryAsync(token);
        }
        // SUB 自己有 01 版 BOM，所以 Root 这行要指定子件版本 01；LEAF 是材料，留空
        await InsertBomDetailAsync(connection, transaction, Root, 1, Sub, 2m, 10m, token, childEdition: Edition);
        await InsertBomDetailAsync(connection, transaction, Sub, 1, Leaf, 3m, 20m, token);
    }

    private static async Task InsertProductAsync(
        SqlConnection connection, SqlTransaction transaction, string no, string name, CancellationToken token)
    {
        await using var command = new SqlCommand(
            "INSERT INTO dbo.PRODUCT (PRO_NO, PRO_NAME, UNIT_ID) VALUES (@no, @name, N'PCS');",
            connection, transaction);
        command.Parameters.Add("@no", SqlDbType.NVarChar, 60).Value = no;
        command.Parameters.Add("@name", SqlDbType.NVarChar, 100).Value = name;
        await command.ExecuteNonQueryAsync(token);
    }

    /// <summary>
    /// 插一行明细。<paramref name="childEdition"/> 是**子件自己的** BOM 版次：
    /// 半成品必须给（否则快照在这行停止展开），材料必须留空。
    /// </summary>
    private static async Task InsertBomDetailAsync(
        SqlConnection connection, SqlTransaction transaction, string parent, short serial, string child,
        decimal qty, decimal lostRate, CancellationToken token, string? childEdition = null)
    {
        await using var command = new SqlCommand(
            """
            INSERT INTO dbo.BOM_STRU_D
                (PRO_NO, EDITION, SERIAL_NO, ELEMENT_PRO_NO, ELEMENT_EDITION, ELEMENT_QTY, BASE_QTY, LOST_RATE)
            VALUES (@parent, @edition, @serial, @child, @childEdition, @qty, 1, @lost);
            """, connection, transaction);
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

    private static async Task SeedOrderAsync(
        SqlConnection connection, SqlTransaction transaction, string productNo, CancellationToken token)
    {
        await using var command = new SqlCommand(
            "INSERT INTO dbo.MOC_PRODUCE_M (PRODUCE_TYPE, PRODUCE_NO, PRO_NO, EDITION) VALUES (@type, @no, @product, @edition);",
            connection, transaction);
        command.Parameters.Add("@type", SqlDbType.NVarChar, 20).Value = Type;
        command.Parameters.Add("@no", SqlDbType.NVarChar, 40).Value = No;
        command.Parameters.Add("@product", SqlDbType.NVarChar, 60).Value = productNo;
        command.Parameters.Add("@edition", SqlDbType.NVarChar, 20).Value = Edition;
        await command.ExecuteNonQueryAsync(token);
    }

    private static async Task<List<string>> ReadAsync(
        SqlConnection connection, SqlTransaction transaction, string sql, CancellationToken token)
    {
        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@type", SqlDbType.NVarChar, 20).Value = Type;
        command.Parameters.Add("@no", SqlDbType.NVarChar, 40).Value = No;
        var rows = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            var cells = new string[reader.FieldCount];
            for (var index = 0; index < reader.FieldCount; index++)
            {
                cells[index] = reader.IsDBNull(index) ? string.Empty : reader.GetValue(index).ToString()!.Trim();
            }
            rows.Add(string.Join('|', cells));
        }
        return rows;
    }
}
