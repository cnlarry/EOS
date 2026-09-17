using System.Data;
using System.Text.Json;
using EOS.API.Data.Effects;
using Microsoft.Data.SqlClient;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 校验目录的真库集成测试：直接以真实连接驱动 <see cref="EffectValidationExecutor"/>，
/// 用事务内临时数据验证「拒绝且文案完整（占位符已替换）」「主从跨单唯一命中/放行」
/// 「规则级适用条件跳过空键」三条路径。整段在事务里造数并回滚，不留残留。
///
/// 需要 EOS_ERP_TEST_CONNECTION（与本仓库其它真库测试一致的约定）。
/// </summary>
[Collection("live-database")]
public sealed class EffectValidationLiveDataTests
{
    private static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("EOS_ERP_TEST_CONNECTION")
        ?? throw new InvalidOperationException(
            "真库集成测试需要 EOS_ERP_TEST_CONNECTION；未配置即失败（无连接跳过≠已验证）。");

    private static readonly EffectValidationExecutor Executor = new();

    private static async Task<ModuleEffectPlan> LoadPlanAsync(
        SqlConnection connection, SqlTransaction transaction, int moduleId, CancellationToken token,
        string validationKey = "duplicate-check", bool? enabledOverride = null)
    {
        string masterTable, detailTable = null!, pkJson, paramStruct, message;
        bool enabled;
        await using (var command = new SqlCommand("""
            SELECT m.MASTER_TABLE, m.DETAIL_TABLE, s.[DEFINITION_JSON], r.PARAM_STRUCT, r.MESSAGE, r.ENABLED
            FROM dbo.MODULES m
            JOIN dbo.WORKBENCH_DEFINITION_SNAPSHOT s ON s.MODULE_ID = m.M_IDX AND s.IS_CURRENT = 1
            JOIN dbo.MODULE_VALIDATION_RULE r ON r.MODULE_ID = m.M_IDX AND r.STAGE = N'SAVE' AND r.VALIDATION_KEY = @Key
            WHERE m.M_IDX = @ModuleId;
            """, connection, transaction))
        {
            command.Parameters.Add("@ModuleId", SqlDbType.Int).Value = moduleId;
            command.Parameters.Add("@Key", SqlDbType.NVarChar, 50).Value = validationKey;
            await using var reader = await command.ExecuteReaderAsync(token);
            Assert.True(await reader.ReadAsync(token), $"模块 {moduleId} 缺少当前快照或 SAVE 期 duplicate-check 配置");
            masterTable = reader.GetString(0);
            // 纯主表模块的 DETAIL_TABLE 存空串；定义装配按"无明细表"归一，测试同样处理。
            detailTable = reader.IsDBNull(1) || string.IsNullOrWhiteSpace(reader.GetString(1))
                ? null!
                : reader.GetString(1);
            // 主键序列取自当前快照定义，与运行时装配一致
            using var definition = JsonDocument.Parse(reader.GetString(2));
            pkJson = definition.RootElement.GetProperty("MasterPkOrder").GetRawText();
            paramStruct = reader.GetString(3);
            message = reader.IsDBNull(4) ? null! : reader.GetString(4);
            enabled = enabledOverride ?? reader.GetBoolean(5);
        }
        var pkOrder = JsonSerializer.Deserialize<List<string>>(pkJson)!;
        using var parameters = JsonDocument.Parse(paramStruct);
        return new ModuleEffectPlan(
            moduleId, masterTable, detailTable, $"live-test-{moduleId}", pkOrder,
            Array.Empty<EffectActionPlan>(),
            [new EffectValidationPlan(1, "SAVE", validationKey, enabled, parameters.RootElement.Clone(), message)]);
    }

    [Fact]
    public async Task 本位币唯一_拒绝并回填诊断占位符()
    {
        var token = CancellationToken.None;
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
        try
        {
            await using (var seed = new SqlCommand(
                "INSERT INTO dbo.CURR (CURR_ID, CURR_NAME, IS_BASE, CURR_RATE, CI) VALUES (N'ADR12ZZ', N'集成测试币别', 1, 1, 'E2E');",
                connection, transaction))
            {
                await seed.ExecuteNonQueryAsync(token);
            }

            var plan = await LoadPlanAsync(connection, transaction, 110103, token);
            var exception = await Assert.ThrowsAsync<EffectValidationException>(() =>
                Executor.ValidateAsync(connection, transaction, plan, "SAVE", token, ["ADR12ZZ"]));

            Assert.Contains("RMB", exception.Message);
            Assert.DoesNotContain("{CURR_ID}", exception.Message);
        }
        finally
        {
            await transaction.RollbackAsync(token);
        }
    }

