using System.Data;
using System.Globalization;
using System.Text.Json;
using EOS.API.Data.Effects;
using EOS.API.Data.Effects.ServiceEffectHandlers;
using Microsoft.Data.SqlClient;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 工序生产计划（2708，原 `sfc-plan` C#）入效果目录后的真库验证：
/// SAVE 期 `sfc-plan-sync` 承接旧过程 `P_SFC_PLAN_After_Save` 的"明细行补全 + 五步回填"整链
/// （补行 → 数量清零 → 数量/生产数量/工时/单人时回填 → 工序类别 → 固定时间归一 → 生产号追加）。
/// 用例把**旧过程语句链内联为基准**（`sp_executesql` 嵌套作用域，避免 #临时表残留），
/// 在同一初始态下比较明细最终行集合；并覆盖两个易错语义：
///   ① 明细补行的客户取产品档案，**产品档案缺行时仍要补行**（客户为空，等价既有实现过程的 left join）；
///   ② 补行得到的行生产号为空，追加后仍为空（旧过程未做空值兜底）。
/// 另跑一遍"连续保存两次"的对拍，覆盖生产号重复追加的既有行为。
/// 造数用 `ADR12SP` 前缀，事务结束回滚。
/// </summary>
[Trait("Category", "Integration")]
[Collection("live-database")]
public sealed class SfcPlanSyncLiveTests
{
    private static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("MSSQL_ERP_CONN")
        ?? throw new InvalidOperationException(
            "真库集成测试需要 MSSQL_ERP_CONN；未配置即失败（无连接跳过≠已验证）。");

    private const int ModuleId = 2708;
    private const string Type = "ADR12SP";
    private const string No = "ADR12SP001";
    private const string PlanDate = "2026-01-05";

    private const string P1 = "ADR12SPP1";   // 有产品档案，客户 C1
    private const string P2 = "ADR12SPP2";   // 有产品档案，客户 C2
    private const string P3 = "ADR12SPP3";   // 无产品档案（验证补行仍发生）
    private const string Pr1 = "ADR12SPR1";  // 非固定时间
    private const string Pr2 = "ADR12SPR2";  // 固定时间（USE_STAND_TIME=1）
    private const string Pr3 = "ADR12SPR3";

    [Fact]
    public async Task 工序生产计划_明细补全与回填_与旧过程一致()
    {
        var token = CancellationToken.None;
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
        try
        {
            await SeedAsync(connection, transaction, token);
            var action = await LoadActionAsync(connection, transaction, token);
            Assert.Equal("sfc-plan-sync", action.EffectKey);
            var plan = new ModuleEffectPlan(ModuleId, "SFC_PLAN_M", "SFC_PLAN_D", "live-sfc-plan",
                ["PLAN_TYPE", "PLAN_NO"], [action], []);

            // ① 目录效果
            await new SfcPlanSyncHandler().ExecuteAsync(
                new ServiceEffectContext(connection, transaction, plan, action, EffectEvent.Save,
                    No, [Type, No], "live-test"), token);
            var byEffect = await ReadAsync(connection, transaction, token);

            // ② 回到同一初始态跑旧过程语句链
            await SeedAsync(connection, transaction, token);
            await RunBaselineAsync(connection, transaction, token);
            var byBaseline = await ReadAsync(connection, transaction, token);

            // ③ 明细最终行集合逐字一致（序号按集合比对：两侧的补行顺序都不保证）
            Assert.Equal(byBaseline.Count, byEffect.Count);
            Assert.Equal(byBaseline, byEffect);
            Assert.Equal([1, 2, 3, 4], byEffect.Select(row => row.Serial).OrderBy(value => value));

            // ④ 逐行钉住业务结论
            Assert.Equal([
                // 原有行：数量取待排数量之和、工时为待排数量×单位用量×单人时，单人时取制程明细
                "ADR12SPP1|ADR12SPR1|10|100|60|7|ADR12SPT1|BASE0001|ADR12SPC1",
                // 补行 + 固定时间工序：标准时间为 4 ⇒ 工时 4，数量归一为 1，生产号为空
                "ADR12SPP1|ADR12SPR2|1|1|4|11|ADR12SPT2||ADR12SPC1",
                "ADR12SPP2|ADR12SPR1|4|40|4|13|ADR12SPT1||ADR12SPC2",
                // 产品档案缺行：仍补行，客户为空
                "ADR12SPP3|ADR12SPR3|6|60|6|17|ADR12SPT3||",
            ], byEffect.Select(row => row.Signature).OrderBy(value => value, StringComparer.Ordinal));

            // ⑤ 连续保存两次的对拍：生产号按既有行为重复追加，两侧仍须一致
            await SeedAsync(connection, transaction, token);
            await new SfcPlanSyncHandler().ExecuteAsync(
                new ServiceEffectContext(connection, transaction, plan, action, EffectEvent.Save,
                    No, [Type, No], "live-test"), token);
            await new SfcPlanSyncHandler().ExecuteAsync(
                new ServiceEffectContext(connection, transaction, plan, action, EffectEvent.Save,
                    No, [Type, No], "live-test"), token);
            var twiceByEffect = await ReadAsync(connection, transaction, token);

            await SeedAsync(connection, transaction, token);
            await RunBaselineAsync(connection, transaction, token);
            await RunBaselineAsync(connection, transaction, token);
            var twiceByBaseline = await ReadAsync(connection, transaction, token);

            Assert.Equal(twiceByBaseline, twiceByEffect);
            Assert.Contains("|BASE00010001|", string.Join("\n", twiceByEffect.Select(row => row.Signature)));
        }
        finally
        {
            await transaction.RollbackAsync(token);
        }
    }

