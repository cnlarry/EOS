using System.Data;
using EOS.API.Data;
using Microsoft.Data.SqlClient;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// BOM 成环检测（原 `P_BOM_CHECK`）已移植为受控 SQL 批的真库验证：
/// ① 逐用例把「原过程本体」与「移植实现」跑在同一批数据上，结论必须一致（含回报的产品号逐字相同，
///    包括原过程 `NVARCHAR(50)` 变量口径带来的尾部补空格）；
/// ② 原过程在"没有回到根"的环上会无限展开、把保存挂死，移植实现按 100 层上限 fail-closed——
///    该差异单独用一条用例钉住（只跑移植实现，不跑会挂死的原逻辑）。
/// 整段在事务内进行，结束回滚。
/// </summary>
[Collection("live-database")]
public sealed class BomCycleCheckLiveTests
{
    private static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("EOS_ERP_TEST_CONNECTION")
        ?? throw new InvalidOperationException(
            "真库集成测试需要 EOS_ERP_TEST_CONNECTION；未配置即失败（无连接跳过≠已验证）。");

    /// <summary>原 `P_BOM_CHECK` 过程本体（逐字保留，作为移植对照基准）。</summary>
    private const string LegacySql = """
        DECLARE @ok INT, @errCode NVARCHAR(50), @step INT
        SELECT @ok = 1, @step = 1
        SELECT @step StepNo, PRO_NO, ELEMENT_PRO_NO INTO #temp FROM BOM_STRU_D WHERE PRO_NO=@ProNo
        WHILE 1=1 BEGIN
            SELECT TOP 1 @errCode=PRO_NO FROM #temp WHERE ELEMENT_PRO_NO=@ProNo AND StepNo=@step
            IF ISNULL(@errCode, '') != '' BEGIN
                SELECT @ok = 0
                BREAK
            END
            ELSE BEGIN
                SELECT @step = @step + 1
                INSERT INTO #temp SELECT @step, b.PRO_NO, b.ELEMENT_PRO_NO FROM BOM_STRU_D b, #temp t
                    WHERE b.PRO_NO = t.ELEMENT_PRO_NO AND t.StepNo = @step - 1
                IF @@ROWCOUNT<=0 BREAK
            END
        END
        DROP TABLE #temp
        SELECT @ok AS Ok, @errCode AS ErrCode
        """;

    [Fact]
    public async Task BOM成环检测_移植实现与原过程本体的结论逐字一致()
    {
        var token = CancellationToken.None;
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
        try
        {
            await SeedAsync(connection, transaction, token);

            // 自环：A→A（第 1 层即命中）
            await AssertSameAsLegacyAsync(connection, transaction, "ADR12BOMA", "ADR12BOMA", token);
            // 两级环：A→B→A（第 2 层命中，回报 B）
            await AssertSameAsLegacyAsync(connection, transaction, "ADR12BOMC", "ADR12BOMD", token);
            // 三级环：A→B→C→A（第 3 层命中，回报 C）
            await AssertSameAsLegacyAsync(connection, transaction, "ADR12BOME", "ADR12BOMG", token);
            // 无环（末级没有元件）→ 两侧都判为无环
            await AssertSameAsLegacyAsync(connection, transaction, "ADR12BOMH", null, token);
            // 深链无环（12 层）→ 两侧都判为无环
            await AssertSameAsLegacyAsync(connection, transaction, "ADR12BOML1", null, token);

            // 回报值口径：原过程把定长 NCHAR(30) 的 PRO_NO 赋给 NVARCHAR(50) 变量 ⇒ 定长补空格原样保留
            var selfCycle = await SysDomainRules.FindBomCycleAsync(connection, transaction, "ADR12BOMA", token);
            Assert.NotNull(selfCycle);
            Assert.Equal(30, selfCycle!.Length);
            Assert.Equal("ADR12BOMA", selfCycle.Trim());
        }
        finally
        {
            await transaction.RollbackAsync(token);
        }
    }

    [Fact]
    public async Task BOM成环检测_没有回到根的环不再挂死而是判为循环()
    {
        var token = CancellationToken.None;
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
        try
        {
            await using (var seed = new SqlCommand("""
                INSERT INTO dbo.BOM_STRU_D (PRO_NO, SERIAL_NO, ELEMENT_PRO_NO) VALUES (N'ADR12BOMX', 1, N'ADR12BOMY');
                INSERT INTO dbo.BOM_STRU_D (PRO_NO, SERIAL_NO, ELEMENT_PRO_NO) VALUES (N'ADR12BOMY', 1, N'ADR12BOMZ');
                INSERT INTO dbo.BOM_STRU_D (PRO_NO, SERIAL_NO, ELEMENT_PRO_NO) VALUES (N'ADR12BOMZ', 1, N'ADR12BOMY');
                """, connection, transaction))
            {
                await seed.ExecuteNonQueryAsync(token);
            }
            // 原过程在这种数据上会无限展开（层数只增不减）⇒ 永远挂住保存；
            // 移植实现按 100 层上限判定为循环（fail-closed），不再挂死。
            var cycle = await SysDomainRules.FindBomCycleAsync(connection, transaction, "ADR12BOMX", token);
            Assert.NotNull(cycle);
        }
        finally
        {
            await transaction.RollbackAsync(token);
        }
    }