    [Fact]
    public async Task 本位币唯一_非本位币放行()
    {
        var token = CancellationToken.None;
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
        try
        {
            await using (var seed = new SqlCommand(
                "INSERT INTO dbo.CURR (CURR_ID, CURR_NAME, IS_BASE, CURR_RATE, CI) VALUES (N'ADR12ZY', N'集成测试币别', 0, 7.2, 'E2E');",
                connection, transaction))
            {
                await seed.ExecuteNonQueryAsync(token);
            }

            var plan = await LoadPlanAsync(connection, transaction, 110103, token);
            await Executor.ValidateAsync(connection, transaction, plan, "SAVE", token, ["ADR12ZY"]);
        }
        finally
        {
            await transaction.RollbackAsync(token);
        }
    }

    [Fact]
    public async Task 排班主从跨单唯一_同月同员工命中并回报序号()
    {
        var token = CancellationToken.None;
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
        try
        {
            await using (var seed = new SqlCommand("""
                INSERT INTO dbo.HR_PLAN_M (PLAN_TYPE, PLAN_NO, COUNT_MONTH) VALUES (N'ADR12', N'P1', N'202609');
                INSERT INTO dbo.HR_PLAN_M (PLAN_TYPE, PLAN_NO, COUNT_MONTH) VALUES (N'ADR12', N'P2', N'202609');
                INSERT INTO dbo.HR_PLAN_D (PLAN_TYPE, PLAN_NO, SERIAL_NO, EMP_ID) VALUES (N'ADR12', N'P1', 1, N'E1');
                INSERT INTO dbo.HR_PLAN_D (PLAN_TYPE, PLAN_NO, SERIAL_NO, EMP_ID) VALUES (N'ADR12', N'P2', 2, N'E1');
                """, connection, transaction))
            {
                await seed.ExecuteNonQueryAsync(token);
            }

            var plan = await LoadPlanAsync(connection, transaction, 180211, token);
            var exception = await Assert.ThrowsAsync<EffectValidationException>(() =>
                Executor.ValidateAsync(connection, transaction, plan, "SAVE", token, ["ADR12", "P2"]));

            Assert.Contains("以下序号项人员当月排班重复", exception.Message);
            Assert.Contains("2", exception.Message);
            Assert.DoesNotContain("{ROWS}", exception.Message);
        }
        finally
        {
            await transaction.RollbackAsync(token);
        }
    }

    [Fact]
    public async Task 排班主从跨单唯一_不同员工放行()
    {
        var token = CancellationToken.None;
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
        try
        {
            await using (var seed = new SqlCommand("""
                INSERT INTO dbo.HR_PLAN_M (PLAN_TYPE, PLAN_NO, COUNT_MONTH) VALUES (N'ADR12', N'P1', N'202609');
                INSERT INTO dbo.HR_PLAN_M (PLAN_TYPE, PLAN_NO, COUNT_MONTH) VALUES (N'ADR12', N'P2', N'202609');
                INSERT INTO dbo.HR_PLAN_D (PLAN_TYPE, PLAN_NO, SERIAL_NO, EMP_ID) VALUES (N'ADR12', N'P1', 1, N'E1');
                INSERT INTO dbo.HR_PLAN_D (PLAN_TYPE, PLAN_NO, SERIAL_NO, EMP_ID) VALUES (N'ADR12', N'P2', 2, N'E9');
                """, connection, transaction))
            {
                await seed.ExecuteNonQueryAsync(token);
            }

            var plan = await LoadPlanAsync(connection, transaction, 180211, token);
            await Executor.ValidateAsync(connection, transaction, plan, "SAVE", token, ["ADR12", "P2"]);
        }
        finally
        {
            await transaction.RollbackAsync(token);
        }
    }

    [Fact]
    public async Task 员工工号唯一_空白工号按适用条件跳过()
    {
        var token = CancellationToken.None;
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
        try
        {
            // 两行同为空白的工号：若无 when 适用条件，键相等且非自身即会误拒。
            await using (var seed = new SqlCommand("""
                INSERT INTO dbo.HR_EMPLOYEE (EMP_ID, EMP_NO, EMP_NAME, STATE) VALUES (N'ADR12E1', N'   ', N'集成测试甲', 1);
                INSERT INTO dbo.HR_EMPLOYEE (EMP_ID, EMP_NO, EMP_NAME, STATE) VALUES (N'ADR12E2', N'   ', N'集成测试乙', 1);
                """, connection, transaction))
            {
                await seed.ExecuteNonQueryAsync(token);
            }

            var plan = await LoadPlanAsync(connection, transaction, 180102, token);
            await Executor.ValidateAsync(connection, transaction, plan, "SAVE", token, ["ADR12E2"]);
        }
        finally
        {
            await transaction.RollbackAsync(token);
        }
    }