    /// <summary>旧过程 `P_SFC_PLAN_After_Save` 的语句链（嵌套作用域内建 #临时表，结束即释放）。</summary>
    private static Task RunBaselineAsync(SqlConnection connection, SqlTransaction transaction, CancellationToken token)
    {
        const string batch = """
            declare @plan_type nchar(10) = @Type, @plan_no nchar(20) = @No;
            exec sp_executesql N'
                select m.PLAN_TYPE, m.PLAN_NO, 0 as SERIAL_NO, m.PRO_NO, d.PROCEDURE_ID,
                       QTY = sum(m.QTY*d.PROCESS_QTY*d.PERSON_HOUR_UNIT)
                  into #tmp_more
                  from SFC_PLAN_MORE m, SFC_PROCESS_D d
                 where m.PLAN_TYPE=@plan_type and m.PLAN_NO=@plan_no and m.PRO_NO=d.PRO_NO
                   and d.PRO_NO+d.PROCEDURE_ID not in
                       (select PRO_NO+PROCEDURE_ID from SFC_PLAN_D where PLAN_TYPE=@plan_type and PLAN_NO=@plan_no)
                 group by m.PLAN_TYPE, m.PLAN_NO, m.PRO_NO, d.PROCEDURE_ID;

                declare @i int, @serial_no int;
                select @i = max(SERIAL_NO) from SFC_PLAN_D where PLAN_TYPE=@plan_type and PLAN_NO=@plan_no;
                select @i = isnull(@i, 0);
                update #tmp_more set @i = @i + 1, SERIAL_NO = @i;

                insert into SFC_PLAN_D(PLAN_TYPE, PLAN_NO, SERIAL_NO, PRO_NO, PROCEDURE_ID, QTY, CLIENT_ID)
                    select t.PLAN_TYPE, t.PLAN_NO, t.SERIAL_NO, t.PRO_NO, t.PROCEDURE_ID, t.QTY, p.CLIENT_ID
                      from #tmp_more t left join PRODUCT p on t.PRO_NO = p.PRO_NO;

                update SFC_PLAN_D set QTY = 0 where PLAN_TYPE=@plan_type and PLAN_NO=@plan_no;

                update SFC_PLAN_D set QTY=s.QTY, PRODUCE_QTY=s.PRODUCE_QTY, HOURS=s.HOURS, PERSON_UNIT_HOUR=s.PERSON_UNIT_HOUR
                    from (select m.PLAN_TYPE, m.PLAN_NO, m.PRO_NO, d.PROCEDURE_ID, d.STANDARD_TIME, d.PERSON_UNIT_HOUR,
                                 PRODUCE_QTY=sum(m.PRODUCE_QTY), QTY=sum(m.QTY),
                                 HOURS=case when ISNULL(d.STANDARD_TIME,0)>0 then max(d.STANDARD_TIME)
                                            else sum(m.QTY*d.PROCESS_QTY*d.PERSON_HOUR_UNIT) end
                            from SFC_PLAN_MORE m, SFC_PROCESS_D d
                           where m.PLAN_TYPE=@plan_type and m.PLAN_NO=@plan_no and m.PRO_NO=d.PRO_NO
                           group by m.PLAN_TYPE, m.PLAN_NO, m.PRO_NO, d.PROCEDURE_ID, d.STANDARD_TIME, d.PERSON_UNIT_HOUR) s
                   where SFC_PLAN_D.PLAN_TYPE=s.PLAN_TYPE and SFC_PLAN_D.PLAN_NO=s.PLAN_NO
                     and SFC_PLAN_D.PRO_NO=s.PRO_NO and SFC_PLAN_D.PROCEDURE_ID=s.PROCEDURE_ID
                     and SFC_PLAN_D.PLAN_TYPE=@plan_type and SFC_PLAN_D.PLAN_NO=@plan_no;

                update SFC_PLAN_D set PROCEDURE_TYPE_ID = SFC_PROCEDURE.PROCEDURE_TYPE_ID
                    from SFC_PROCEDURE
                   where SFC_PLAN_D.PROCEDURE_ID = SFC_PROCEDURE.PROCEDURE_ID
                     and SFC_PLAN_D.PLAN_TYPE=@plan_type and SFC_PLAN_D.PLAN_NO=@plan_no;

                update SFC_PLAN_D set QTY=1, PRODUCE_QTY=1
                    from SFC_PROCEDURE
                   where SFC_PROCEDURE.USE_STAND_TIME=1 and SFC_PROCEDURE.PROCEDURE_ID = SFC_PLAN_D.PROCEDURE_ID
                     and SFC_PLAN_D.PLAN_TYPE=@plan_type and SFC_PLAN_D.PLAN_NO=@plan_no;

                update SFC_PLAN_D set PRODUCE_NO = RTRIM(SFC_PLAN_D.PRODUCE_NO)+RIGHT(RTRIM(SFC_PLAN_MORE.PRODUCE_NO),4)
                    from SFC_PLAN_MORE
                   where SFC_PLAN_MORE.PLAN_TYPE=SFC_PLAN_D.PLAN_TYPE and SFC_PLAN_MORE.PLAN_NO=SFC_PLAN_D.PLAN_NO
                     and SFC_PLAN_MORE.PRO_NO=SFC_PLAN_D.PRO_NO
                     and SFC_PLAN_D.PLAN_TYPE=@plan_type and SFC_PLAN_D.PLAN_NO=@plan_no;
            ', N'@plan_type nchar(10), @plan_no nchar(20)', @plan_type=@Type, @plan_no=@No;
            """;
        return ExecuteAsync(connection, transaction, token, batch, ("@Type", Type), ("@No", No));
    }

