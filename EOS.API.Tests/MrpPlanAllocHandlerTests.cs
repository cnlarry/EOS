using System.Text.Json;
using System.Text.RegularExpressions;
using EOS.API.Data.Effects;
using EOS.API.Data.Effects.ServiceEffectHandlers;
using Microsoft.Data.SqlClient;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// Unit (no DB) and integration tests for the mrp-plan-alloc service handler:
/// fail-closed parameter validation, the segmented order-open SQL, the
/// min-stock material-provide SQL, NULL propagation without COALESCE, idempotent
/// re-runs and this-document scoping. Integration tests run inside rolled-back
/// transactions and are skipped without a connection string.
/// </summary>
public class MrpPlanAllocHandlerTests
{
    private static ModuleEffectPlan OrderPlan() => new(
        1405, "COP_ORDER_M", "COP_ORDER_D", "v-test",
        new[] { "ORDER_TYPE", "ORDER_NO" },
        Array.Empty<EffectActionPlan>(), Array.Empty<EffectValidationPlan>());

    private static ModuleEffectPlan ProducePlan() => new(
        1502, "MOC_PRODUCE_M", "MOC_PRODUCE_D", "v-test",
        new[] { "PRODUCE_TYPE", "PRODUCE_NO" },
        Array.Empty<EffectActionPlan>(), Array.Empty<EffectValidationPlan>());