    [Fact]
    public async Task 员工工号唯一_重复工号拒绝()
    {
        var token = CancellationToken.None;
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
        try
        {
            await using (var seed = new SqlCommand("""
                INSERT INTO dbo.HR_EMPLOYEE (EMP_ID, EMP_NO, EMP_NAME, STATE) VALUES (N'ADR12E3', N'ADR12NO', N'集成测试丙', 1);
                INSERT INTO dbo.HR_EMPLOYEE (EMP_ID, EMP_NO, EMP_NAME, STATE) VALUES (N'ADR12E4', N'ADR12NO', N'集成测试丁', 1);
                """, connection, transaction))
            {
                await seed.ExecuteNonQueryAsync(token);
            }

            var plan = await LoadPlanAsync(connection, transaction, 180102, token);
            var exception = await Assert.ThrowsAsync<EffectValidationException>(() =>
                Executor.ValidateAsync(connection, transaction, plan, "SAVE", token, ["ADR12E4"]));

            Assert.Contains("ADR12NO", exception.Message);
            Assert.Contains("已分配给", exception.Message);
            Assert.DoesNotContain("{EMP_NAME}", exception.Message);
        }
        finally
        {
            await transaction.RollbackAsync(token);
        }
    }

    // ---------- 其余主从跨单唯一族：同一形状、逐族真实表列 ----------

    private static async Task AssertRejectsAsync(
        int moduleId, string seedSql, string[] keyValues, string expectedFragment)
    {
        var token = CancellationToken.None;
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
        try
        {
            await using (var seed = new SqlCommand(seedSql, connection, transaction))
            {
                await seed.ExecuteNonQueryAsync(token);
            }

            var plan = await LoadPlanAsync(connection, transaction, moduleId, token);
            var exception = await Assert.ThrowsAsync<EffectValidationException>(() =>
                Executor.ValidateAsync(connection, transaction, plan, "SAVE", token, keyValues));

            Assert.Contains(expectedFragment, exception.Message);
            Assert.DoesNotContain("{ROWS}", exception.Message);
        }
        finally
        {
            await transaction.RollbackAsync(token);
        }
    }

    private static async Task AssertPassesAsync(int moduleId, string seedSql, string[] keyValues)
    {
        var token = CancellationToken.None;
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
        try
        {
            await using (var seed = new SqlCommand(seedSql, connection, transaction))
            {
                await seed.ExecuteNonQueryAsync(token);
            }

            var plan = await LoadPlanAsync(connection, transaction, moduleId, token);
            await Executor.ValidateAsync(connection, transaction, plan, "SAVE", token, keyValues);
        }
        finally
        {
            await transaction.RollbackAsync(token);
        }
    }

    [Fact]
    public async Task 每月出勤参数_同月同员工拒绝_固定文案()
        => await AssertRejectsAsync(180205, """
            INSERT INTO dbo.HR_ENACTMENT_M (ENACTMENT_TYPE, ENACTMENT_NO, COUNT_MONTH) VALUES (N'ADR12', N'K1', N'190001');
            INSERT INTO dbo.HR_ENACTMENT_M (ENACTMENT_TYPE, ENACTMENT_NO, COUNT_MONTH) VALUES (N'ADR12', N'K2', N'190001');
            INSERT INTO dbo.HR_ENACTMENT_D (ENACTMENT_TYPE, ENACTMENT_NO, SERIAL_NO, EMP_ID) VALUES (N'ADR12', N'K1', 1, N'E1');
            INSERT INTO dbo.HR_ENACTMENT_D (ENACTMENT_TYPE, ENACTMENT_NO, SERIAL_NO, EMP_ID) VALUES (N'ADR12', N'K2', 2, N'E1');
            """, ["ADR12", "K2"], "资料重复!每个员工每个月份只可有一笔资料");