    private static async Task<List<SfcPlanRow>> ReadAsync(
        SqlConnection connection, SqlTransaction transaction, CancellationToken token)
    {
        await using var command = new SqlCommand("""
            SELECT SERIAL_NO, PRO_NO, PROCEDURE_ID, QTY, PRODUCE_QTY, HOURS, PERSON_UNIT_HOUR,
                   PROCEDURE_TYPE_ID, PRODUCE_NO, CLIENT_ID
            FROM dbo.SFC_PLAN_D WHERE PLAN_TYPE=@Type AND PLAN_NO=@No;
            """, connection, transaction);
        command.Parameters.Add("@Type", SqlDbType.NVarChar, 20).Value = Type;
        command.Parameters.Add("@No", SqlDbType.NVarChar, 40).Value = No;
        var rows = new List<SfcPlanRow>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            rows.Add(new SfcPlanRow(
                reader.GetInt16(0),
                Str(reader, 1), Str(reader, 2), Num(reader, 3), Num(reader, 4), Num(reader, 5), Num(reader, 6),
                Str(reader, 7), Str(reader, 8), Str(reader, 9)));
        }
        return rows.OrderBy(row => row.Signature, StringComparer.Ordinal).ToList();
    }

    private static string Str(SqlDataReader reader, int index)
        => reader.IsDBNull(index) ? string.Empty : reader.GetValue(index).ToString()?.Trim() ?? string.Empty;

    private static string Num(SqlDataReader reader, int index)
        => reader.IsDBNull(index)
            ? string.Empty
            : Convert.ToDecimal(reader.GetValue(index)).ToString("0.####", CultureInfo.InvariantCulture);

    private static async Task SeedAsync(SqlConnection connection, SqlTransaction transaction, CancellationToken token)
    {
        await ExecuteAsync(connection, transaction, token, """
            DELETE FROM dbo.SFC_PLAN_D WHERE PLAN_TYPE=@Type;
            DELETE FROM dbo.SFC_PLAN_MORE WHERE PLAN_TYPE=@Type;
            DELETE FROM dbo.SFC_PLAN_M WHERE PLAN_TYPE=@Type;
            DELETE FROM dbo.SFC_PROCESS_D WHERE PRO_NO IN (@P1, @P2, @P3);
            DELETE FROM dbo.SFC_PROCEDURE WHERE PROCEDURE_ID IN (@Pr1, @Pr2, @Pr3);
            DELETE FROM dbo.PRODUCT WHERE PRO_NO IN (@P1, @P2);

            INSERT INTO dbo.PRODUCT (PRO_NO, CLIENT_ID) VALUES (@P1, N'ADR12SPC1'), (@P2, N'ADR12SPC2');
            INSERT INTO dbo.SFC_PROCEDURE (PROCEDURE_ID, PROCEDURE_TYPE_ID, USE_STAND_TIME) VALUES
                (@Pr1, N'ADR12SPT1', 0), (@Pr2, N'ADR12SPT2', 1), (@Pr3, N'ADR12SPT3', 0);
            INSERT INTO dbo.SFC_PROCESS_D (PRO_NO, SERIAL_NO, PROCEDURE_ID, PROCESS_QTY, PERSON_HOUR_UNIT, PERSON_UNIT_HOUR, STANDARD_TIME) VALUES
                (@P1, 1, @Pr1, 2, 3, 7, 0),
                (@P1, 2, @Pr2, 1, 5, 11, 4),
                (@P2, 1, @Pr1, 1, 1, 13, 0),
                (@P3, 1, @Pr3, 1, 1, 17, 0);

            INSERT INTO dbo.SFC_PLAN_M (PLAN_TYPE, PLAN_NO, PLAN_DATE) VALUES (@Type, @No, @PlanDate);
            INSERT INTO dbo.SFC_PLAN_MORE (PLAN_TYPE, PLAN_NO, SERIAL_NO, PRO_NO, QTY, PRODUCE_QTY, PRODUCE_NO) VALUES
                (@Type, @No, 1, @P1, 10, 100, N'WO-0001'),
                (@Type, @No, 2, @P2, 4, 40, N'WO-0002'),
                (@Type, @No, 3, @P3, 6, 60, N'WO-0003');
            -- 明细先只有 (P1,Pr1) 一行，其余三组由保存期动作补齐；原有行的客户由录入端给定，保存期不改
            INSERT INTO dbo.SFC_PLAN_D (PLAN_TYPE, PLAN_NO, SERIAL_NO, PRO_NO, PROCEDURE_ID, QTY, PRODUCE_QTY, HOURS, PERSON_UNIT_HOUR, PRODUCE_NO, CLIENT_ID) VALUES
                (@Type, @No, 1, @P1, @Pr1, 0, 0, 0, 0, N'BASE', N'ADR12SPC1');
            """,
            ("@Type", Type), ("@No", No), ("@PlanDate", PlanDate),
            ("@P1", P1), ("@P2", P2), ("@P3", P3), ("@Pr1", Pr1), ("@Pr2", Pr2), ("@Pr3", Pr3));
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

    private sealed record SfcPlanRow(
        int Serial, string Product, string Procedure, string Qty, string ProduceQty, string Hours,
        string PersonUnitHour, string ProcedureType, string ProduceNo, string Client)
    {
        public string Signature =>
            $"{Product}|{Procedure}|{Qty}|{ProduceQty}|{Hours}|{PersonUnitHour}|{ProcedureType}|{ProduceNo}|{Client}";
    }
}