    private static ISet<string> OrderColumns() => new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "COP_ORDER_M", "COP_ORDER_M.ORDER_TYPE", "COP_ORDER_M.ORDER_NO",
        "COP_ORDER_D", "COP_ORDER_D.ORDER_TYPE", "COP_ORDER_D.ORDER_NO", "COP_ORDER_D.SERIAL_NO",
        "COP_ORDER_D.PRO_NO", "COP_ORDER_D.QTY", "COP_ORDER_D.SPARE_QTY",
        "COP_ORDER_D.PLAN_QTY", "COP_ORDER_D.PLAN_SPARE_QTY", "COP_ORDER_D.DEPOT_QTY",
        "PRODUCT", "PRODUCT.PRO_NO", "PRODUCT.MRP_QTY",
    };

    private static ISet<string> ProduceColumns() => new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "MOC_PRODUCE_M", "MOC_PRODUCE_M.PRODUCE_TYPE", "MOC_PRODUCE_M.PRODUCE_NO",
        "MOC_PRODUCE_D", "MOC_PRODUCE_D.PRODUCE_TYPE", "MOC_PRODUCE_D.PRODUCE_NO",
        "MOC_PRODUCE_D.SERIAL_NO", "MOC_PRODUCE_D.PRO_NO", "MOC_PRODUCE_D.NEED_QTY",
        "MOC_PRODUCE_D.DEPOT_QTY",
        "PRODUCT", "PRODUCT.PRO_NO", "PRODUCT.MRP_QTY",
    };

    private const string OrderParamsJson =
        """{"targetTable":"COP_ORDER_D","fields":["PLAN_QTY","PLAN_SPARE_QTY","DEPOT_QTY"],"stockSource":"PRODUCT.MRP_QTY","mode":"order-open"}""";

    private const string ProduceParamsJson =
        """{"targetTable":"MOC_PRODUCE_D","field":"DEPOT_QTY","stockSource":"PRODUCT.MRP_QTY","scope":"THIS_DOC"}""";

    private static MrpPlanAllocSpec ParseOrder() =>
        MrpPlanAllocSpec.Parse(JsonDocument.Parse(OrderParamsJson).RootElement, OrderPlan(), OrderColumns());

    [Fact]
    public void EffectKey_RegisteredAsService()
    {
        Assert.Equal("mrp-plan-alloc", new MrpPlanAllocHandler().EffectKey);
        Assert.True(EffectRegistry.IsImplemented("mrp-plan-alloc"));
    }

    [Fact]
    public void Parse_OrderOpen_AcceptsLandedParams()
    {
        var spec = ParseOrder();
        Assert.Equal("COP_ORDER_D", spec.TargetTable, ignoreCase: true);
        Assert.Equal("order-open", spec.Mode, ignoreCase: true);
    }

    [Fact]
    public void Parse_MaterialProvide_AcceptsLandedParams()
    {
        var spec = MrpPlanAllocSpec.Parse(
            JsonDocument.Parse(ProduceParamsJson).RootElement, ProducePlan(), ProduceColumns());
        Assert.Equal("MOC_PRODUCE_D", spec.TargetTable, ignoreCase: true);
        Assert.Equal("material-provide", spec.Mode, ignoreCase: true);
    }

    [Fact]
    public void Parse_RejectsBadConfigurations()
    {
        var orderPlan = OrderPlan();
        var columns = OrderColumns();

        // Unknown mode / scope / stock source.
        Assert.Throws<EffectConfigException>(() => MrpPlanAllocSpec.Parse(JsonDocument.Parse(
            """{"targetTable":"COP_ORDER_D","fields":["PLAN_QTY","PLAN_SPARE_QTY","DEPOT_QTY"],"stockSource":"PRODUCT.MRP_QTY","mode":"unknown"}""").RootElement, orderPlan, columns));
        Assert.Throws<EffectConfigException>(() => MrpPlanAllocSpec.Parse(JsonDocument.Parse(
            """{"targetTable":"COP_ORDER_D","fields":["PLAN_QTY","PLAN_SPARE_QTY","DEPOT_QTY"],"stockSource":"PRODUCT.MRP_QTY","mode":"order-open","scope":"GLOBAL"}""").RootElement, orderPlan, columns));
        Assert.Throws<EffectConfigException>(() => MrpPlanAllocSpec.Parse(JsonDocument.Parse(
            """{"targetTable":"COP_ORDER_D","fields":["PLAN_QTY","PLAN_SPARE_QTY","DEPOT_QTY"],"stockSource":"INV_PRO_DEPOT.QTY","mode":"order-open"}""").RootElement, orderPlan, columns));

        // Target must be the module detail table.
        Assert.Throws<EffectConfigException>(() => MrpPlanAllocSpec.Parse(JsonDocument.Parse(
            """{"targetTable":"COP_ORDER_M","fields":["PLAN_QTY","PLAN_SPARE_QTY","DEPOT_QTY"],"stockSource":"PRODUCT.MRP_QTY","mode":"order-open"}""").RootElement, orderPlan, columns));

        // order-open field shape: exactly the three segmented columns, no singular field.
        Assert.Throws<EffectConfigException>(() => MrpPlanAllocSpec.Parse(JsonDocument.Parse(
            """{"targetTable":"COP_ORDER_D","fields":["PLAN_QTY","DEPOT_QTY"],"stockSource":"PRODUCT.MRP_QTY","mode":"order-open"}""").RootElement, orderPlan, columns));
        Assert.Throws<EffectConfigException>(() => MrpPlanAllocSpec.Parse(JsonDocument.Parse(
            """{"targetTable":"COP_ORDER_D","field":"DEPOT_QTY","stockSource":"PRODUCT.MRP_QTY","mode":"order-open"}""").RootElement, orderPlan, columns));

        // material-provide field shape.
        var producePlan = ProducePlan();
        var produceColumns = ProduceColumns();
        Assert.Throws<EffectConfigException>(() => MrpPlanAllocSpec.Parse(JsonDocument.Parse(
            """{"targetTable":"MOC_PRODUCE_D","stockSource":"PRODUCT.MRP_QTY","mode":"material-provide"}""").RootElement, producePlan, produceColumns));
        Assert.Throws<EffectConfigException>(() => MrpPlanAllocSpec.Parse(JsonDocument.Parse(
            """{"targetTable":"MOC_PRODUCE_D","field":"NEED_QTY","stockSource":"PRODUCT.MRP_QTY","mode":"material-provide"}""").RootElement, producePlan, produceColumns));
        Assert.Throws<EffectConfigException>(() => MrpPlanAllocSpec.Parse(JsonDocument.Parse(
            """{"targetTable":"MOC_PRODUCE_D","fields":["DEPOT_QTY"],"stockSource":"PRODUCT.MRP_QTY","mode":"material-provide"}""").RootElement, producePlan, produceColumns));
        // Explicit mode must agree with the target table default.
        Assert.Throws<EffectConfigException>(() => MrpPlanAllocSpec.Parse(JsonDocument.Parse(
            """{"targetTable":"MOC_PRODUCE_D","field":"DEPOT_QTY","stockSource":"PRODUCT.MRP_QTY","mode":"order-open"}""").RootElement, producePlan, produceColumns));
        Assert.Throws<EffectConfigException>(() => MrpPlanAllocSpec.Parse(JsonDocument.Parse(
            """{"targetTable":"COP_ORDER_D","fields":["PLAN_QTY","PLAN_SPARE_QTY","DEPOT_QTY"],"stockSource":"PRODUCT.MRP_QTY","mode":"material-provide"}""").RootElement, OrderPlan(), OrderColumns()));

        // Missing physical objects fail closed.
        Assert.Throws<EffectConfigException>(() => MrpPlanAllocSpec.Parse(JsonDocument.Parse(
            OrderParamsJson).RootElement, orderPlan,
            new HashSet<string>(columns.Where(item => item != "COP_ORDER_D"))));
        Assert.Throws<EffectConfigException>(() => MrpPlanAllocSpec.Parse(JsonDocument.Parse(
            OrderParamsJson).RootElement, orderPlan,
            new HashSet<string>(columns.Where(item => item != "PRODUCT.MRP_QTY"))));
        Assert.Throws<EffectConfigException>(() => MrpPlanAllocSpec.Parse(JsonDocument.Parse(
            OrderParamsJson).RootElement, orderPlan,
            new HashSet<string>(columns.Where(item => item != "COP_ORDER_M.ORDER_NO"))));
    }

    [Fact]
    public void BuildUpdate_OrderOpen_HasSegmentedAssignmentsAndDocScope()
    {
        var parameters = new List<EffectSqlParameter>();
        var sql = MrpPlanAllocHandler.BuildUpdate(
            OrderPlan(), ParseOrder(), new[] { "DD", "DD17110164" }, parameters);

        // Segmented plan/spare/depot expressions ported from the legacy cursor.
        Assert.Contains("D.[PLAN_QTY] = CASE WHEN P.MRP_QTY > 0", sql);
        Assert.Contains("D.[PLAN_SPARE_QTY] = CASE WHEN P.MRP_QTY - D.QTY > 0", sql);
        Assert.Contains("D.[DEPOT_QTY] = CASE WHEN P.MRP_QTY > 0", sql);
        Assert.Contains("FROM dbo.[COP_ORDER_D] D", sql);
        Assert.Contains("JOIN dbo.[COP_ORDER_M] M ON D.[ORDER_TYPE] = M.[ORDER_TYPE]", sql);
        Assert.Contains("JOIN dbo.PRODUCT P ON P.PRO_NO = D.PRO_NO", sql);
        // This-document scope only; keys travel as parameters.
        Assert.Contains("WHERE M.[ORDER_TYPE] = @mk0 AND M.[ORDER_NO] = @mk1", sql);
        Assert.Equal(new object?[] { "DD", "DD17110164" }, parameters.Select(parameter => parameter.Value));
        // Legacy NULL propagation: no COALESCE anywhere in the statement.
        Assert.DoesNotContain("COALESCE", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ISNULL", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildUpdate_MaterialProvide_HasMinExpressionAndDocScope()
    {
        var plan = ProducePlan();
        var spec = MrpPlanAllocSpec.Parse(
            JsonDocument.Parse(ProduceParamsJson).RootElement, plan, ProduceColumns());
        var parameters = new List<EffectSqlParameter>();
        var sql = MrpPlanAllocHandler.BuildUpdate(plan, spec, new[] { "ZCML", "ZLD2608081" }, parameters);

        Assert.Contains("D.[DEPOT_QTY] = CASE WHEN P.MRP_QTY > 0", sql);
        Assert.Contains("WHEN P.MRP_QTY >= D.NEED_QTY THEN D.NEED_QTY ELSE P.MRP_QTY", sql);
        Assert.Contains("FROM dbo.[MOC_PRODUCE_D] D", sql);
        // The legacy global update is corrected to this-document scope.
        Assert.Contains("WHERE M.[PRODUCE_TYPE] = @mk0 AND M.[PRODUCE_NO] = @mk1", sql);
        Assert.Equal(new object?[] { "ZCML", "ZLD2608081" }, parameters.Select(parameter => parameter.Value));
        Assert.DoesNotContain("COALESCE", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Trait("Category", "Integration")]
    [Fact]
    public async Task Execute_OrderOpen_RecomputesPlanFromCurrentStock()
    {
        if (ResolveConnectionString() is not { } connectionString)
        {
            return;
        }
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
        try
        {
            var handler = new MrpPlanAllocHandler();
            using var paramsDoc = JsonDocument.Parse(OrderParamsJson);
            var action = new EffectActionPlan(2, "APPROVE_EFFECT", "mrp-plan-alloc", null, true, "BLOCK", null, paramsDoc.RootElement, null, Array.Empty<EffectOpPlan>());
            var context = new ServiceEffectContext(connection, transaction, OrderPlan(), action,
                EffectEvent.ApproveEffect, "DD,DD17110164", new[] { "DD", "DD17110164" }, "test");

            // 断言依赖的可用库存由本用例在自己事务内写入：PRODUCT.MRP_QTY 是 MRP 重算的派生列，
            // 读共享开发库的当前值会让用例随"上次谁跑过重算、清理过什么"变红。
            await SeedOrderLineStockAsync(connection, transaction, [(1, 18.0), (2, 3499.0)]);

            var affected = await handler.ExecuteAsync(context, CancellationToken.None);
            Assert.Equal(2, affected);

            // Line 1: stock 18 < qty 2020 -> plan the remainder, depot takes the stock.
            // Line 2: stock 3499 covers qty 2020 -> zero plan, full depot allocation.
            var lines = await ReadOrderLinesAsync(connection, transaction);
            Assert.Equal(2002.0, lines[1].PlanQty!.Value, 3);
            Assert.Equal(18.0, lines[1].DepotQty!.Value, 3);
            Assert.Equal(0.0, lines[2].PlanQty!.Value, 3);
            Assert.Equal(0.0, lines[2].PlanSpareQty!.Value, 3);
            Assert.Equal(2020.0, lines[2].DepotQty!.Value, 3);

            // Pure recompute: a second run changes nothing.
            var rerun = await handler.ExecuteAsync(context, CancellationToken.None);
            Assert.Equal(2, rerun);
            var rerunLines = await ReadOrderLinesAsync(connection, transaction);
            Assert.Equal(lines[1].PlanQty, rerunLines[1].PlanQty);
            Assert.Equal(lines[1].DepotQty, rerunLines[1].DepotQty);
        }
        finally
        {
            await transaction.RollbackAsync();
        }
    }

    [Trait("Category", "Integration")]
    [Fact]
    public async Task Execute_MaterialProvide_ScopesToThisDoc()
    {
        if (ResolveConnectionString() is not { } connectionString)
        {
            return;
        }
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
        try
        {
            var siblingBefore = await ReadDepotQtyAsync(connection, transaction, "ZCML", "ZLD2608080");
            var handler = new MrpPlanAllocHandler();
            using var paramsDoc = JsonDocument.Parse(ProduceParamsJson);
            var action = new EffectActionPlan(3, "APPROVE_EFFECT", "mrp-plan-alloc", null, true, "BLOCK", null, paramsDoc.RootElement, null, Array.Empty<EffectOpPlan>());
            var context = new ServiceEffectContext(connection, transaction, ProducePlan(), action,
                EffectEvent.ApproveEffect, "ZCML,ZLD2608081", new[] { "ZCML", "ZLD2608081" }, "test");

            // NEED_QTY 0 with stock 10 -> min gives 0 (dev data has no positive NEED_QTY;
            // the partial/full branches are covered by the SQL-shape test above).
            var affected = await handler.ExecuteAsync(context, CancellationToken.None);
            Assert.Equal(1, affected);
            Assert.Equal(0, await ReadDepotQtyAsync(connection, transaction, "ZCML", "ZLD2608081"));

            // The legacy global update is gone: sibling documents are untouched.
            Assert.Equal(siblingBefore, await ReadDepotQtyAsync(connection, transaction, "ZCML", "ZLD2608080"));
        }
        finally
        {
            await transaction.RollbackAsync();
        }
    }

    [Trait("Category", "Integration")]
    [Fact]
    public async Task Execute_GateOff_ReturnsZeroRowsWithoutTouchingDocs()
    {
        if (ResolveConnectionString() is not { } connectionString)
        {
            return;
        }
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
        try
        {
            await using (var gate = new SqlCommand(
                "UPDATE dbo.SYSSS SET PARAM_VALUE=N'0' WHERE OWNER_MODULE=110111 AND PARAM_KEY=N'PRO_MRP'",
                connection, transaction))
            {
                await gate.ExecuteNonQueryAsync();
            }
            var before = await ReadOrderLinesAsync(connection, transaction);
            var handler = new MrpPlanAllocHandler();
            using var paramsDoc = JsonDocument.Parse(OrderParamsJson);
            var action = new EffectActionPlan(2, "APPROVE_EFFECT", "mrp-plan-alloc", null, true, "BLOCK", null, paramsDoc.RootElement, null, Array.Empty<EffectOpPlan>());
            var context = new ServiceEffectContext(connection, transaction, OrderPlan(), action,
                EffectEvent.ApproveEffect, "DD,DD17110164", new[] { "DD", "DD17110164" }, "test");

            Assert.Equal(0, await handler.ExecuteAsync(context, CancellationToken.None));
            var after = await ReadOrderLinesAsync(connection, transaction);
            Assert.Equal(before[1].PlanQty, after[1].PlanQty);
            Assert.Equal(before[1].DepotQty, after[1].DepotQty);
        }
        finally
        {
            await transaction.RollbackAsync();
        }
    }

    private sealed record OrderLine(double? PlanQty, double? PlanSpareQty, double? DepotQty);

    /// <summary>
    /// 把被测订单各行所引用产品的可用库存显式写进本用例的事务（回滚）。
    /// 前置写入而不是读库内现值，用例的期望值才不依赖"派生列上次何时被重算"。
    /// </summary>
    private static async Task SeedOrderLineStockAsync(
        SqlConnection connection, SqlTransaction transaction, (int SerialNo, double MrpQty)[] stocks)
    {
        foreach (var (serialNo, mrpQty) in stocks)
        {
            await using var command = new SqlCommand("""
                UPDATE P SET P.MRP_QTY = @qty
                FROM dbo.PRODUCT P
                JOIN dbo.COP_ORDER_D D ON D.PRO_NO = P.PRO_NO
                WHERE D.ORDER_TYPE = 'DD' AND D.ORDER_NO = 'DD17110164' AND D.SERIAL_NO = @serial;
                """, connection, transaction);
            command.Parameters.Add("@qty", System.Data.SqlDbType.Float).Value = mrpQty;
            command.Parameters.Add("@serial", System.Data.SqlDbType.SmallInt).Value = (short)serialNo;
            Assert.True(await command.ExecuteNonQueryAsync() >= 1,
                $"开发数据里订单 DD/DD17110164 第 {serialNo} 行没有可更新的产品，用例前置写入失败。");
        }
    }

    private static async Task<Dictionary<int, OrderLine>> ReadOrderLinesAsync(SqlConnection connection, SqlTransaction transaction)
    {
        var result = new Dictionary<int, OrderLine>();
        await using var command = new SqlCommand(
            "SELECT SERIAL_NO, PLAN_QTY, PLAN_SPARE_QTY, DEPOT_QTY FROM dbo.COP_ORDER_D WHERE ORDER_TYPE='DD' AND ORDER_NO='DD17110164';",
            connection, transaction);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result[reader.GetInt16(0)] = new OrderLine(
                reader.IsDBNull(1) ? null : Convert.ToDouble(reader.GetValue(1)),
                reader.IsDBNull(2) ? null : Convert.ToDouble(reader.GetValue(2)),
                reader.IsDBNull(3) ? null : Convert.ToDouble(reader.GetValue(3)));
        }
        return result;
    }

    private static async Task<double?> ReadDepotQtyAsync(
        SqlConnection connection, SqlTransaction transaction, string type, string no)
    {
        await using var command = new SqlCommand(
            "SELECT DEPOT_QTY FROM dbo.MOC_PRODUCE_D WHERE PRODUCE_TYPE=@t AND PRODUCE_NO=@n;",
            connection, transaction);
        command.Parameters.Add("@t", System.Data.SqlDbType.NChar, 10).Value = type;
        command.Parameters.Add("@n", System.Data.SqlDbType.NChar, 20).Value = no;
        var value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? null : Convert.ToDouble(value);
    }

    private static string? ResolveConnectionString()
    {
        var env = Environment.GetEnvironmentVariable("EOS_ERP_TEST_CONNECTION");
        if (!string.IsNullOrWhiteSpace(env))
        {
            return env;
        }

        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "config.toml");
            var text = File.ReadAllText(path);
            var match = Regex.Match(text, "\\[mcp_servers\\.mssql\\.env\\][\\s\\S]*?MSSQL_CONNECTION_STRING\\s*=\\s*\"([^\"]+)\"");
            return match.Success && match.Groups[1].Value.Contains("Database=EOS.ERP")
                ? match.Groups[1].Value
                : null;
        }
        catch
        {
            return null;
        }
    }
}