    [Fact]
    public async Task 每月出勤参数_不同员工放行()
        => await AssertPassesAsync(180205, """
            INSERT INTO dbo.HR_ENACTMENT_M (ENACTMENT_TYPE, ENACTMENT_NO, COUNT_MONTH) VALUES (N'ADR12', N'K1', N'190001');
            INSERT INTO dbo.HR_ENACTMENT_M (ENACTMENT_TYPE, ENACTMENT_NO, COUNT_MONTH) VALUES (N'ADR12', N'K2', N'190001');
            INSERT INTO dbo.HR_ENACTMENT_D (ENACTMENT_TYPE, ENACTMENT_NO, SERIAL_NO, EMP_ID) VALUES (N'ADR12', N'K1', 1, N'E1');
            INSERT INTO dbo.HR_ENACTMENT_D (ENACTMENT_TYPE, ENACTMENT_NO, SERIAL_NO, EMP_ID) VALUES (N'ADR12', N'K2', 2, N'E9');
            """, ["ADR12", "K2"]);

    [Fact]
    public async Task 加班申请_当日同员工拒绝并按日期判空()
        => await AssertRejectsAsync(180206, """
            INSERT INTO dbo.HR_APPLY_M (APPLY_TYPE, APPLY_NO, COUNT_DATE) VALUES (N'ADR12', N'K1', '1900-01-01');
            INSERT INTO dbo.HR_APPLY_M (APPLY_TYPE, APPLY_NO, COUNT_DATE) VALUES (N'ADR12', N'K2', '1900-01-01');
            INSERT INTO dbo.HR_APPLY_D (APPLY_TYPE, APPLY_NO, SERIAL_NO, EMP_ID) VALUES (N'ADR12', N'K1', 1, N'E1');
            INSERT INTO dbo.HR_APPLY_D (APPLY_TYPE, APPLY_NO, SERIAL_NO, EMP_ID) VALUES (N'ADR12', N'K2', 2, N'E1');
            """, ["ADR12", "K2"], "以下人员当日加班申请重复");

    [Fact]
    public async Task 加班申请_日期为空时按适用条件跳过()
        => await AssertPassesAsync(180206, """
            INSERT INTO dbo.HR_APPLY_M (APPLY_TYPE, APPLY_NO, COUNT_DATE) VALUES (N'ADR12', N'K1', NULL);
            INSERT INTO dbo.HR_APPLY_M (APPLY_TYPE, APPLY_NO, COUNT_DATE) VALUES (N'ADR12', N'K2', NULL);
            INSERT INTO dbo.HR_APPLY_D (APPLY_TYPE, APPLY_NO, SERIAL_NO, EMP_ID) VALUES (N'ADR12', N'K1', 1, N'E1');
            INSERT INTO dbo.HR_APPLY_D (APPLY_TYPE, APPLY_NO, SERIAL_NO, EMP_ID) VALUES (N'ADR12', N'K2', 2, N'E1');
            """, ["ADR12", "K2"]);

    [Fact]
    public async Task 工资表_同月同员工拒绝_整月扫描()
        => await AssertRejectsAsync(180309, """
            INSERT INTO dbo.HR_WAGE_M (WAGE_TYPE, WAGE_NO, COUNT_MONTH) VALUES (N'ADR12', N'K1', N'190001');
            INSERT INTO dbo.HR_WAGE_M (WAGE_TYPE, WAGE_NO, COUNT_MONTH) VALUES (N'ADR12', N'K2', N'190001');
            INSERT INTO dbo.HR_WAGE_D (WAGE_TYPE, WAGE_NO, SERIAL_NO, EMP_ID) VALUES (N'ADR12', N'K1', 1, N'E1');
            INSERT INTO dbo.HR_WAGE_D (WAGE_TYPE, WAGE_NO, SERIAL_NO, EMP_ID) VALUES (N'ADR12', N'K2', 2, N'E1');
            """, ["ADR12", "K2"], "以下人员当月工资表重复");

    [Fact]
    public async Task HRM排班_同月同员工拒绝()
        => await AssertRejectsAsync(180651, """
            INSERT INTO dbo.HRM_PLAN_M (PLAN_TYPE, PLAN_NO, COUNT_MONTH) VALUES (N'ADR12', N'K1', N'190001');
            INSERT INTO dbo.HRM_PLAN_M (PLAN_TYPE, PLAN_NO, COUNT_MONTH) VALUES (N'ADR12', N'K2', N'190001');
            INSERT INTO dbo.HRM_PLAN_D (PLAN_TYPE, PLAN_NO, SERIAL_NO, EMP_ID) VALUES (N'ADR12', N'K1', 1, N'E1');
            INSERT INTO dbo.HRM_PLAN_D (PLAN_TYPE, PLAN_NO, SERIAL_NO, EMP_ID) VALUES (N'ADR12', N'K2', 2, N'E1');
            """, ["ADR12", "K2"], "以下序号项人员当月排班重复");

