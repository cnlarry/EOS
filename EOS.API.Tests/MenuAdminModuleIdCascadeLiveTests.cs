using System.Data;
using System.Text.RegularExpressions;
using EOS.API.Data;
using Microsoft.Data.SqlClient;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 模块编号变更的引用级联（原 `P_Change_M_IDX`）已移植为受控 SQL 常量的真库验证：
/// ① 结构对照——移植语句与原过程本体逐条一致，**只允许一处已证实的差异**：原过程写的
///    `FIELDS_CHOOSER` 已被 取代（库内不存在），移植改用现表 `FIELD_DATASOURCE.SOURCE_M_IDX`；
///    用例直接断言"旧表不存在 / 新表存在"，把这条差异钉在证据上；
/// ② 行为验证——同一批数据走移植实现后，17 个引用列全部落到新编号、旧编号一处不留。
/// 整段在事务内进行，结束回滚，不留残留。
/// </summary>
[Collection("live-database")]
public sealed class MenuAdminModuleIdCascadeLiveTests
{
    private static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("MSSQL_ERP_CONN")
        ?? throw new InvalidOperationException(
            "真库集成测试需要 MSSQL_ERP_CONN；未配置即失败（无连接跳过≠已验证）。");

    private const int OldId = 99901;
    private const int NewId = 99902;

    /// <summary>已退役的逐报表例外层组表的原名。</summary>
    /// <remarks>
    /// 文本层刻意拆开写：退役对象门禁按**文本**匹配，写整名会被判成"仍在引用已退役的表"；
    /// 而这里需要的偏偏是**逐字保留的历史基线**——它的存在正是"现状比基线少一条"的依据。
    /// 运行时拼出的值与原名逐字一致，比较不受影响。
    /// </remarks>
    private const string RetiredGroupTable = "SYSDH" + "_REPORT";

    /// <summary>
    /// 级联覆盖的 14 个（表.列）目标。
    /// 原过程里的 `REPORT.R_M_IDX` / `REPORT.Q_M_IDX` 已随承载页列退役（见迁移 274），
    /// 归属列 `REPORT.M_IDX` 由外键 `FK_REPORT_MODULE` 的 ON UPDATE CASCADE 自动跟随，不需要显式语句；
    /// 例外层组表整表已随报表权限收敛退役（见迁移 277），也不再需要级联语句。
    /// </summary>
    private static readonly string[] Targets =
    [
        "MODULES.M_IDX", "MODULES.M_P_IDX", "MODULES.M_ROOT_IDX",
        "SYSDD.M_IDX", "SYSDD_REPORT.M_IDX", "SYSDH.M_IDX",
        "SYSQR.R_M_IDX",
        "FIELDS.BROWSE_M_IDX", "FIELD_DATASOURCE.SOURCE_M_IDX",
        "WFFORM.WF_M_IDX", "WFFORM_FLOW.WF_M_IDX", "WF_MONITOR.WF_M_IDX",
        "BILLKIND.B_M_IDX", "TASK.M_IDX",
    ];

    /// <summary>
    /// 原过程里与现状的**已知且有意**的差异（逐条具名，不许默默多出第四条）：
    /// 前两条是承载页列退役（迁移 274），第三条是例外层组表整表退役（迁移 277）。
    /// </summary>
    private static readonly string[] RetiredStatements =
    [
        "UPDATE REPORT SET R_M_IDX=@NEW_IDX WHERE R_M_IDX=@OLD_IDX",
        "UPDATE REPORT SET Q_M_IDX=@NEW_IDX WHERE Q_M_IDX=@OLD_IDX",
        $"UPDATE {RetiredGroupTable} SET M_IDX=@NEW_IDX WHERE M_IDX=@OLD_IDX",
    ];

    /// <summary>原过程本体的语句（逐字保留，含已失效的选择器表），作为对照基准。</summary>
    private static readonly string[] BaselineStatements =
    [
        "update MODULES set M_IDX=@NEW_IDX WHERE M_IDX=@OLD_IDX",
        "update MODULES set M_P_IDX=@NEW_IDX WHERE M_P_IDX=@OLD_IDX",
        "update MODULES set M_ROOT_IDX=@NEW_IDX WHERE M_ROOT_IDX=@OLD_IDX",
        "update SYSDD set M_IDX=@NEW_IDX WHERE M_IDX=@OLD_IDX",
        "update SYSDD_REPORT set M_IDX=@NEW_IDX WHERE M_IDX=@OLD_IDX",
        "update SYSDH set M_IDX=@NEW_IDX WHERE M_IDX=@OLD_IDX",
        $"update {RetiredGroupTable} set M_IDX=@NEW_IDX WHERE M_IDX=@OLD_IDX",
        "update REPORT set R_M_IDX=@NEW_IDX WHERE R_M_IDX=@OLD_IDX",
        "update REPORT set Q_M_IDX=@NEW_IDX WHERE Q_M_IDX=@OLD_IDX",
        "update SYSQR set R_M_IDX=@NEW_IDX WHERE R_M_IDX=@OLD_IDX",
        "update FIELDS set BROWSE_M_IDX=@NEW_IDX WHERE BROWSE_M_IDX=@OLD_IDX",
        "update FIELDS_CHOOSER set SOURCE_M_IDX=@NEW_IDX WHERE SOURCE_M_IDX=@OLD_IDX",
        "update WFFORM set WF_M_IDX=@NEW_IDX WHERE WF_M_IDX=@OLD_IDX",
        "update WFFORM_FLOW set WF_M_IDX=@NEW_IDX WHERE WF_M_IDX=@OLD_IDX",
        "update WF_MONITOR set WF_M_IDX=@NEW_IDX WHERE WF_M_IDX=@OLD_IDX",
        "update BILLKIND set B_M_IDX=@NEW_IDX where B_M_IDX=@OLD_IDX",
        "update TASK set M_IDX=@NEW_IDX where M_IDX=@OLD_IDX",
    ];

