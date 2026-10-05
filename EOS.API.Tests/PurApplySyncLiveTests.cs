using System.Data;
using System.Text.Json;
using EOS.API.Data.Effects;
using EOS.API.Data.Effects.ServiceEffectHandlers;
using Microsoft.Data.SqlClient;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 请购单族（1615/1616，原 `pur-apply` C#）入效果目录后的真库验证：
/// SAVE 期 `pur-apply-sync` 承接旧过程 `P_PUR_APPLY_After_Save` 的整链
/// （补明细行 → 应购/损耗回填 → 订单字段回填 → 专购表数量分配 → 主表单号串联）。
/// 用例把**旧过程语句链内联为基准**（含分配游标与单号串联游标，`sp_executesql` 嵌套作用域避免 #临时表残留），
/// 在同一初始态下比较三张表的最终状态；并覆盖两个易错语义：
///   ① 待购表产品**没有产品档案时仍要补明细行**（仓库/单位为空，等价既有实现过程的 left join）；
///   ② 待购表 QTY 分配是"按产品把明细数量逐行扣减"，同一产品多行时只有前面的行拿满。
/// 另跑一次"无待购行"场景：仅应购数量清零，其余不动。
/// 造数用 `ADR12PA` 前缀，事务结束回滚。
/// </summary>
[Trait("Category", "Integration")]
[Collection("live-database")]
public sealed class PurApplySyncLiveTests
{
    private static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("MSSQL_ERP_CONN")
        ?? throw new InvalidOperationException(
            "真库集成测试需要 MSSQL_ERP_CONN；未配置即失败（无连接跳过≠已验证）。");

    private const int ModuleId = 1616;
    private const string Type = "ADR12PA";
    private const string No = "ADR12PA001";
    private const string OrderType = "ADR12PAOT";
    private const string P1 = "ADR12PAP1";
    private const string P2 = "ADR12PAP2";
    private const string P3 = "ADR12PAP3";   // 无产品档案