    [Fact]
    public async Task HRM工资表_同月同员工拒绝()
        => await AssertRejectsAsync(180504, """
            INSERT INTO dbo.HRM_WAGE_M (WAGE_TYPE, WAGE_NO, COUNT_MONTH) VALUES (N'ADR12', N'K1', N'190001');
            INSERT INTO dbo.HRM_WAGE_M (WAGE_TYPE, WAGE_NO, COUNT_MONTH) VALUES (N'ADR12', N'K2', N'190001');
            INSERT INTO dbo.HRM_WAGE_D (WAGE_TYPE, WAGE_NO, SERIAL_NO, EMP_ID) VALUES (N'ADR12', N'K1', 1, N'E1');
            INSERT INTO dbo.HRM_WAGE_D (WAGE_TYPE, WAGE_NO, SERIAL_NO, EMP_ID) VALUES (N'ADR12', N'K2', 2, N'E1');
            """, ["ADR12", "K2"], "以下人员当月工资表重复");

    // ---------- 1606 引用存在性：按 C# 判据重建后只保留"厂商存在且未停止交易" ----------

    [Fact]
    public async Task 采购单引用校验_现存单据放行()
    {
        var token = CancellationToken.None;
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
        try
        {
            string type, no;
            await using (var pick = new SqlCommand(
                "SELECT TOP 1 PURCHASE_TYPE, PURCHASE_NO FROM dbo.PUR_PURCHASE_M ORDER BY PURCHASE_DATE DESC;",
                connection, transaction))
            {
                await using var reader = await pick.ExecuteReaderAsync(token);
                Assert.True(await reader.ReadAsync(token), "库内没有采购单样本");
                type = reader.GetString(0).Trim();
                no = reader.GetString(1).Trim();
            }

            var plan = await LoadPlanAsync(connection, transaction, 1606, token, "reference-exists");
            Assert.True(plan.Rules[0].Enabled, "1606 SAVE 引用校验应处于启用状态");
            await Executor.ValidateAsync(connection, transaction, plan, "SAVE", token, [type, no]);
        }
        finally
        {
            await transaction.RollbackAsync(token);
        }
    }

    [Fact]
    public async Task 采购单引用校验_只保留厂商存在性断言()
    {
        var token = CancellationToken.None;
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
        try
        {
            var plan = await LoadPlanAsync(connection, transaction, 1606, token, "reference-exists");
            var checks = plan.Rules[0].Params.GetProperty("checks");
            // 曾经存在的"明细申购单存在/明细产品存在"两条断言与真实数据不符（6746/8729 采购单被误拦），
            // 按 C# 判据重建后只允许保留厂商断言；重新塞回臆造断言时本用例会失败。
            Assert.Equal(1, checks.GetArrayLength());
            Assert.Equal("SUPPLIER", checks[0].GetProperty("refTable").GetString());
        }
        finally
        {
            await transaction.RollbackAsync(token);
        }
    }

    [Fact]
    public async Task 采购单引用校验_厂商不存在或已停止交易时拒绝()
    {
        var token = CancellationToken.None;
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
        try
        {
            await using (var seed = new SqlCommand("""
                INSERT INTO dbo.PUR_PURCHASE_M (PURCHASE_TYPE, PURCHASE_NO, SUPPLIER_ID, PURCHASE_DATE)
                VALUES (N'ADR12', N'K9', N'ZZNONE', SYSDATETIME());
                """, connection, transaction))
            {
                await seed.ExecuteNonQueryAsync(token);
            }

            var plan = await LoadPlanAsync(connection, transaction, 1606, token, "reference-exists");
            var exception = await Assert.ThrowsAsync<EffectValidationException>(() =>
                Executor.ValidateAsync(connection, transaction, plan, "SAVE", token, ["ADR12", "K9"]));

            Assert.Contains("厂商编号不存在或已停止交易", exception.Message);
        }
        finally
        {
            await transaction.RollbackAsync(token);
        }
    }

