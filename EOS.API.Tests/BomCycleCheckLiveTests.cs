using System.Data;
using System.Text.Json;
using System.Text.RegularExpressions;
using EOS.API.Data.Effects;
using Microsoft.Data.SqlClient;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// BOM 成环检测入校验目录（新模板 `no-cycle`，来源旧过程 `P_BOM_CHECK`）后的真库验证：
/// ① 逐用例把「原过程本体」与「目录规则」跑在同一批数据上，结论必须一致（文案按空白归一后相同）；
/// ② 原过程在"没有回到根"的环上会无限展开、把保存挂死，目录规则按 `maxDepth`（100 层）fail-closed——
///    该差异单独用一条用例钉住（只跑目录规则，不跑会挂死的原逻辑）。
/// 整段在事务内进行，结束回滚。
/// </summary>
[Trait("Category", "Integration")]
[Collection("live-database")]
public sealed class BomCycleCheckLiveTests
{
    private static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("MSSQL_ERP_CONN")
        ?? throw new InvalidOperationException(
            "真库集成测试需要 MSSQL_ERP_CONN；未配置即失败（无连接跳过≠已验证）。");

    private static readonly EffectValidationExecutor Executor = new();

    /// <summary>原 `P_BOM_CHECK` 过程本体（逐字保留，作为目录规则的对照基准）。</summary>
    private const string BaselineSql = """
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
    public async Task BOM成环检测_目录规则与原过程本体的结论一致()
    {
        var token = CancellationToken.None;
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
        try
        {
            var plan = await LoadPlanAsync(connection, transaction, token);
            await SeedAsync(connection, transaction, token);

            // 自环：A→A（第 1 层即命中）
            await AssertSameAsBaselineAsync(connection, transaction, plan, "ADR12BOMA", "ADR12BOMA", token);
            // 两级环：C→D→C（第 2 层命中，回报 D）
            await AssertSameAsBaselineAsync(connection, transaction, plan, "ADR12BOMC", "ADR12BOMD", token);
            // 三级环：E→F→G→E（第 3 层命中，回报 G）
            await AssertSameAsBaselineAsync(connection, transaction, plan, "ADR12BOME", "ADR12BOMG", token);
            // 无环（末级没有元件）→ 两侧都放行
            await AssertSameAsBaselineAsync(connection, transaction, plan, "ADR12BOMH", null, token);
            // 深链无环（12 层）→ 两侧都放行
            await AssertSameAsBaselineAsync(connection, transaction, plan, "ADR12BOML1", null, token);
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
            var plan = await LoadPlanAsync(connection, transaction, token);
            await ExecuteAsync(connection, transaction, token, """
                INSERT INTO dbo.BOM_STRU_M (PRO_NO) VALUES (N'ADR12BOMX');
                INSERT INTO dbo.BOM_STRU_D (PRO_NO, EDITION, SERIAL_NO, ELEMENT_PRO_NO) VALUES (N'ADR12BOMX', N'01', 1, N'ADR12BOMY');
                INSERT INTO dbo.BOM_STRU_D (PRO_NO, EDITION, SERIAL_NO, ELEMENT_PRO_NO) VALUES (N'ADR12BOMY', N'01', 1, N'ADR12BOMZ');
                INSERT INTO dbo.BOM_STRU_D (PRO_NO, EDITION, SERIAL_NO, ELEMENT_PRO_NO) VALUES (N'ADR12BOMZ', N'01', 1, N'ADR12BOMY');
                """);
            // 原过程在这种数据上会无限展开（层数只增不减）⇒ 永远挂住保存；
            // 目录规则按 maxDepth=100 判定为循环（fail-closed），不再挂死。
            var error = await Assert.ThrowsAsync<EffectValidationException>(() =>
                Executor.ValidateAsync(connection, transaction, plan, "SAVE", token, ["ADR12BOMX"]));
            Assert.StartsWith("以下元件在BOM结构中循环使用", Normalize(error.Message));
        }
        finally
        {
            await transaction.RollbackAsync(token);
        }
    }

    /// <summary>同批数据下比对原过程本体与目录规则；命中的用例同时钉住文案。</summary>
    private static async Task AssertSameAsBaselineAsync(
        SqlConnection connection, SqlTransaction transaction, ModuleEffectPlan plan,
        string proNo, string? expectedErrCode, CancellationToken token)
    {
        var baseline = await BaselineAsync(connection, transaction, proNo, token);
        if (baseline is null)
        {
            await Executor.ValidateAsync(connection, transaction, plan, "SAVE", token, [proNo]);
            Assert.Null(expectedErrCode);
            return;
        }
        var error = await Assert.ThrowsAsync<EffectValidationException>(() =>
            Executor.ValidateAsync(connection, transaction, plan, "SAVE", token, [proNo]));
        // 旧过程文案 = 表头 + char(13) + 变量值（定长补空格）；目录返回表头 + 命中值（已去空格）
        Assert.Equal(Normalize("以下元件在BOM结构中循环使用 \r" + baseline), Normalize(error.Message));
        Assert.NotNull(expectedErrCode);
        Assert.Contains(expectedErrCode!, error.Message);
    }

    private static string Normalize(string value) => Regex.Replace(value, @"\s+", " ").Trim();

    private static async Task<string?> BaselineAsync(
        SqlConnection connection, SqlTransaction transaction, string proNo, CancellationToken token)
    {
        await using var command = new SqlCommand(BaselineSql, connection, transaction);
        command.Parameters.Add("@ProNo", SqlDbType.NVarChar, 50).Value = proNo;
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token)) return null;
        var ok = reader.IsDBNull(0) ? 1 : Convert.ToInt32(reader.GetValue(0));
        if (ok == 1) return null;
        return reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
    }

    private static async Task<ModuleEffectPlan> LoadPlanAsync(
        SqlConnection connection, SqlTransaction transaction, CancellationToken token)
    {
        string masterTable, detailTable, paramStruct;
        await using (var command = new SqlCommand("""
            SELECT LTRIM(RTRIM(m.MASTER_TABLE)), LTRIM(RTRIM(ISNULL(m.DETAIL_TABLE, N''))), r.PARAM_STRUCT
            FROM dbo.MODULES m
            JOIN dbo.MODULE_VALIDATION_RULE r ON r.M_IDX = m.M_IDX AND r.STAGE = N'SAVE'
                 AND r.VALIDATION_KEY = N'no-cycle'
            WHERE m.M_IDX = 1204;
            """, connection, transaction))
        {
            await using var reader = await command.ExecuteReaderAsync(token);
            Assert.True(await reader.ReadAsync(token), "模块 1204 缺少 SAVE 期 no-cycle 配置");
            masterTable = reader.GetString(0);
            detailTable = reader.GetString(1);
            paramStruct = reader.GetString(2);
        }
        // 主键序从**物理主键约束**推导，不读发布快照：1204 是定制页，不参与发布流程
        // （迁移 415 已清掉历史快照），运行期按 MODULES/FIELDS 实时装配。
        var pkOrder = new List<string>();
        await using (var command = new SqlCommand("""
            SELECT c.name
            FROM sys.key_constraints kc
            JOIN sys.index_columns ic ON ic.object_id = kc.parent_object_id AND ic.index_id = kc.unique_index_id
            JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE kc.parent_object_id = OBJECT_ID(@QualifiedTable) AND kc.type = 'PK'
            ORDER BY ic.key_ordinal;
            """, connection, transaction))
        {
            command.Parameters.Add("@QualifiedTable", SqlDbType.NVarChar, 200).Value = "dbo." + masterTable;
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token)) pkOrder.Add(reader.GetString(0));
        }
        Assert.NotEmpty(pkOrder);
        using var parameters = JsonDocument.Parse(paramStruct);
        return new ModuleEffectPlan(1204, masterTable, detailTable, "live-bom-cycle", pkOrder,
            Array.Empty<EffectActionPlan>(),
            [new EffectValidationPlan(1, "SAVE", "no-cycle", true, parameters.RootElement.Clone(), null)]);
    }

    private static async Task SeedAsync(SqlConnection connection, SqlTransaction transaction, CancellationToken token)
        => await ExecuteAsync(connection, transaction, token, """
            -- 起点产品需要主表行（目录规则从当前单据主表行取起点值）
            INSERT INTO dbo.BOM_STRU_M (PRO_NO) VALUES (N'ADR12BOMA'), (N'ADR12BOMC'), (N'ADR12BOME'),
                                                      (N'ADR12BOMH'), (N'ADR12BOML1');
            -- 自环
            INSERT INTO dbo.BOM_STRU_D (PRO_NO, EDITION, SERIAL_NO, ELEMENT_PRO_NO) VALUES (N'ADR12BOMA', N'01', 1, N'ADR12BOMA');
            -- 两级环：C→D→C
            INSERT INTO dbo.BOM_STRU_D (PRO_NO, EDITION, SERIAL_NO, ELEMENT_PRO_NO) VALUES (N'ADR12BOMC', N'01', 1, N'ADR12BOMD');
            INSERT INTO dbo.BOM_STRU_D (PRO_NO, EDITION, SERIAL_NO, ELEMENT_PRO_NO) VALUES (N'ADR12BOMD', N'01', 1, N'ADR12BOMC');
            -- 三级环：E→F→G→E
            INSERT INTO dbo.BOM_STRU_D (PRO_NO, EDITION, SERIAL_NO, ELEMENT_PRO_NO) VALUES (N'ADR12BOME', N'01', 1, N'ADR12BOMF');
            INSERT INTO dbo.BOM_STRU_D (PRO_NO, EDITION, SERIAL_NO, ELEMENT_PRO_NO) VALUES (N'ADR12BOMF', N'01', 1, N'ADR12BOMG');
            INSERT INTO dbo.BOM_STRU_D (PRO_NO, EDITION, SERIAL_NO, ELEMENT_PRO_NO) VALUES (N'ADR12BOMG', N'01', 1, N'ADR12BOME');
            -- 无环：H→I→J（J 无下级）
            INSERT INTO dbo.BOM_STRU_D (PRO_NO, EDITION, SERIAL_NO, ELEMENT_PRO_NO) VALUES (N'ADR12BOMH', N'01', 1, N'ADR12BOMI');
            INSERT INTO dbo.BOM_STRU_D (PRO_NO, EDITION, SERIAL_NO, ELEMENT_PRO_NO) VALUES (N'ADR12BOMI', N'01', 1, N'ADR12BOMJ');
            -- 深链无环：L1→L2→…→L12（末级无下级）
            INSERT INTO dbo.BOM_STRU_D (PRO_NO, EDITION, SERIAL_NO, ELEMENT_PRO_NO) VALUES (N'ADR12BOML1', N'01', 1, N'ADR12BOML2');
            INSERT INTO dbo.BOM_STRU_D (PRO_NO, EDITION, SERIAL_NO, ELEMENT_PRO_NO) VALUES (N'ADR12BOML2', N'01', 1, N'ADR12BOML3');
            INSERT INTO dbo.BOM_STRU_D (PRO_NO, EDITION, SERIAL_NO, ELEMENT_PRO_NO) VALUES (N'ADR12BOML3', N'01', 1, N'ADR12BOML4');
            INSERT INTO dbo.BOM_STRU_D (PRO_NO, EDITION, SERIAL_NO, ELEMENT_PRO_NO) VALUES (N'ADR12BOML4', N'01', 1, N'ADR12BOML5');
            INSERT INTO dbo.BOM_STRU_D (PRO_NO, EDITION, SERIAL_NO, ELEMENT_PRO_NO) VALUES (N'ADR12BOML5', N'01', 1, N'ADR12BOML6');
            INSERT INTO dbo.BOM_STRU_D (PRO_NO, EDITION, SERIAL_NO, ELEMENT_PRO_NO) VALUES (N'ADR12BOML6', N'01', 1, N'ADR12BOML7');
            INSERT INTO dbo.BOM_STRU_D (PRO_NO, EDITION, SERIAL_NO, ELEMENT_PRO_NO) VALUES (N'ADR12BOML7', N'01', 1, N'ADR12BOML8');
            INSERT INTO dbo.BOM_STRU_D (PRO_NO, EDITION, SERIAL_NO, ELEMENT_PRO_NO) VALUES (N'ADR12BOML8', N'01', 1, N'ADR12BOML9');
            INSERT INTO dbo.BOM_STRU_D (PRO_NO, EDITION, SERIAL_NO, ELEMENT_PRO_NO) VALUES (N'ADR12BOML9', N'01', 1, N'ADR12BOML10');
            INSERT INTO dbo.BOM_STRU_D (PRO_NO, EDITION, SERIAL_NO, ELEMENT_PRO_NO) VALUES (N'ADR12BOML10', N'01', 1, N'ADR12BOML11');
            INSERT INTO dbo.BOM_STRU_D (PRO_NO, EDITION, SERIAL_NO, ELEMENT_PRO_NO) VALUES (N'ADR12BOML11', N'01', 1, N'ADR12BOML12');
            """);

    private static async Task ExecuteAsync(
        SqlConnection connection, SqlTransaction transaction, CancellationToken token, string sql)
    {
        await using var command = new SqlCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync(token);
    }
}