    [Fact]
    public async Task 请购单_待购表汇总与申购数量分配与旧过程一致()
    {
        var token = CancellationToken.None;
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
        try
        {
            await SeedAsync(connection, transaction, token, withMore: true, extraNullMore: false);
            var action = await LoadActionAsync(connection, transaction, token);
            Assert.Equal("pur-apply-sync", action.EffectKey);
            var plan = new ModuleEffectPlan(ModuleId, "PUR_APPLY_M", "PUR_APPLY_D", "live-pur-apply",
                ["APPLY_TYPE", "APPLY_NO"], [action], []);

            // ① 目录效果
            await new PurApplySyncHandler().ExecuteAsync(
                new ServiceEffectContext(connection, transaction, plan, action, EffectEvent.Save,
                    No, [Type, No], "live-test"), token);
            var byEffect = await ReadAsync(connection, transaction, token);

            // 补行：P3 无产品档案仍补行（序号 3、仓库/单位为空、数量取待购合计 2）
            var appended = byEffect.Details.Single(row => row.StartsWith("3|", StringComparison.Ordinal));
            Assert.Equal(["3", P3, "2", "0", "2", "", "", "", "", "", "", "", "", ""], appended.Split('|'));
            // 应购/损耗按产品合计回填；订单字段由待购行关联的订单行回填（原有行的仓库/单位不动）
            Assert.Contains("1|ADR12PAP1|4|1|5|X|X|ADR12PAOT|PO-1|1|CP1|CO1|2026-03-01|ADR12PAC1", byEffect.Details);
            Assert.Contains("2|ADR12PAP2|4|2|2|X|X|ADR12PAOT|PO-2|1|CP2|CO2|2026-03-02|ADR12PAC2", byEffect.Details);
            // 分配：P1 明细数量 5 先喂满待购行 serial1（需 3）再喂 serial2（需 1）；P2/P3 各自拿剩余
            Assert.Equal([3d, 1d, 2d, 2d], byEffect.MoreQty);
            Assert.Equal(("PO-1,PO-2", "PR1,PR2"), byEffect.Master);

            // ② 回到同一初始态跑旧过程语句链
            await SeedAsync(connection, transaction, token, withMore: true, extraNullMore: false);
            await RunBaselineAsync(connection, transaction, token);
            var byBaseline = await ReadAsync(connection, transaction, token);

            Assert.Equal(byBaseline.Details, byEffect.Details);
            Assert.Equal(byBaseline.MoreQty, byEffect.MoreQty);
            Assert.Equal(byBaseline.Master, byEffect.Master);

            // ③ 无待购行：只清零应购数量，订单字段与单号串联不动
            await SeedAsync(connection, transaction, token, withMore: false, extraNullMore: false);
            await new PurApplySyncHandler().ExecuteAsync(
                new ServiceEffectContext(connection, transaction, plan, action, EffectEvent.Save,
                    No, [Type, No], "live-test"), token);
            var noMore = await ReadAsync(connection, transaction, token);
            Assert.All(noMore.Details, row => Assert.Equal("0", row.Split('|')[2]));
            Assert.Equal(("OLD-ORDER", "OLD-PRODUCE"), noMore.Master);

            await SeedAsync(connection, transaction, token, withMore: false, extraNullMore: false);
            await RunBaselineAsync(connection, transaction, token);
            var noMoreBaseline = await ReadAsync(connection, transaction, token);
            Assert.Equal(noMoreBaseline.Details, noMore.Details);
            Assert.Equal(noMoreBaseline.Master, noMore.Master);

            // ④ 有意差异（登记在覆盖率报告）：待购行里存在"没有关联订单"的行时，
            //    旧过程把整个串联结果算成 NULL ⇒ 主表单号**完全不回写**（保持旧值）；
            //    移植实现按"去重非空值"串联（其余行仍回写），空值行不参与。
            await SeedAsync(connection, transaction, token, withMore: true, extraNullMore: true);
            await new PurApplySyncHandler().ExecuteAsync(
                new ServiceEffectContext(connection, transaction, plan, action, EffectEvent.Save,
                    No, [Type, No], "live-test"), token);
            var withNullByEffect = await ReadAsync(connection, transaction, token);
            Assert.Equal(("PO-1,PO-2", "PR1,PR2"), withNullByEffect.Master);

            await SeedAsync(connection, transaction, token, withMore: true, extraNullMore: true);
            await RunBaselineAsync(connection, transaction, token);
            var withNullByBaseline = await ReadAsync(connection, transaction, token);
            Assert.Equal(("OLD-ORDER", "OLD-PRODUCE"), withNullByBaseline.Master);
            // 除主表单号外，其余状态两侧仍逐项一致
            Assert.Equal(withNullByBaseline.Details, withNullByEffect.Details);
            Assert.Equal(withNullByBaseline.MoreQty, withNullByEffect.MoreQty);
        }
        finally
        {
            await transaction.RollbackAsync(token);
        }
    }