    /// <summary>
    /// line-require 的跨表条件触发器（condition）：判据"产品为批管则批号必填"需要
    /// PRODUCT.MANAGE_BATCH 这类**跨表存在性**条件，旧的 triggers 只支持明细列与常量比较，
    /// 表达不了。本用例验证 condition 触发器命中、诊断序号回填，以及补上批号后放行。
    /// </summary>
    [Fact]
    public async Task 批管品必填批号_跨表条件触发器命中并回报序号()
    {
        var token = CancellationToken.None;
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
        try
        {
            await using (var seed = new SqlCommand("""
                INSERT INTO dbo.PRODUCT (PRO_NO, PRO_NAME, MANAGE_BATCH) VALUES (N'ADR12BATCHP', N'集成测试批管品', 1);
                INSERT INTO dbo.INV_OCCUR_IN_D (OCCUR_TYPE, OCCUR_NO, SERIAL_NO, PRO_NO, BATCH_NO)
                    VALUES (N'ADR12', N'ADR12LINEREQ1', 1, N'ADR12BATCHP', N'');
                """, connection, transaction))
            {
                await seed.ExecuteNonQueryAsync(token);
            }

            var plan = await LoadPlanAsync(connection, transaction, 130103, token, "line-require");
            var exception = await Assert.ThrowsAsync<EffectValidationException>(() =>
                Executor.ValidateAsync(connection, transaction, plan, "SAVE", token, ["ADR12", "ADR12LINEREQ1"]));
            Assert.Contains("以下序号项需要输入批号", exception.Message);
            Assert.Contains("1", exception.Message);

            // 同一单据、非批管品 → 放行（条件不成立）
            await using (var swap = new SqlCommand("""
                INSERT INTO dbo.PRODUCT (PRO_NO, PRO_NAME, MANAGE_BATCH) VALUES (N'ADR12PLAINP', N'集成测试非批管品', 0);
                UPDATE dbo.INV_OCCUR_IN_D SET PRO_NO = N'ADR12PLAINP'
                    WHERE OCCUR_TYPE = N'ADR12' AND OCCUR_NO = N'ADR12LINEREQ1' AND SERIAL_NO = 1;
                """, connection, transaction))
            {
                await swap.ExecuteNonQueryAsync(token);
            }
            await Executor.ValidateAsync(connection, transaction, plan, "SAVE", token, ["ADR12", "ADR12LINEREQ1"]);

            // 批管品补上批号 → 放行
            await using (var fix = new SqlCommand("""
                UPDATE dbo.INV_OCCUR_IN_D SET PRO_NO = N'ADR12BATCHP', BATCH_NO = N'B001'
                    WHERE OCCUR_TYPE = N'ADR12' AND OCCUR_NO = N'ADR12LINEREQ1' AND SERIAL_NO = 1;
                """, connection, transaction))
            {
                await fix.ExecuteNonQueryAsync(token);
            }
            await Executor.ValidateAsync(connection, transaction, plan, "SAVE", token, ["ADR12", "ADR12LINEREQ1"]);
        }
        finally
        {
            await transaction.RollbackAsync(token);
        }
    }

    [Fact]
    public async Task 量产模入库_受门控的入库不超完工未入_分组求和与源列聚合诊断()
    {
        var token = CancellationToken.None;
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
        try
        {
            // 同一批次两行入库共 4，模具数量 3、完工未入 0 ⇒ 合计超量；
            // 诊断应回报该分组的 MAX(SERIAL_NO)=2（源列聚合），而不是分组键。
            await using (var seed = new SqlCommand("""
                INSERT INTO dbo.MOU_BATCH_M (BATCH_TYPE, BATCH_NO, QTY, FINISHED_QTY)
                VALUES (N'ZZ', N'ZZT2609BATCH01', 3, 0);
                INSERT INTO dbo.MOU_BATCHIN_D (BATCHIN_TYPE, BATCHIN_NO, SERIAL_NO, BATCH_TYPE, BATCH_NO, QTY)
                VALUES (N'ZZ', N'ZZT2609BI01', 1, N'ZZ', N'ZZT2609BATCH01', 2),
                       (N'ZZ', N'ZZT2609BI01', 2, N'ZZ', N'ZZT2609BATCH01', 2);
                """, connection, transaction))
            {
                await seed.ExecuteNonQueryAsync(token);
            }

            var plan = await LoadPlanAsync(connection, transaction, 2912, token, "qty-not-exceed");

            // 门控关（库内 2912 的 ERROR_NO_SAVE=0）→ 跳过，超量也放行
            await Executor.ValidateAsync(connection, transaction, plan, "SAVE", token, ["ZZ", "ZZT2609BI01"]);

            // 门控开 → 命中：文案逐字 + 分组内最大序号（源列聚合诊断）
            await using (var open = new SqlCommand(
                "UPDATE dbo.MODULES SET ERROR_NO_SAVE=1 WHERE M_IDX=2912;", connection, transaction))
            {
                await open.ExecuteNonQueryAsync(token);
            }
            var exception = await Assert.ThrowsAsync<EffectValidationException>(() =>
                Executor.ValidateAsync(connection, transaction, plan, "SAVE", token, ["ZZ", "ZZT2609BI01"]));
            Assert.Contains("以下序号项量产模入库不能大于模具完工未入数量", exception.Message);
            Assert.Contains("2", exception.Message);
        }
        finally
        {
            await transaction.RollbackAsync(token);
        }
    }