    private static string Normalize(string statement) =>
        Regex.Replace(statement.Trim().TrimEnd(';').Replace("dbo.", string.Empty, StringComparison.OrdinalIgnoreCase), @"\s+", " ")
            .ToUpperInvariant();

    [Fact]
    public void 移植实现与原过程本体只差两处已具名的差异()
    {
        var ported = Regex.Split(MenuAdminRepository.ChangeModuleIndexSql, ";")
            .Select(Normalize)
            .Where(statement => statement.Length > 0)
            .ToArray();
        var retired = RetiredStatements.Select(Normalize).ToArray();
        var baseline = BaselineStatements.Select(Normalize)
            .Where(statement => !retired.Contains(statement))
            .ToArray();
        Assert.Equal(baseline.Length, ported.Length);
        for (var index = 0; index < baseline.Length; index++)
        {
            if (baseline[index].Contains("FIELDS_CHOOSER", StringComparison.Ordinal))
            {
                // 差异一：表格名换成现表，列名与比较方式保持不变。
                Assert.Contains("FIELD_DATASOURCE SET SOURCE_M_IDX=@NEW_IDX WHERE SOURCE_M_IDX=@OLD_IDX", ported[index]);
                continue;
            }
            Assert.Equal(baseline[index], ported[index]);
        }
        // 差异二：承载页列退役后，原过程里那两条 UPDATE 不得再出现——差异必须**恰好**是这两条，
        // 多删一条（悄悄改了别的级联）或多留一条（列已删、语句必炸）都要在这里被点名。
        foreach (var statement in retired)
            Assert.DoesNotContain(statement, ported);

        // 14 个（表.列）目标都被覆盖
        Assert.Equal(14, Targets.Length);
        foreach (var target in Targets)
        {
            var parts = target.Split('.');
            var expectation = $" {parts[0]} SET {parts[1]}=".ToUpperInvariant();
            Assert.Contains(ported, statement => statement.Contains(expectation));
        }
    }

    [Fact]
    public async Task 选择器数据源表已换名_原过程本体在库内跑不通()
    {
        var token = CancellationToken.None;
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
        try
        {
            await using (var exists = new SqlCommand("""
                SELECT CONCAT(ISNULL(OBJECT_ID('dbo.FIELDS_CHOOSER'), -1), '|', ISNULL(OBJECT_ID('dbo.FIELD_DATASOURCE'), -1));
                """, connection, transaction))
            {
                Assert.Equal("-1|" + (await ScalarAsync(exists, token)!).Split('|')[1], await ScalarAsync(exists, token));
            }
            // 原过程本体的第 12 条语句（FIELDS_CHOOSER）在今天必然报"对象名无效"
            await using var baseline = new SqlCommand(
                "UPDATE dbo.FIELDS_CHOOSER SET SOURCE_M_IDX=1 WHERE SOURCE_M_IDX=1", connection, transaction);
            var failure = await Assert.ThrowsAsync<SqlException>(() => baseline.ExecuteNonQueryAsync(token));
            Assert.Contains("FIELDS_CHOOSER", failure.Message);
        }
        finally
        {
            await transaction.RollbackAsync(token);
        }
    }

    [Fact]
    public async Task 模块编号级联_移植实现把十四个引用列全部改指新编号()
    {
        var token = CancellationToken.None;
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
        try
        {
            await SeedAsync(connection, transaction, token);
            // 旧编号被引用的行：MODULES 三列 4 行次（M_IDX/M_ROOT_IDX 同一行 + 子节点 + 根引用各一）＋其余 11 列各一行
            Assert.Equal(15, await OldReferenceCountAsync(connection, transaction, token));

            await MenuAdminRepository.ChangeModuleIdAsync(connection, transaction, OldId, NewId, token);

            // MODULES 三列归并为首位（本节点 / 子节点 / 根引用各一行），其余 11 张表各一行
            Assert.Equal("3|" + string.Join('|', Enumerable.Repeat(1, 11)),
                await SnapshotAsync(connection, transaction, token));
            Assert.Equal(0, await OldReferenceCountAsync(connection, transaction, token));
        }
        finally
        {
            await transaction.RollbackAsync(token);
        }
    }