    /// <summary>旧过程 `P_PUR_APPLY_After_Save` 的写段语句链（补行 → 清零 → 回填 → 订单字段 → 分配 → 单号串联）。</summary>
    private static Task RunBaselineAsync(SqlConnection connection, SqlTransaction transaction, CancellationToken token)
    {
        const string batch = """
            exec sp_executesql N'
                select APPLY_TYPE, APPLY_NO, 0 as SERIAL_NO, PRO_NO, sum(REQUIRE_QTY) REQUIRE_QTY into #tmp_more
                  from PUR_APPLY_MORE where APPLY_TYPE=@apply_type and APPLY_NO=@apply_no
                   and PRO_NO not in (select PRO_NO from PUR_APPLY_D where APPLY_TYPE=@apply_type and APPLY_NO=@apply_no)
                 group by APPLY_TYPE, APPLY_NO, PRO_NO;
                declare @i int, @serial_no int, @pro_no nchar(30), @pro_more nchar(30);
                declare @qty decimal(18,8), @qty_more decimal(18,8), @order_no nvarchar(20), @produce_no nvarchar(20),
                        @order_count nvarchar(300), @produce_count nvarchar(300);
                select @i = max(SERIAL_NO) from PUR_APPLY_D where APPLY_TYPE=@apply_type and APPLY_NO=@apply_no;
                select @i = isnull(@i, 0);
                update #tmp_more set @i = @i + 1, SERIAL_NO = @i;
                insert into PUR_APPLY_D(APPLY_TYPE, APPLY_NO, SERIAL_NO, PRO_NO, DEPOT_ID, QTY, UNIT_ID)
                    select t.APPLY_TYPE, t.APPLY_NO, t.SERIAL_NO, t.PRO_NO, p.DEPOT_ID, t.REQUIRE_QTY, p.UNIT_ID
                      from #tmp_more t left join PRODUCT p on t.PRO_NO = p.PRO_NO;
                update PUR_APPLY_D set REQUIRE_QTY = 0 where APPLY_TYPE=@apply_type and APPLY_NO=@apply_no;
                update PUR_APPLY_D set REQUIRE_QTY=s.REQUIRE_QTY, LOST_QTY=s.LOST_QTY
                    from (select APPLY_TYPE, APPLY_NO, PRO_NO, sum(REQUIRE_QTY) REQUIRE_QTY, sum(LOST_QTY) LOST_QTY
                            from PUR_APPLY_MORE where APPLY_TYPE=@apply_type and APPLY_NO=@apply_no
                           group by APPLY_TYPE, APPLY_NO, PRO_NO) s
                   where PUR_APPLY_D.APPLY_TYPE=s.APPLY_TYPE and PUR_APPLY_D.APPLY_NO=s.APPLY_NO
                     and PUR_APPLY_D.PRO_NO=s.PRO_NO
                     and PUR_APPLY_D.APPLY_TYPE=@apply_type and PUR_APPLY_D.APPLY_NO=@apply_no;
                update PUR_APPLY_D set ORDER_TYPE=d.ORDER_TYPE, ORDER_NO=d.ORDER_NO, ORDER_SERIAL_NO=d.SERIAL_NO,
                       CLIENT_PRO_NO=d.CLIENT_PRO_NO, CLIENT_ORDER_NO=d.CLIENT_ORDER_NO, USED_DATE=d.PRE_SEND_DATE,
                       CLIENT_ID=c.CLIENT_ID
                    from COP_ORDER_M c, COP_ORDER_D d, PUR_APPLY_MORE m
                   where m.APPLY_TYPE=@apply_type and m.APPLY_NO=@apply_no
                     and m.ORDER_TYPE=d.ORDER_TYPE and m.ORDER_NO=d.ORDER_NO and m.ORDER_SERIAL_NO=d.SERIAL_NO
                     and c.ORDER_TYPE=d.ORDER_TYPE and c.ORDER_NO=d.ORDER_NO
                     and m.APPLY_TYPE=PUR_APPLY_D.APPLY_TYPE and m.APPLY_NO=PUR_APPLY_D.APPLY_NO
                     and m.PRO_NO=PUR_APPLY_D.PRO_NO;
                update PUR_APPLY_MORE set QTY = 0 where APPLY_TYPE=@apply_type and APPLY_NO=@apply_no;
                select @pro_no = '''';
                declare cur_tmp cursor for
                    select SERIAL_NO, PRO_NO, REQUIRE_QTY from PUR_APPLY_MORE
                     where APPLY_TYPE=@apply_type and APPLY_NO=@apply_no order by PRO_NO, SERIAL_NO;
                open cur_tmp
                fetch next from cur_tmp into @serial_no, @pro_more, @qty_more
                while @@FETCH_STATUS = 0
                begin
                    if @pro_no <> @pro_more
                        select @pro_no = @pro_more, @qty = QTY from PUR_APPLY_D
                         where APPLY_TYPE=@apply_type and APPLY_NO=@apply_no and PRO_NO=@pro_more
                    if @qty > @qty_more
                        update PUR_APPLY_MORE set QTY = @qty_more, @qty = @qty - @qty_more
                         where APPLY_TYPE=@apply_type and APPLY_NO=@apply_no and SERIAL_NO=@serial_no
                    else if @qty > 0 begin
                        update PUR_APPLY_MORE set QTY = @qty
                         where APPLY_TYPE=@apply_type and APPLY_NO=@apply_no and SERIAL_NO=@serial_no
                        select @qty = @qty - @qty_more
                    end
                    fetch next from cur_tmp into @serial_no, @pro_more, @qty_more
                end
                close cur_tmp
                deallocate cur_tmp;
                select @order_count = '''';
                declare cur_order cursor for
                    select distinct ORDER_NO from PUR_APPLY_MORE
                     where APPLY_TYPE=@apply_type and APPLY_NO=@apply_no order by ORDER_NO;
                open cur_order
                fetch next from cur_order into @order_no
                while @@FETCH_STATUS = 0
                begin
                    select @order_count = @order_count + rtrim(@order_no)
                    fetch next from cur_order into @order_no
                    if @@FETCH_STATUS = 0 select @order_count = @order_count + '',''
                end
                close cur_order
                deallocate cur_order
                if @order_count <> ''''
                    update PUR_APPLY_M set ORDER_NO=@order_count where APPLY_TYPE=@apply_type and APPLY_NO=@apply_no;
                select @produce_count = '''';
                declare cur_produce cursor for
                    select distinct PRODUCE_NO from PUR_APPLY_MORE
                     where APPLY_TYPE=@apply_type and APPLY_NO=@apply_no order by PRODUCE_NO;
                open cur_produce
                fetch next from cur_produce into @produce_no
                while @@FETCH_STATUS = 0
                begin
                    select @produce_count = @produce_count + rtrim(@produce_no)
                    fetch next from cur_produce into @produce_no
                    if @@FETCH_STATUS = 0 select @produce_count = @produce_count + '',''
                end
                close cur_produce
                deallocate cur_produce
                if @produce_count <> ''''
                    update PUR_APPLY_M set PRODUCE_NO=@produce_count where APPLY_TYPE=@apply_type and APPLY_NO=@apply_no;
            ', N'@apply_type nchar(10), @apply_no nchar(20)', @apply_type=@Type, @apply_no=@No;
            """;
        return ExecuteAsync(connection, transaction, token, batch, ("@Type", Type), ("@No", No));
    }