    [Fact]
    public async Task 工序发料_受门控的出库不超工序工单入库_门关跳过门开命中五列诊断()
    {
        var token = CancellationToken.None;
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
        try
        {
            // 工序工单行：已出库 9、已入库 10；本单发料 5 ⇒ 9+5 > 10 超量
            await using (var seed = new SqlCommand("""
                INSERT INTO dbo.MOC_WORK_D (WORK_TYPE, WORK_NO, SERIAL_NO, PROCESS_QTY, FINISHED_OUT_QTY, FINISHED_IN_QTY)
                VALUES (N'ZZ', N'ZZT2609WO01', 1, 10, 9, 10);
                INSERT INTO dbo.MOC_WORK_OUT_M (WORK_OUT_TYPE, WORK_OUT_NO) VALUES (N'ZZ', N'ZZT2609WOOUT01');
                INSERT INTO dbo.MOC_WORK_OUT_D (WORK_OUT_TYPE, WORK_OUT_NO, SERIAL_NO, WORK_TYPE, WORK_NO, WORK_SERIAL_NO, QTY)
                VALUES (N'ZZ', N'ZZT2609WOOUT01', 1, N'ZZ', N'ZZT2609WO01', 1, 5);
                """, connection, transaction))
            {
                await seed.ExecuteNonQueryAsync(token);
            }

            var plan = await LoadPlanAsync(connection, transaction, 2707, token, "qty-not-exceed");

            // 门控关（库内 2707 的 ERROR_NO_SAVE=0）→ 跳过，超量也放行
            await Executor.ValidateAsync(connection, transaction, plan, "SAVE", token, ["ZZ", "ZZT2609WOOUT01"]);

            // 门控开 → 命中：文案头部逐字 + 五列诊断（工单单别/单号/数量/已入库/单据数量）
            await using (var open = new SqlCommand(
                "UPDATE dbo.MODULES SET ERROR_NO_SAVE=1 WHERE M_IDX=2707;", connection, transaction))
            {
                await open.ExecuteNonQueryAsync(token);
            }
            var exception = await Assert.ThrowsAsync<EffectValidationException>(() =>
                Executor.ValidateAsync(connection, transaction, plan, "SAVE", token, ["ZZ", "ZZT2609WOOUT01"]));
            Assert.Contains("以下出库超出工序工单入库数量", exception.Message);
            Assert.Contains("工序工单单别   单号   数量   已入库数量   单据数量", exception.Message);
            Assert.Contains("ZZT2609WO01", exception.Message);
        }
        finally
        {
            await transaction.RollbackAsync(token);
        }
    }