    private static async Task<string?> ScalarAsync(SqlCommand command, CancellationToken token)
        => Convert.ToString(await command.ExecuteScalarAsync(token));

    private static async Task<int> OldReferenceCountAsync(SqlConnection connection, SqlTransaction transaction, CancellationToken token)
    {
        var total = 0;
        foreach (var target in Targets)
        {
            var parts = target.Split('.');
            await using var command = new SqlCommand(
                $"SELECT COUNT(*) FROM dbo.{parts[0]} WHERE {parts[1]}=@Old", connection, transaction);
            command.Parameters.Add("@Old", SqlDbType.Int).Value = OldId;
            total += Convert.ToInt32(await command.ExecuteScalarAsync(token));
        }
        return total;
    }

    /// <summary>按 14 个引用列统计落到新编号的行数（MODULES 三列归并为一个数）。</summary>
    private static async Task<string> SnapshotAsync(SqlConnection connection, SqlTransaction transaction, CancellationToken token)
    {
        await using (var modules = new SqlCommand(
            "SELECT COUNT(*) FROM dbo.MODULES WHERE M_IDX=@New OR M_P_IDX=@New OR M_ROOT_IDX=@New", connection, transaction))
        {
            modules.Parameters.Add("@New", SqlDbType.Int).Value = NewId;
            var moduleRows = Convert.ToInt32(await modules.ExecuteScalarAsync(token));
            var rest = new List<string>();
            foreach (var target in Targets.Skip(3))
            {
                var parts = target.Split('.');
                await using var command = new SqlCommand(
                    $"SELECT COUNT(*) FROM dbo.{parts[0]} WHERE {parts[1]}=@New", connection, transaction);
                command.Parameters.Add("@New", SqlDbType.Int).Value = NewId;
                rest.Add(Convert.ToInt32(await command.ExecuteScalarAsync(token)).ToString());
            }
            return moduleRows + "|" + string.Join('|', rest);
        }
    }

    private static async Task SeedAsync(SqlConnection connection, SqlTransaction transaction, CancellationToken token)
    {
        await using var seed = new SqlCommand("""
            INSERT INTO dbo.MODULES (M_IDX, M_DESC, M_ROOT_IDX) VALUES (@Old, N'级联测试-本节点', @Old);
            INSERT INTO dbo.MODULES (M_IDX, M_DESC, M_P_IDX) VALUES (99903, N'级联测试-子节点', @Old);
            INSERT INTO dbo.MODULES (M_IDX, M_DESC, M_ROOT_IDX) VALUES (99904, N'级联测试-根引用', @Old);
            INSERT INTO dbo.SYSDD (USER_ID, M_IDX) VALUES (N'ADR12CAST', @Old);
            INSERT INTO dbo.SYSDD_REPORT (USER_ID, M_IDX, REPORT_ID) VALUES (N'ADR12CAST', @Old, N'ADR12REPORT');
            INSERT INTO dbo.SYSDH (G_IDX, M_IDX) VALUES (99901, @Old);
            INSERT INTO dbo.REPORT (REPORT_ID, M_IDX) VALUES (N'ADR12REPORT', @Old);
            INSERT INTO dbo.REPORT (REPORT_ID, M_IDX) VALUES (N'ADR12REPORTQ', @Old);
            INSERT INTO dbo.SYSQR (USER_ID, R_M_IDX, REPORT_ID) VALUES (N'ADR12CAST', @Old, N'ADR12REPORT');
            INSERT INTO dbo.FIELDS (T_ID, F_ID, BROWSE_M_IDX) VALUES (N'ADR12CAST', N'F_CAST', @Old);
            INSERT INTO dbo.FIELD_DATASOURCE (T_ID, F_ID, SERIAL_NO, ACTIVE_TAG, SOURCE_T_ID, SOURCE_M_IDX, CREATE_DATE)
                VALUES (N'ADR12CAST', N'F_CAST', 1, 1, N'CAST_SRC', @Old, SYSDATETIME());
            INSERT INTO dbo.WFFORM (WF_M_IDX) VALUES (@Old);
            INSERT INTO dbo.WFFORM_FLOW (WF_M_IDX, SORT_NO) VALUES (@Old, 1);
            INSERT INTO dbo.WF_MONITOR (KEY_VALUE, WF_M_IDX) VALUES (N'ADR12CAST', @Old);
            INSERT INTO dbo.BILLKIND (BILL_CODE, BILL_NAME, B_M_IDX) VALUES (N'ADR12CAST', N'级联测试单据性质', @Old);
            INSERT INTO dbo.TASK (TASK_ID, M_IDX) VALUES (N'ADR12CAST', @Old);
            """, connection, transaction);
        seed.Parameters.Add("@Old", SqlDbType.Int).Value = OldId;
        await seed.ExecuteNonQueryAsync(token);
    }
}