    private static async Task SeedAsync(
        SqlConnection connection, SqlTransaction transaction, CancellationToken token, bool withMore,
        bool extraNullMore)
    {
        await ExecuteAsync(connection, transaction, token, """
            DELETE FROM dbo.PUR_APPLY_MORE WHERE APPLY_TYPE=@Type;
            DELETE FROM dbo.PUR_APPLY_D WHERE APPLY_TYPE=@Type;
            DELETE FROM dbo.PUR_APPLY_M WHERE APPLY_TYPE=@Type;
            DELETE FROM dbo.COP_ORDER_D WHERE ORDER_TYPE=@OrderType;
            DELETE FROM dbo.COP_ORDER_M WHERE ORDER_TYPE=@OrderType;
            DELETE FROM dbo.PRODUCT WHERE PRO_NO IN (@P1, @P2);

            INSERT INTO dbo.PRODUCT (PRO_NO, DEPOT_ID, UNIT_ID) VALUES (@P1, N'D1', N'U1'), (@P2, N'D2', N'U2');
            INSERT INTO dbo.COP_ORDER_M (ORDER_TYPE, ORDER_NO, CLIENT_ID) VALUES
                (@OrderType, N'PO-1', N'ADR12PAC1'), (@OrderType, N'PO-2', N'ADR12PAC2');
            INSERT INTO dbo.COP_ORDER_D (ORDER_TYPE, ORDER_NO, SERIAL_NO, CLIENT_PRO_NO, CLIENT_ORDER_NO, PRE_SEND_DATE) VALUES
                (@OrderType, N'PO-1', 1, N'CP1', N'CO1', '2026-03-01'),
                (@OrderType, N'PO-2', 1, N'CP2', N'CO2', '2026-03-02');
            INSERT INTO dbo.PUR_APPLY_M (APPLY_TYPE, APPLY_NO, ORDER_NO, PRODUCE_NO) VALUES (@Type, @No, N'OLD-ORDER', N'OLD-PRODUCE');
            INSERT INTO dbo.PUR_APPLY_D (APPLY_TYPE, APPLY_NO, SERIAL_NO, PRO_NO, QTY, REQUIRE_QTY, LOST_QTY, DEPOT_ID, UNIT_ID) VALUES
                (@Type, @No, 1, @P1, 5, 99, 99, N'X', N'X'),
                (@Type, @No, 2, @P2, 2, 99, 99, N'X', N'X');
            """,
            ("@Type", Type), ("@No", No), ("@OrderType", OrderType), ("@P1", P1), ("@P2", P2));
        if (!withMore) return;
        await ExecuteAsync(connection, transaction, token, """
            INSERT INTO dbo.PUR_APPLY_MORE (APPLY_TYPE, APPLY_NO, SERIAL_NO, PRO_NO, QTY, REQUIRE_QTY, LOST_QTY,
                                            ORDER_TYPE, ORDER_NO, ORDER_SERIAL_NO, PRODUCE_NO) VALUES
                (@Type, @No, 1, @P1, 77, 3, 1, @OrderType, N'PO-1', 1, N'PR1'),
                (@Type, @No, 2, @P1, 77, 1, 0, @OrderType, N'PO-1', 1, N'PR2'),
                (@Type, @No, 3, @P2, 77, 4, 2, @OrderType, N'PO-2', 1, N'PR1'),
                (@Type, @No, 4, @P3, 77, 2, 0, NULL, N'PO-2', NULL, N'PR2');
            """,
            ("@Type", Type), ("@No", No), ("@OrderType", OrderType), ("@P1", P1), ("@P2", P2), ("@P3", P3));
        if (!extraNullMore) return;
        // 一条"没有关联订单"的待购行：用于钉住主表单号串联的既有差异（见用例 ④）
        await ExecuteAsync(connection, transaction, token, """
            INSERT INTO dbo.PUR_APPLY_MORE (APPLY_TYPE, APPLY_NO, SERIAL_NO, PRO_NO, QTY, REQUIRE_QTY, LOST_QTY)
            VALUES (@Type, @No, 5, @P1, 77, 0, 0);
            """, ("@Type", Type), ("@No", No), ("@P1", P1));
    }