    [Fact]
    public async Task 品质日分析_受模块门控的数量校验_开关关闭跳过_打开命中且文案一致()
    {
        var token = CancellationToken.None;
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
        try
        {
            // 造一张品质日分析单：明细指向一张"品检额度已满"的生产单
            await using (var seed = new SqlCommand("""
                INSERT INTO dbo.MOC_PRODUCE_M (PRODUCE_TYPE, PRODUCE_NO, QTY, FINISHED_ANALYSIS_QTY)
                VALUES (N'ZZ', N'ZZT2609PRD01', 10, 10);
                INSERT INTO dbo.QC_ANALYSIS_M (ANALYSIS_TYPE, ANALYSIS_NO) VALUES (N'ZZ', N'ZZT2609QA01');
                INSERT INTO dbo.QC_ANALYSIS_D (ANALYSIS_TYPE, ANALYSIS_NO, SERIAL_NO, PRODUCE_TYPE, PRODUCE_NO, PRO_NO, PRODUCE_QTY)
                VALUES (N'ZZ', N'ZZT2609QA01', 1, N'ZZ', N'ZZT2609PRD01', N'ZZT2609PRO01', 5);
                INSERT INTO dbo.PRODUCT (PRO_NO, PRO_NAME) VALUES (N'ZZT2609PRO01', N'集成测试品号');
                """, connection, transaction))
            {
                await seed.ExecuteNonQueryAsync(token);
            }

            var plan = await LoadPlanAsync(connection, transaction, 3303, token, "qty-not-exceed");

            // 模块开关关闭（库内 3303 的 ERROR_NO_SAVE=0）→ 门控跳过整条校验，超量也放行
            var flag = Convert.ToInt32(await new SqlCommand(
                "SELECT ISNULL(ERROR_NO_SAVE,0) FROM dbo.MODULES WHERE M_IDX=3303;", connection, transaction)
                .ExecuteScalarAsync(token));
            Assert.Equal(0, flag);
            await Executor.ValidateAsync(connection, transaction, plan, "SAVE", token, ["ZZ", "ZZT2609QA01"]);

            // 门控打开（事务内临时置 1）→ 命中并回报生产单号，文案与旧实现逐字一致
            await using (var open = new SqlCommand(
                "UPDATE dbo.MODULES SET ERROR_NO_SAVE=1 WHERE M_IDX=3303;", connection, transaction))
            {
                await open.ExecuteNonQueryAsync(token);
            }
            var exception = await Assert.ThrowsAsync<EffectValidationException>(() =>
                Executor.ValidateAsync(connection, transaction, plan, "SAVE", token, ["ZZ", "ZZT2609QA01"]));
            Assert.Contains("以下生产单号已品检数量超出生产单生产数量！", exception.Message);
            Assert.Contains("ZZT2609PRD01", exception.Message);

            // 无门控的引用校验（该族里当前**生效**的那部分）：明细指向不存在的制令单 → 拒绝
            await using (var missing = new SqlCommand("""
                UPDATE dbo.MODULES SET ERROR_NO_SAVE=0 WHERE M_IDX=3303;
                UPDATE dbo.QC_ANALYSIS_D SET PRODUCE_NO = N'ZZT2609NOPROD'
                 WHERE ANALYSIS_TYPE = N'ZZ' AND ANALYSIS_NO = N'ZZT2609QA01' AND SERIAL_NO = 1;
                """, connection, transaction))
            {
                await missing.ExecuteNonQueryAsync(token);
            }
            var referencePlan = await LoadPlanAsync(connection, transaction, 3303, token, "reference-exists");
            var reference = await Assert.ThrowsAsync<EffectValidationException>(() =>
                Executor.ValidateAsync(connection, transaction, referencePlan, "SAVE", token, ["ZZ", "ZZT2609QA01"]));
            Assert.Contains("以下序号项制令单不存在", reference.Message);
        }
        finally
        {
            await transaction.RollbackAsync(token);
        }
    }

    [Fact]
    public async Task 量产模具完工_申请数量超承认单可申请数量_拒绝_固定文案()
    {
        var token = CancellationToken.None;
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
        try
        {
            await using (var seed = new SqlCommand("""
                INSERT INTO dbo.MOU_ACCEPT_M (ACCEPT_TYPE, ACCEPT_NO, QTY, FINISHED_QTY)
                VALUES (N'ZZ', N'ZZT2609AC01', 100, 0);
                INSERT INTO dbo.MOU_BATCH_M (BATCH_TYPE, BATCH_NO, ACCEPT_TYPE, ACCEPT_NO, QTY)
                VALUES (N'ZZ', N'ZZT2609BT01', N'ZZ', N'ZZT2609AC01', 101);
                """, connection, transaction))
            {
                await seed.ExecuteNonQueryAsync(token);
            }

            var plan = await LoadPlanAsync(connection, transaction, 2906, token, "qty-not-exceed");
            var exception = await Assert.ThrowsAsync<EffectValidationException>(() =>
                Executor.ValidateAsync(connection, transaction, plan, "SAVE", token, ["ZZ", "ZZT2609BT01"]));
            Assert.Contains("申请数量已超过承认单可申请数量", exception.Message);

            // 本单数量落到"已完工 + 本单 ≤ 可申请"之内 → 放行
            await using (var fix = new SqlCommand(
                "UPDATE dbo.MOU_BATCH_M SET QTY = 99 WHERE BATCH_TYPE = N'ZZ' AND BATCH_NO = N'ZZT2609BT01';",
                connection, transaction))
            {
                await fix.ExecuteNonQueryAsync(token);
            }
            await Executor.ValidateAsync(connection, transaction, plan, "SAVE", token, ["ZZ", "ZZT2609BT01"]);

            // 边界：已完工 1 + 本单 99 == 承认单 100 → 恰好用满额度，放行（判据是严格大于）
            await using (var fill = new SqlCommand(
                "UPDATE dbo.MOU_ACCEPT_M SET FINISHED_QTY = 1 WHERE ACCEPT_TYPE = N'ZZ' AND ACCEPT_NO = N'ZZT2609AC01';",
                connection, transaction))
            {
                await fill.ExecuteNonQueryAsync(token);
            }
            await Executor.ValidateAsync(connection, transaction, plan, "SAVE", token, ["ZZ", "ZZT2609BT01"]);
        }
        finally
        {
            await transaction.RollbackAsync(token);
        }
    }
}