    /// <summary>同批数据下比对原过程本体与移植实现；期望命中的用例同时钉住回报值。</summary>
    private static async Task AssertSameAsLegacyAsync(
        SqlConnection connection, SqlTransaction transaction, string proNo, string? expectedErrCode, CancellationToken token)
    {
        var legacy = await LegacyAsync(connection, transaction, proNo, token);
        var ported = await SysDomainRules.FindBomCycleAsync(connection, transaction, proNo, token);
        Assert.Equal(legacy, ported);
        if (expectedErrCode is null)
        {
            Assert.Null(ported);
            return;
        }
        Assert.NotNull(ported);
        Assert.Equal(expectedErrCode, ported!.Trim());
    }

    private static async Task<string?> LegacyAsync(
        SqlConnection connection, SqlTransaction transaction, string proNo, CancellationToken token)
    {
        await using var command = new SqlCommand(LegacySql, connection, transaction);
        command.Parameters.Add("@ProNo", SqlDbType.NVarChar, 50).Value = proNo;
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token)) return null;
        var ok = reader.IsDBNull(0) ? 1 : Convert.ToInt32(reader.GetValue(0));
        if (ok == 1) return null;
        return reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
    }

    private static async Task SeedAsync(SqlConnection connection, SqlTransaction transaction, CancellationToken token)
    {
        await using var seed = new SqlCommand("""
            -- 自环
            INSERT INTO dbo.BOM_STRU_D (PRO_NO, SERIAL_NO, ELEMENT_PRO_NO) VALUES (N'ADR12BOMA', 1, N'ADR12BOMA');
            -- 两级环：C→D→C
            INSERT INTO dbo.BOM_STRU_D (PRO_NO, SERIAL_NO, ELEMENT_PRO_NO) VALUES (N'ADR12BOMC', 1, N'ADR12BOMD');
            INSERT INTO dbo.BOM_STRU_D (PRO_NO, SERIAL_NO, ELEMENT_PRO_NO) VALUES (N'ADR12BOMD', 1, N'ADR12BOMC');
            -- 三级环：E→F→G→E
            INSERT INTO dbo.BOM_STRU_D (PRO_NO, SERIAL_NO, ELEMENT_PRO_NO) VALUES (N'ADR12BOME', 1, N'ADR12BOMF');
            INSERT INTO dbo.BOM_STRU_D (PRO_NO, SERIAL_NO, ELEMENT_PRO_NO) VALUES (N'ADR12BOMF', 1, N'ADR12BOMG');
            INSERT INTO dbo.BOM_STRU_D (PRO_NO, SERIAL_NO, ELEMENT_PRO_NO) VALUES (N'ADR12BOMG', 1, N'ADR12BOME');
            -- 无环：H→I→J（J 无下级）
            INSERT INTO dbo.BOM_STRU_D (PRO_NO, SERIAL_NO, ELEMENT_PRO_NO) VALUES (N'ADR12BOMH', 1, N'ADR12BOMI');
            INSERT INTO dbo.BOM_STRU_D (PRO_NO, SERIAL_NO, ELEMENT_PRO_NO) VALUES (N'ADR12BOMI', 1, N'ADR12BOMJ');
            -- 深链无环：L1→L2→…→L12（末级无下级）
            INSERT INTO dbo.BOM_STRU_D (PRO_NO, SERIAL_NO, ELEMENT_PRO_NO) VALUES (N'ADR12BOML1', 1, N'ADR12BOML2');
            INSERT INTO dbo.BOM_STRU_D (PRO_NO, SERIAL_NO, ELEMENT_PRO_NO) VALUES (N'ADR12BOML2', 1, N'ADR12BOML3');
            INSERT INTO dbo.BOM_STRU_D (PRO_NO, SERIAL_NO, ELEMENT_PRO_NO) VALUES (N'ADR12BOML3', 1, N'ADR12BOML4');
            INSERT INTO dbo.BOM_STRU_D (PRO_NO, SERIAL_NO, ELEMENT_PRO_NO) VALUES (N'ADR12BOML4', 1, N'ADR12BOML5');
            INSERT INTO dbo.BOM_STRU_D (PRO_NO, SERIAL_NO, ELEMENT_PRO_NO) VALUES (N'ADR12BOML5', 1, N'ADR12BOML6');
            INSERT INTO dbo.BOM_STRU_D (PRO_NO, SERIAL_NO, ELEMENT_PRO_NO) VALUES (N'ADR12BOML6', 1, N'ADR12BOML7');
            INSERT INTO dbo.BOM_STRU_D (PRO_NO, SERIAL_NO, ELEMENT_PRO_NO) VALUES (N'ADR12BOML7', 1, N'ADR12BOML8');
            INSERT INTO dbo.BOM_STRU_D (PRO_NO, SERIAL_NO, ELEMENT_PRO_NO) VALUES (N'ADR12BOML8', 1, N'ADR12BOML9');
            INSERT INTO dbo.BOM_STRU_D (PRO_NO, SERIAL_NO, ELEMENT_PRO_NO) VALUES (N'ADR12BOML9', 1, N'ADR12BOML10');
            INSERT INTO dbo.BOM_STRU_D (PRO_NO, SERIAL_NO, ELEMENT_PRO_NO) VALUES (N'ADR12BOML10', 1, N'ADR12BOML11');
            INSERT INTO dbo.BOM_STRU_D (PRO_NO, SERIAL_NO, ELEMENT_PRO_NO) VALUES (N'ADR12BOML11', 1, N'ADR12BOML12');
            """, connection, transaction);
        await seed.ExecuteNonQueryAsync(token);
    }
}