    private static async Task<((string OrderNo, string ProduceNo) Master, List<string> Details, List<double> MoreQty)>
        ReadAsync(SqlConnection connection, SqlTransaction transaction, CancellationToken token)
    {
        string orderNo, produceNo;
        await using (var master = new SqlCommand(
            "SELECT ISNULL(ORDER_NO,''), ISNULL(PRODUCE_NO,'') FROM dbo.PUR_APPLY_M WHERE APPLY_TYPE=@Type AND APPLY_NO=@No;",
            connection, transaction))
        {
            master.Parameters.Add("@Type", SqlDbType.NVarChar, 20).Value = Type;
            master.Parameters.Add("@No", SqlDbType.NVarChar, 40).Value = No;
            await using var reader = await master.ExecuteReaderAsync(token);
            Assert.True(await reader.ReadAsync(token));
            orderNo = reader.GetString(0);
            produceNo = reader.GetString(1);
        }
        var details = new List<string>();
        await using (var detail = new SqlCommand("""
            SELECT SERIAL_NO, LTRIM(RTRIM(PRO_NO)), REQUIRE_QTY, LOST_QTY, QTY, LTRIM(RTRIM(ISNULL(DEPOT_ID,''))),
                   LTRIM(RTRIM(ISNULL(UNIT_ID,''))), LTRIM(RTRIM(ISNULL(ORDER_TYPE,''))),
                   LTRIM(RTRIM(ISNULL(ORDER_NO,''))), ORDER_SERIAL_NO, LTRIM(RTRIM(ISNULL(CLIENT_PRO_NO,''))),
                   LTRIM(RTRIM(ISNULL(CLIENT_ORDER_NO,''))), ISNULL(CONVERT(varchar(10), USED_DATE, 120),''),
                   LTRIM(RTRIM(ISNULL(CLIENT_ID,'')))
            FROM dbo.PUR_APPLY_D WHERE APPLY_TYPE=@Type AND APPLY_NO=@No ORDER BY SERIAL_NO;
            """, connection, transaction))
        {
            detail.Parameters.Add("@Type", SqlDbType.NVarChar, 20).Value = Type;
            detail.Parameters.Add("@No", SqlDbType.NVarChar, 40).Value = No;
            await using var reader = await detail.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                details.Add(string.Join('|', Enumerable.Range(0, reader.FieldCount)
                    .Select(index => reader.IsDBNull(index) ? string.Empty : Convert.ToString(reader.GetValue(index), System.Globalization.CultureInfo.InvariantCulture)?.Trim())));
            }
        }
        var moreQty = new List<double>();
        await using (var more = new SqlCommand(
            "SELECT ISNULL(QTY,0) FROM dbo.PUR_APPLY_MORE WHERE APPLY_TYPE=@Type AND APPLY_NO=@No ORDER BY SERIAL_NO;",
            connection, transaction))
        {
            more.Parameters.Add("@Type", SqlDbType.NVarChar, 20).Value = Type;
            more.Parameters.Add("@No", SqlDbType.NVarChar, 40).Value = No;
            await using var reader = await more.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token)) moreQty.Add(Convert.ToDouble(reader.GetValue(0)));
        }
        return ((orderNo, produceNo), details, moreQty);
    }

    private static async Task<EffectActionPlan> LoadActionAsync(
        SqlConnection connection, SqlTransaction transaction, CancellationToken token)
    {
        await using var command = new SqlCommand("""
            SELECT SEQ, EVENT_CODE, EFFECT_KEY, EFFECT_NAME, ENABLED, FAIL_MODE,
                   CONDITION_STRUCT, PARAM_STRUCT, REVERSE_STRUCT
            FROM dbo.MODULE_BUSINESS_ACTION
            WHERE M_IDX=@ModuleId AND EVENT_CODE=N'SAVE' AND SEQ=1;
            """, connection, transaction);
        command.Parameters.Add("@ModuleId", SqlDbType.Int).Value = ModuleId;
        await using var reader = await command.ExecuteReaderAsync(token);
        Assert.True(await reader.ReadAsync(token), $"模块 {ModuleId} 缺少 SAVE 期动作");
        return new EffectActionPlan(
            reader.GetInt32(0), reader.GetString(1), reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetBoolean(4), reader.GetString(5),
            Parse(reader.IsDBNull(6) ? null : reader.GetString(6)),
            Parse(reader.IsDBNull(7) ? null : reader.GetString(7)),
            Parse(reader.IsDBNull(8) ? null : reader.GetString(8)),
            Array.Empty<EffectOpPlan>());
    }

    private static JsonElement? Parse(string? json)
        => string.IsNullOrWhiteSpace(json) ? null : JsonDocument.Parse(json).RootElement.Clone();

    private static async Task ExecuteAsync(
        SqlConnection connection, SqlTransaction transaction, CancellationToken token, string sql,
        params (string Name, object? Value)[] parameters)
    {
        await using var command = new SqlCommand(sql, connection, transaction);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(token);
    }

    private static Task ExecuteAsync(
        SqlConnection connection, SqlTransaction transaction, string sql, CancellationToken token,
        params (string Name, object? Value)[] parameters)
        => ExecuteAsync(connection, transaction, token, sql, parameters);
}
