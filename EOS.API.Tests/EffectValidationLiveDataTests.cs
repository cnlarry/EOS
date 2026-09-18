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

    /// <summary>
    /// 生产领料族（1503 MOC_GET_D）与模房领料（2907 MOU_GET_D）的"批管品必填批号"已由保存期
    /// line-require 承担：命中拒绝并回报明细序号，补上批号后放行。两张明细表同形，用例一并覆盖，
    /// 确认迁移后两个族名（moc-get / mou-get）不再需要 C# 分派。
    /// </summary>
    [Fact]
    public async Task 领料族必填批号_迁目录后仍命中并回报序号()
    {
        var token = CancellationToken.None;
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
        try
        {
            await using (var seed = new SqlCommand("""
                INSERT INTO dbo.PRODUCT (PRO_NO, PRO_NAME, MANAGE_BATCH) VALUES (N'ADR12GETP', N'集成测试领料批管品', 1);
                INSERT INTO dbo.MOC_GET_D (GET_TYPE, GET_NO, SERIAL_NO, PRO_NO, BATCH_NO)
                    VALUES (N'ADR12', N'ADR12MOCGET1', 1, N'ADR12GETP', N'');
                INSERT INTO dbo.MOU_GET_D (GET_TYPE, GET_NO, SERIAL_NO, PRO_NO, BATCH_NO)
                    VALUES (N'ADR12', N'ADR12MOUGET1', 1, N'ADR12GETP', N'');
                """, connection, transaction))
            {
                await seed.ExecuteNonQueryAsync(token);
            }

            // 生产领料单（1503）：批管品缺批号 → 拒绝且回报序号
            var mocPlan = await LoadPlanAsync(connection, transaction, 1503, token, "line-require");
            var mocFailure = await Assert.ThrowsAsync<EffectValidationException>(() =>
                Executor.ValidateAsync(connection, transaction, mocPlan, "SAVE", token, ["ADR12", "ADR12MOCGET1"]));
            Assert.Contains("以下序号项需要输入批号", mocFailure.Message);
            Assert.Contains("1", mocFailure.Message);

            // 生产领料单（1514 生产补料单，共用 MOC_GET_D）：同形判据同样生效
            var refillPlan = await LoadPlanAsync(connection, transaction, 1514, token, "line-require");
            var refillFailure = await Assert.ThrowsAsync<EffectValidationException>(() =>
                Executor.ValidateAsync(connection, transaction, refillPlan, "SAVE", token, ["ADR12", "ADR12MOCGET1"]));
            Assert.Contains("以下序号项需要输入批号", refillFailure.Message);

            // 模房领料单（2907，MOU_GET_D）→ 拒绝
            var mouPlan = await LoadPlanAsync(connection, transaction, 2907, token, "line-require");
            var mouFailure = await Assert.ThrowsAsync<EffectValidationException>(() =>
                Executor.ValidateAsync(connection, transaction, mouPlan, "SAVE", token, ["ADR12", "ADR12MOUGET1"]));
            Assert.Contains("以下序号项需要输入批号", mouFailure.Message);

            // 补上批号 → 两单均放行
            await using (var fix = new SqlCommand("""
                UPDATE dbo.MOC_GET_D SET BATCH_NO = N'B001'
                    WHERE GET_TYPE = N'ADR12' AND GET_NO = N'ADR12MOCGET1' AND SERIAL_NO = 1;
                UPDATE dbo.MOU_GET_D SET BATCH_NO = N'B001'
                    WHERE GET_TYPE = N'ADR12' AND GET_NO = N'ADR12MOUGET1' AND SERIAL_NO = 1;
                """, connection, transaction))
            {
                await fix.ExecuteNonQueryAsync(token);
            }
            await Executor.ValidateAsync(connection, transaction, mocPlan, "SAVE", token, ["ADR12", "ADR12MOCGET1"]);
            await Executor.ValidateAsync(connection, transaction, mouPlan, "SAVE", token, ["ADR12", "ADR12MOUGET1"]);
        }
        finally
        {
            await transaction.RollbackAsync(token);
        }
    }

    /// <summary>
    /// 行字段断言（line-require.check.assert）两条迁移路径的真库用例：
    /// ① 货币资料（110103）：主表行作用域（scope=MASTER）+ 触发器 IS_BASE=1 + 断言 CURR_RATE=1；
    /// ② 库存盘点单（130101）：明细行无条件断言 CHECK_QTY&gt;=0，命中回报序号。
    /// </summary>
    [Fact]
    public async Task 行字段断言_本位币汇率与盘点数下界_迁目录后仍生效()
    {
        var token = CancellationToken.None;
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
        try
        {
            await using (var seed = new SqlCommand("""
                INSERT INTO dbo.CURR (CURR_ID, CURR_NAME, CURR_RATE, IS_BASE, CREATE_PERSON, CREATE_DATE, CONFIRM_TAG, CI)
                    VALUES (N'ADR12CUR1', N'集成测试本位币', 2, 1, N'ADR12', GETDATE(), 0, N'');
                INSERT INTO dbo.INV_CHECK_STOCK_D (CHECK_STOCK_TYPE, CHECK_STOCK_NO, SERIAL_NO, CHECK_QTY)
                    VALUES (N'ADR12', N'ADR12STOCK1', 1, -5);
                """, connection, transaction))
            {
                await seed.ExecuteNonQueryAsync(token);
            }

            var currencyPlan = await LoadPlanAsync(connection, transaction, 110103, token, "line-require");
            var currencyFailure = await Assert.ThrowsAsync<EffectValidationException>(() =>
                Executor.ValidateAsync(connection, transaction, currencyPlan, "SAVE", token, ["ADR12CUR1"]));
            Assert.Equal("本位币汇率只能为1", currencyFailure.Message);

            var stockPlan = await LoadPlanAsync(connection, transaction, 130101, token, "line-require");
            var stockFailure = await Assert.ThrowsAsync<EffectValidationException>(() =>
                Executor.ValidateAsync(connection, transaction, stockPlan, "SAVE", token, ["ADR12", "ADR12STOCK1"]));
            Assert.Contains("以下序号项盘点数小于0", stockFailure.Message);
            Assert.Contains("1", stockFailure.Message);

            // 汇率改为 1 → 放行；非本位币即使汇率非 1 也放行（触发器不成立）
            await using (var fixRate = new SqlCommand(
                "UPDATE dbo.CURR SET CURR_RATE = 1 WHERE CURR_ID = N'ADR12CUR1';", connection, transaction))
            {
                await fixRate.ExecuteNonQueryAsync(token);
            }
            await Executor.ValidateAsync(connection, transaction, currencyPlan, "SAVE", token, ["ADR12CUR1"]);
            await using (var clearBase = new SqlCommand(
                "UPDATE dbo.CURR SET IS_BASE = 0, CURR_RATE = 2 WHERE CURR_ID = N'ADR12CUR1';", connection, transaction))
            {
                await clearBase.ExecuteNonQueryAsync(token);
            }
            await Executor.ValidateAsync(connection, transaction, currencyPlan, "SAVE", token, ["ADR12CUR1"]);

            // 盘点数为零（下界取等号）与空值均放行
            await using (var fixQty = new SqlCommand(
                "UPDATE dbo.INV_CHECK_STOCK_D SET CHECK_QTY = 0 WHERE CHECK_STOCK_TYPE = N'ADR12' AND CHECK_STOCK_NO = N'ADR12STOCK1';",
                connection, transaction))
            {
                await fixQty.ExecuteNonQueryAsync(token);
            }
            await Executor.ValidateAsync(connection, transaction, stockPlan, "SAVE", token, ["ADR12", "ADR12STOCK1"]);
            await using (var nullQty = new SqlCommand(
                "UPDATE dbo.INV_CHECK_STOCK_D SET CHECK_QTY = NULL WHERE CHECK_STOCK_TYPE = N'ADR12' AND CHECK_STOCK_NO = N'ADR12STOCK1';",
                connection, transaction))
            {
                await nullQty.ExecuteNonQueryAsync(token);
            }
            await Executor.ValidateAsync(connection, transaction, stockPlan, "SAVE", token, ["ADR12", "ADR12STOCK1"]);
        }
        finally
        {
            await transaction.RollbackAsync(token);
        }
    }

    /// <summary>
    /// 进出口报关单（300301/300304 出口、300302/300305 进口）的"报关数量不超备案合同数量"
    /// 已由保存期 qty-not-exceed（usage-not-exceed + MODULE 门控）承担：门关跳过、门开命中且
    /// 四列诊断（手册编号 / 合同数量 / 已报关数量 / 本单数量）逐字一致、数量降到额度内放行。
    /// </summary>
    [Fact]
    public async Task 报关单_受门控的报关不超合同数量_门关跳过门开命中四列诊断()
    {
        var token = CancellationToken.None;
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
        try
        {
            await using (var seed = new SqlCommand("""
                INSERT INTO dbo.CUS_MANUAL_PRO (MANUAL_NO, SERIAL_NO, QTY, EXP_QTY, ZC_QTY, ZR_QTY)
                    VALUES (N'ADR12MANUALPRO', 1, 100, 95, 0, 0);
                INSERT INTO dbo.CUS_EXPORT_D (EXPORT_TYPE, EXPORT_NO, SERIAL_NO, MANUAL_NO, PRO_SERIAL_NO, QTY)
                    VALUES (N'ADR12', N'ADR12EXP1', 1, N'ADR12MANUALPRO', 1, 10);
                INSERT INTO dbo.CUS_MANUAL_MAT (MANUAL_NO, SERIAL_NO, QTY, IMP_QTY, TRAN_QTY, ZC_QTY, BF_QTY, ZR_QTY)
                    VALUES (N'ADR12MANUALMAT', 1, 100, 95, 0, 0, 0, 0);
                INSERT INTO dbo.CUS_IMPORT_D (IMPORT_TYPE, IMPORT_NO, SERIAL_NO, MANUAL_NO, MAT_SERIAL_NO, QTY)
                    VALUES (N'ADR12', N'ADR12IMP1', 1, N'ADR12MANUALMAT', 1, 10);
                """, connection, transaction))
            {
                await seed.ExecuteNonQueryAsync(token);
            }

            var exportPlan = await LoadPlanAsync(connection, transaction, 300301, token, "qty-not-exceed");
            var importPlan = await LoadPlanAsync(connection, transaction, 300302, token, "qty-not-exceed");

            // 门关（两个模块当前 ERROR_NO_SAVE=0）→ 超量也放行，与旧 C# 的早退分支一致
            await Executor.ValidateAsync(connection, transaction, exportPlan, "SAVE", token, ["ADR12", "ADR12EXP1"]);
            await Executor.ValidateAsync(connection, transaction, importPlan, "SAVE", token, ["ADR12", "ADR12IMP1"]);

            await using (var gate = new SqlCommand(
                "UPDATE dbo.MODULES SET ERROR_NO_SAVE = 1 WHERE M_IDX IN (300301, 300302);", connection, transaction))
            {
                await gate.ExecuteNonQueryAsync(token);
            }

            var exportFailure = await Assert.ThrowsAsync<EffectValidationException>(() =>
                Executor.ValidateAsync(connection, transaction, exportPlan, "SAVE", token, ["ADR12", "ADR12EXP1"]));
            Assert.Equal("以下报关单已超出合同数量\r\n 手册编号  数 量  已出数量  单据数量\r\nADR12MANUALPRO    100    95    10",
                exportFailure.Message);

            var importFailure = await Assert.ThrowsAsync<EffectValidationException>(() =>
                Executor.ValidateAsync(connection, transaction, importPlan, "SAVE", token, ["ADR12", "ADR12IMP1"]));
            Assert.Equal("以下报关单已超出合同数量\r\n 手册编号  数 量  已进数量  单据数量\r\nADR12MANUALMAT    100    95    10",
                importFailure.Message);

            // 本单数量降到额度内（已报关 95 + 本单 5 = 100，不再大于合同 100）→ 放行
            await using (var fix = new SqlCommand("""
                UPDATE dbo.CUS_EXPORT_D SET QTY = 5
                    WHERE EXPORT_TYPE = N'ADR12' AND EXPORT_NO = N'ADR12EXP1' AND SERIAL_NO = 1;
                UPDATE dbo.CUS_IMPORT_D SET QTY = 5
                    WHERE IMPORT_TYPE = N'ADR12' AND IMPORT_NO = N'ADR12IMP1' AND SERIAL_NO = 1;
                """, connection, transaction))
            {
                await fix.ExecuteNonQueryAsync(token);
            }
            await Executor.ValidateAsync(connection, transaction, exportPlan, "SAVE", token, ["ADR12", "ADR12EXP1"]);
            await Executor.ValidateAsync(connection, transaction, importPlan, "SAVE", token, ["ADR12", "ADR12IMP1"]);
        }
        finally
        {
            await transaction.RollbackAsync(token);
        }
    }

    /// <summary>
    /// 备货单（1411）"已备货不超订单/工单完工"已由保存期 qty-not-exceed 承担，且**数量与备品
    /// 两个量纲合成同一判据**（旧实现是一句 `WHERE a OR b`，靠 dimensions + OR 合并复刻）：
    /// 门关跳过；订单口径命中回报七列；只违反备品量纲时同样命中（证明 OR 合并与第二个量纲的
    /// 诊断投影都生效）；切到工单口径走另一张被引用表；最后无门控的批号必填仍拦。
    /// </summary>
    [Fact]
    public async Task 备货单_两量纲不超订单与工单_门控与批号必填()
    {
        var token = CancellationToken.None;
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
        try
        {
            await using (var seed = new SqlCommand("""
                INSERT INTO dbo.PRODUCT (PRO_NO, PRO_NAME, MANAGE_BATCH) VALUES (N'ADR12FITP', N'集成测试备货批管品', 1);
                INSERT INTO dbo.COP_ORDER_D (ORDER_TYPE, ORDER_NO, SERIAL_NO, QTY, SPARE_QTY, FINISHED_FITOUT_QTY, FINISHED_FITOUT_SPARE_QTY)
                    VALUES (N'ADR12', N'ADR12ORD1', 1, 100, 50, 80, 40);
                INSERT INTO dbo.MOC_PRODUCE_M (PRODUCE_TYPE, PRODUCE_NO, FINISHED_QTY, FINISHED_SPARE_QTY, FINISHED_FITOUT_QTY, FINISHED_FITOUT_SPARE_QTY)
                    VALUES (N'ADR12', N'ADR12PRO1', 200, 60, 80, 40);
                INSERT INTO dbo.COP_FITOUT_D (FITOUT_TYPE, FITOUT_NO, SERIAL_NO, ORDER_TYPE, ORDER_NO, ORDER_SERIAL_NO,
                                              PRODUCE_TYPE, PRODUCE_NO, QTY, SPARE_QTY, BATCH_NO, PRO_NO)
                    VALUES (N'ADR12', N'ADR12FIT1', 1, N'ADR12', N'ADR12ORD1', 1, N'ADR12', N'ADR12PRO1', 30, 5, N'', N'ADR12FITP');
                """, connection, transaction))
            {
                await seed.ExecuteNonQueryAsync(token);
            }

            var qtyPlan = await LoadPlanAsync(connection, transaction, 1411, token, "qty-not-exceed");
            var batchPlan = await LoadPlanAsync(connection, transaction, 1411, token, "line-require");
            var keys = new[] { "ADR12", "ADR12FIT1" };

            // 门关（ERROR_NO_SAVE=0、两个全局口径开关均为 0）→ 超量也放行
            await Executor.ValidateAsync(connection, transaction, qtyPlan, "SAVE", token, keys);

            // 订单口径：数量量纲命中（已备货 80 + 本单 30 > 订单 100），七列诊断逐字一致
            await using (var gate = new SqlCommand(
                "UPDATE dbo.MODULES SET ERROR_NO_SAVE = 1 WHERE M_IDX = 1411; UPDATE dbo.SYSSS SET FITOUT_ORDER_TAG = 1;",
                connection, transaction))
            {
                await gate.ExecuteNonQueryAsync(token);
            }
            var orderFailure = await Assert.ThrowsAsync<EffectValidationException>(() =>
                Executor.ValidateAsync(connection, transaction, qtyPlan, "SAVE", token, keys));
            Assert.Equal("以下会出现已备货数量超出订单数量\r\n订单号   数量   已备货数量  单据数量  备品  已备货备品  单据备品\r\nADR12ORD1    100    80    30    50    40    5",
                orderFailure.Message);

            // 只违反备品量纲（数量降到额度内）→ 同一个 check 仍命中，且诊断第 7 列取第二量纲求和张
            await using (var spareOnly = new SqlCommand("""
                UPDATE dbo.COP_FITOUT_D SET QTY = 5, SPARE_QTY = 15
                    WHERE FITOUT_TYPE = N'ADR12' AND FITOUT_NO = N'ADR12FIT1' AND SERIAL_NO = 1;
                """, connection, transaction))
            {
                await spareOnly.ExecuteNonQueryAsync(token);
            }
            var spareFailure = await Assert.ThrowsAsync<EffectValidationException>(() =>
                Executor.ValidateAsync(connection, transaction, qtyPlan, "SAVE", token, keys));
            Assert.Equal("以下会出现已备货数量超出订单数量\r\n订单号   数量   已备货数量  单据数量  备品  已备货备品  单据备品\r\nADR12ORD1    100    80    5    50    40    15",
                spareFailure.Message);

            // 切到工单口径（本单 15 备品 vs 工单已备货 40 + 15 > 完工备品 60？→ 55 ≤ 60 不超；数量 80+5 ≤ 200 不超）
            // 故此处先把工单完工备品降到 50 以命中工单口径
            await using (var produceGate = new SqlCommand("""
                UPDATE dbo.SYSSS SET FITOUT_ORDER_TAG = 0, FITOUT_PRODUCE_TAG = 1;
                UPDATE dbo.MOC_PRODUCE_M SET FINISHED_SPARE_QTY = 50
                    WHERE PRODUCE_TYPE = N'ADR12' AND PRODUCE_NO = N'ADR12PRO1';
                """, connection, transaction))
            {
                await produceGate.ExecuteNonQueryAsync(token);
            }
            var produceFailure = await Assert.ThrowsAsync<EffectValidationException>(() =>
                Executor.ValidateAsync(connection, transaction, qtyPlan, "SAVE", token, keys));
            Assert.Equal("以下会出现已备货数量超出工单完工数量\r\n工单号   数量   已备货数量  单据数量  备品  已备货备品  单据备品\r\nADR12PRO1    200    80    5    50    40    15",
                produceFailure.Message);

            // 数量与备品都降到额度内 → 量纲校验放行，但批管品缺批号仍由无门控的 line-require 拦下
            await using (var clear = new SqlCommand("""
                UPDATE dbo.COP_FITOUT_D SET SPARE_QTY = 0
                    WHERE FITOUT_TYPE = N'ADR12' AND FITOUT_NO = N'ADR12FIT1' AND SERIAL_NO = 1;
                """, connection, transaction))
            {
                await clear.ExecuteNonQueryAsync(token);
            }
            await Executor.ValidateAsync(connection, transaction, qtyPlan, "SAVE", token, keys);
            var batchFailure = await Assert.ThrowsAsync<EffectValidationException>(() =>
                Executor.ValidateAsync(connection, transaction, batchPlan, "SAVE", token, keys));
            Assert.Contains("以下序号项需要输入批号! ", batchFailure.Message);
            Assert.Contains("\r\n1", batchFailure.Message);

            // 补上批号 → 放行
            await using (var fixBatch = new SqlCommand("""
                UPDATE dbo.COP_FITOUT_D SET BATCH_NO = N'B001'
                    WHERE FITOUT_TYPE = N'ADR12' AND FITOUT_NO = N'ADR12FIT1' AND SERIAL_NO = 1;
                """, connection, transaction))
            {
                await fixBatch.ExecuteNonQueryAsync(token);
            }
            await Executor.ValidateAsync(connection, transaction, batchPlan, "SAVE", token, keys);
        }
        finally
        {
            await transaction.RollbackAsync(token);
        }
    }

    /// <summary>
    /// 生产记录单（180401）"完工数量不超制令制程允许生产最大数量"已由保存期 qty-not-exceed 承担：
    /// 来源按（制令别/制令号/工序）分组求和，被引用行按同一键取 `MAX`（旧实现即 `MAX(允许量)`／
    /// `MAX(计划量)`，同一工序有多条制程行时不能按"任意一行"取数），命中回报本单**逐条明细序号**。
    /// 用例把旧实现的那条 SQL 内联为基准，逐字比对引擎输出。
    /// </summary>
    [Fact]
    public async Task 生产记录单_不超制令制程允许最大数量_被引用行聚合与逐明细序号诊断()
    {
        var token = CancellationToken.None;
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
        try
        {
            await using (var seed = new SqlCommand("""
                INSERT INTO dbo.MOC_PRODUCE_PROCESS_D (PRODUCE_TYPE, PRODUCE_NO, SERIAL_NO, PROCEDURE_ID, PROCESS_OVER_QTY, FINISHED_PLAN_QTY)
                    VALUES (N'ADR12', N'ADR12PRO1', 1, N'PROC1', 100, 50),
                           (N'ADR12', N'ADR12PRO1', 2, N'PROC1', 200, 0);
                INSERT INTO dbo.SFC_DAILY_D (DAILY_TYPE, DAILY_NO, SERIAL_NO, PRODUCE_TYPE, PRODUCE_NO, PROCEDURE_ID, FINISHED_QTY)
                    VALUES (N'ADR12', N'ADR12DAILY1', 1, N'ADR12', N'ADR12PRO1', N'PROC1', 30),
                           (N'ADR12', N'ADR12DAILY1', 2, N'ADR12', N'ADR12PRO1', N'PROC1', 40);
                """, connection, transaction))
            {
                await seed.ExecuteNonQueryAsync(token);
            }

            var plan = await LoadPlanAsync(connection, transaction, 180401, token, "qty-not-exceed");
            var keys = new[] { "ADR12", "ADR12DAILY1" };

            // 被引用行取 MAX：允许量 200 vs 计划量 0 + 本单 70 → 不超，放行；
            // 若按"任意一行"（允许量 100 / 计划量 50）取数会误判为超量——这一步正是聚合口径的守卫。
            await Executor.ValidateAsync(connection, transaction, plan, "SAVE", token, keys);
            Assert.Null(await LegacyDailyMessageAsync(connection, transaction, token));

            // 计划量的最大值抬到 200 → 200 < 200 + 70 命中，逐明细回报序号，文案与旧实现逐字一致
            await using (var raise = new SqlCommand("""
                UPDATE dbo.MOC_PRODUCE_PROCESS_D SET FINISHED_PLAN_QTY = 200
                    WHERE PRODUCE_TYPE = N'ADR12' AND PRODUCE_NO = N'ADR12PRO1' AND SERIAL_NO = 2;
                """, connection, transaction))
            {
                await raise.ExecuteNonQueryAsync(token);
            }
            var legacy = await LegacyDailyMessageAsync(connection, transaction, token);
            var failure = await Assert.ThrowsAsync<EffectValidationException>(() =>
                Executor.ValidateAsync(connection, transaction, plan, "SAVE", token, keys));
            Assert.Equal("以下序号项数量超过制令制程允许生产最大数量 \r\n1\r\n2", failure.Message);
            Assert.Equal(legacy, failure.Message);
        }
        finally
        {
            await transaction.RollbackAsync(token);
        }
    }

    /// <summary>已退役的 C# 判据原样内联，作为本用例的基准文案（不在生产代码里保留）。</summary>
    private static async Task<string?> LegacyDailyMessageAsync(
        SqlConnection connection, SqlTransaction transaction, CancellationToken token)
    {
        await using var qty = new SqlCommand("""
            SELECT TOP 11 d.SERIAL_NO
            FROM dbo.SFC_DAILY_D d
            JOIN (SELECT d2.PRODUCE_TYPE, d2.PRODUCE_NO, d2.PROCEDURE_ID,
                         MAX(ISNULL(p.PROCESS_OVER_QTY,0)) PROCESS_OVER_QTY,
                         MAX(ISNULL(p.FINISHED_PLAN_QTY,0)) FINISHED_PLAN_QTY,
                         SUM(ISNULL(d2.FINISHED_QTY,0)) DAILY_QTY
                  FROM dbo.SFC_DAILY_D d2
                  JOIN dbo.MOC_PRODUCE_PROCESS_D p
                    ON p.PRODUCE_TYPE=d2.PRODUCE_TYPE AND p.PRODUCE_NO=d2.PRODUCE_NO AND p.PROCEDURE_ID=d2.PROCEDURE_ID
                  WHERE d2.DAILY_TYPE=@Type AND d2.DAILY_NO=@No
                  GROUP BY d2.PRODUCE_TYPE, d2.PRODUCE_NO, d2.PROCEDURE_ID) g
              ON g.PRODUCE_TYPE=d.PRODUCE_TYPE AND g.PRODUCE_NO=d.PRODUCE_NO AND g.PROCEDURE_ID=d.PROCEDURE_ID
            WHERE d.DAILY_TYPE=@Type AND d.DAILY_NO=@No
              AND g.PROCESS_OVER_QTY < g.FINISHED_PLAN_QTY + g.DAILY_QTY
            ORDER BY d.SERIAL_NO;
            """, connection, transaction);
        qty.Parameters.Add("@Type", SqlDbType.NChar, 10).Value = "ADR12";
        qty.Parameters.Add("@No", SqlDbType.NChar, 20).Value = "ADR12DAILY1";
        await using var reader = await qty.ExecuteReaderAsync(token);
        var lines = new List<string>();
        while (await reader.ReadAsync(token)) lines.Add(Convert.ToInt32(reader.GetValue(0)).ToString());
        return lines.Count == 0
            ? null
            : "以下序号项数量超过制令制程允许生产最大数量 \r\n" + string.Join("\r\n", lines.Take(10));
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
    public async Task 订单变更_四段判据_原单批核变更量下限与订单号唯一()
    {
        var token = CancellationToken.None;
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
        try
        {
            await using (var seed = new SqlCommand("""
                INSERT INTO dbo.COP_ORDER_M (ORDER_TYPE, ORDER_NO, CONFIRM_TAG, CLIENT_ORDER_NO)
                VALUES (N'DD', N'ZZT2609ORD01', 0, NULL);
                INSERT INTO dbo.COP_ORDER_D (ORDER_TYPE, ORDER_NO, SERIAL_NO, FINISHED_SEND_QTY, FINISHED_SPARE_QTY, FINISHED_PRODUCE_QTY, FINISHED_PRODUCE_SPARE_QTY)
                VALUES (N'DD', N'ZZT2609ORD01', 1, 5, 0, 5, 0);
                INSERT INTO dbo.COP_ORDER_CHANGE_M (CHANGE_ORDER_TYPE, CHANGE_ORDER_NO, ORDER_TYPE, ORDER_NO, CLIENT_ORDER_NO)
                VALUES (N'ZZ', N'ZZT2609OC01', N'DD', N'ZZT2609ORD01', N'ZZT2609CON01');
                INSERT INTO dbo.COP_ORDER_CHANGE_D (CHANGE_ORDER_TYPE, CHANGE_ORDER_NO, SERIAL_NO, ORDER_TYPE, ORDER_NO, ORDER_SERIAL_NO, QTY, SPARE_QTY, PLAN_QTY)
                VALUES (N'ZZ', N'ZZT2609OC01', 1, N'DD', N'ZZT2609ORD01', 1, 2, 0, 1);
                """, connection, transaction))
            {
                await seed.ExecuteNonQueryAsync(token);
            }

            // ① 原订单未批核 → 拒绝
            var refPlan = await LoadPlanAsync(connection, transaction, 1418, token, "reference-exists");
            var notApproved = await Assert.ThrowsAsync<EffectValidationException>(() =>
                Executor.ValidateAsync(connection, transaction, refPlan, "SAVE", token, ["ZZ", "ZZT2609OC01"]));
            Assert.Contains("订单未批核，不可变更", notApproved.Message);

            // ② 批核后：变更明细数量 2 < 已送货 5 → 命中
            await using (var approve = new SqlCommand(
                "UPDATE dbo.COP_ORDER_M SET CONFIRM_TAG = 1 WHERE ORDER_TYPE = N'DD' AND ORDER_NO = N'ZZT2609ORD01';",
                connection, transaction))
            {
                await approve.ExecuteNonQueryAsync(token);
            }
            var qtyPlan = await LoadPlanAsync(connection, transaction, 1418, token, "qty-not-exceed");
            var tooLow = await Assert.ThrowsAsync<EffectValidationException>(() =>
                Executor.ValidateAsync(connection, transaction, qtyPlan, "SAVE", token, ["ZZ", "ZZT2609OC01"]));
            Assert.Contains("变更后以下序号项订单数量小于已完工或已送货数量", tooLow.Message);

            // ③ 数量补齐后：计划量 1 < 已下生产单 5 → 命中计划量判据
            await using (var fix = new SqlCommand(
                "UPDATE dbo.COP_ORDER_CHANGE_D SET QTY = 5, PLAN_QTY = 5 WHERE CHANGE_ORDER_TYPE = N'ZZ' AND CHANGE_ORDER_NO = N'ZZT2609OC01' AND SERIAL_NO = 1;",
                connection, transaction))
            {
                await fix.ExecuteNonQueryAsync(token);
            }
            await Executor.ValidateAsync(connection, transaction, qtyPlan, "SAVE", token, ["ZZ", "ZZT2609OC01"]);

            // ④ 客户订单号与另一订单重复 → 拒绝
            await using (var dup = new SqlCommand(
                "INSERT INTO dbo.COP_ORDER_M (ORDER_TYPE, ORDER_NO, CONFIRM_TAG, CLIENT_ORDER_NO) VALUES (N'DD', N'ZZT2609ORD02', 1, N'ZZT2609CON01');",
                connection, transaction))
            {
                await dup.ExecuteNonQueryAsync(token);
            }
            var dupPlan = await LoadPlanAsync(connection, transaction, 1418, token, "duplicate-check");
            var duplicated = await Assert.ThrowsAsync<EffectValidationException>(() =>
                Executor.ValidateAsync(connection, transaction, dupPlan, "SAVE", token, ["ZZ", "ZZT2609OC01"]));
            Assert.Contains("客户订单号重复", duplicated.Message);

            // ⑤ 该订单号改为唯一 → 放行（原单自身不算重复：excludeVia 生效）
            await using (var uniq = new SqlCommand(
                "UPDATE dbo.COP_ORDER_M SET CLIENT_ORDER_NO = N'ZZT2609CON02' WHERE ORDER_NO = N'ZZT2609ORD02';",
                connection, transaction))
            {
                await uniq.ExecuteNonQueryAsync(token);
            }
            await Executor.ValidateAsync(connection, transaction, dupPlan, "SAVE", token, ["ZZ", "ZZT2609OC01"]);
        }
        finally
        {
            await transaction.RollbackAsync(token);
        }
    }

    [Fact]
    public async Task 采购变更_原单批核与变更量下限_三态校验()
    {
        var token = CancellationToken.None;
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
        try
        {
            await using (var seed = new SqlCommand("""
                INSERT INTO dbo.PUR_PURCHASE_M (PURCHASE_TYPE, PURCHASE_NO, CONFIRM_TAG)
                VALUES (N'ZZ', N'ZZT2609PUR01', 0);
                INSERT INTO dbo.PUR_PURCHASE_D (PURCHASE_TYPE, PURCHASE_NO, SERIAL_NO, RECEIVE_QTY)
                VALUES (N'ZZ', N'ZZT2609PUR01', 1, 6);
                INSERT INTO dbo.PUR_PURCHASE_CHANGE_M (CHANGE_PURCHASE_TYPE, CHANGE_PURCHASE_NO, PURCHASE_TYPE, PURCHASE_NO)
                VALUES (N'ZZ', N'ZZT2609PCG01', N'ZZ', N'ZZT2609PUR01');
                INSERT INTO dbo.PUR_PURCHASE_CHANGE_D (CHANGE_PURCHASE_TYPE, CHANGE_PURCHASE_NO, SERIAL_NO, PURCHASE_TYPE, PURCHASE_NO, PURCHASE_SERIAL_NO, QTY)
                VALUES (N'ZZ', N'ZZT2609PCG01', 1, N'ZZ', N'ZZT2609PUR01', 1, 4);
                """, connection, transaction))
            {
                await seed.ExecuteNonQueryAsync(token);
            }

            // ① 原单未批核 → 拒绝
            var refPlan = await LoadPlanAsync(connection, transaction, 1609, token, "reference-exists");
            var notApproved = await Assert.ThrowsAsync<EffectValidationException>(() =>
                Executor.ValidateAsync(connection, transaction, refPlan, "SAVE", token, ["ZZ", "ZZT2609PCG01"]));
            Assert.Contains("采购单未批核，不可变更", notApproved.Message);

            // ② 原单批核后：变更明细数量 4 < 已收货 6 → 命中并回报本单序号
            await using (var approve = new SqlCommand(
                "UPDATE dbo.PUR_PURCHASE_M SET CONFIRM_TAG = 1 WHERE PURCHASE_TYPE = N'ZZ' AND PURCHASE_NO = N'ZZT2609PUR01';",
                connection, transaction))
            {
                await approve.ExecuteNonQueryAsync(token);
            }
            var qtyPlan = await LoadPlanAsync(connection, transaction, 1609, token, "qty-not-exceed");
            var tooLow = await Assert.ThrowsAsync<EffectValidationException>(() =>
                Executor.ValidateAsync(connection, transaction, qtyPlan, "SAVE", token, ["ZZ", "ZZT2609PCG01"]));
            Assert.Contains("变更后以下序号项采购单数量小于已收货数量", tooLow.Message);
            Assert.Contains("1", tooLow.Message);

            // ③ 变更量补齐到 6 → 放行
            await using (var ok = new SqlCommand(
                "UPDATE dbo.PUR_PURCHASE_CHANGE_D SET QTY = 6 WHERE CHANGE_PURCHASE_TYPE = N'ZZ' AND CHANGE_PURCHASE_NO = N'ZZT2609PCG01' AND SERIAL_NO = 1;",
                connection, transaction))
            {
                await ok.ExecuteNonQueryAsync(token);
            }
            await Executor.ValidateAsync(connection, transaction, qtyPlan, "SAVE", token, ["ZZ", "ZZT2609PCG01"]);
        }
        finally
        {
            await transaction.RollbackAsync(token);
        }
    }

    [Fact]
    public async Task 制令变更_原单批核与变更量下限_三态校验()
    {
        var token = CancellationToken.None;
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
        try
        {
            // 制令单：未批核 + 已生产 8（变更主表数量 5 更低）；制令明细已领料 4（变更明细应领 2 更低）
            await using (var seed = new SqlCommand("""
                INSERT INTO dbo.MOC_PRODUCE_M (PRODUCE_TYPE, PRODUCE_NO, CONFIRM_TAG, FINISHED_QTY, FINISHED_SPARE_QTY)
                VALUES (N'ZZ', N'ZZT2609PRD03', 0, 8, 0);
                INSERT INTO dbo.MOC_PRODUCE_D (PRODUCE_TYPE, PRODUCE_NO, PRO_NO, SERIAL_NO, USED_QTY)
                VALUES (N'ZZ', N'ZZT2609PRD03', N'ZZT2609PRO02', 1, 4);
                INSERT INTO dbo.MOC_PRODUCE_CHANGE_M (CHANGE_PRODUCE_TYPE, CHANGE_PRODUCE_NO, PRODUCE_TYPE, PRODUCE_NO, QTY, SPARE_QTY)
                VALUES (N'ZZ', N'ZZT2609PC01', N'ZZ', N'ZZT2609PRD03', 5, 0);
                INSERT INTO dbo.MOC_PRODUCE_CHANGE_D (CHANGE_PRODUCE_TYPE, CHANGE_PRODUCE_NO, SERIAL_NO, PRODUCE_TYPE, PRODUCE_NO, PRODUCE_SERIAL_NO, NEED_QTY)
                VALUES (N'ZZ', N'ZZT2609PC01', 1, N'ZZ', N'ZZT2609PRD03', 1, 2);
                """, connection, transaction))
            {
                await seed.ExecuteNonQueryAsync(token);
            }

            // ① 原单未批核 → 拒绝
            var refPlan = await LoadPlanAsync(connection, transaction, 1509, token, "reference-exists");
            var notApproved = await Assert.ThrowsAsync<EffectValidationException>(() =>
                Executor.ValidateAsync(connection, transaction, refPlan, "SAVE", token, ["ZZ", "ZZT2609PC01"]));
            Assert.Contains("生产单未批核，不可变更", notApproved.Message);

            // ② 原单批核后：变更量不得小于已生产（主表级）
            await using (var approve = new SqlCommand(
                "UPDATE dbo.MOC_PRODUCE_M SET CONFIRM_TAG = 1 WHERE PRODUCE_TYPE = N'ZZ' AND PRODUCE_NO = N'ZZT2609PRD03';",
                connection, transaction))
            {
                await approve.ExecuteNonQueryAsync(token);
            }
            var qtyPlan = await LoadPlanAsync(connection, transaction, 1509, token, "qty-not-exceed");
            // 主表数量 5 < 已生产 8 → 命中；此处先把变更量抬到 8 以便单独验证明细级判据
            var masterTooLow = await Assert.ThrowsAsync<EffectValidationException>(() =>
                Executor.ValidateAsync(connection, transaction, qtyPlan, "SAVE", token, ["ZZ", "ZZT2609PC01"]));
            Assert.Contains("变更后以下序号项数量小于已生产数量", masterTooLow.Message);

            // ③ 主表数量合规、明细应领料 2 < 已领料 4 → 命中明细级判据并回报本单序号
            await using (var fix = new SqlCommand(
                "UPDATE dbo.MOC_PRODUCE_CHANGE_M SET QTY = 8 WHERE CHANGE_PRODUCE_TYPE = N'ZZ' AND CHANGE_PRODUCE_NO = N'ZZT2609PC01';",
                connection, transaction))
            {
                await fix.ExecuteNonQueryAsync(token);
            }
            var lineTooLow = await Assert.ThrowsAsync<EffectValidationException>(() =>
                Executor.ValidateAsync(connection, transaction, qtyPlan, "SAVE", token, ["ZZ", "ZZT2609PC01"]));
            Assert.Contains("变更后以下序号项应领料数量小于制令已领料", lineTooLow.Message);

            // ④ 明细应领料补齐到 4 → 放行
            await using (var ok = new SqlCommand(
                "UPDATE dbo.MOC_PRODUCE_CHANGE_D SET NEED_QTY = 4 WHERE CHANGE_PRODUCE_TYPE = N'ZZ' AND CHANGE_PRODUCE_NO = N'ZZT2609PC01' AND SERIAL_NO = 1;",
                connection, transaction))
            {
                await ok.ExecuteNonQueryAsync(token);
            }
            await Executor.ValidateAsync(connection, transaction, qtyPlan, "SAVE", token, ["ZZ", "ZZT2609PC01"]);
        }
        finally
        {
            await transaction.RollbackAsync(token);
        }
    }

    [Fact]
    public async Task 送货回执_被引用行已有回执即拒绝_未回执放行()
    {
        var token = CancellationToken.None;
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
        try
        {
            await using (var seed = new SqlCommand("""
                INSERT INTO dbo.COP_CALLBACK_M (CALLBACK_TYPE, CALLBACK_NO) VALUES (N'ZZ', N'ZZT2609CB01');
                INSERT INTO dbo.COP_CALLBACK_D (CALLBACK_TYPE, CALLBACK_NO, SERIAL_NO, S_R_TYPE, S_R_NO, S_R_SERIAL_NO)
                VALUES (N'ZZ', N'ZZT2609CB01', 1, N'ZZ', N'ZZT2609SND01', 1);
                INSERT INTO dbo.COP_SEND_D (SEND_TYPE, SEND_NO, SERIAL_NO, CALLBACK_NO)
                VALUES (N'ZZ', N'ZZT2609SND01', 1, NULL);
                """, connection, transaction))
            {
                await seed.ExecuteNonQueryAsync(token);
            }

            var plan = await LoadPlanAsync(connection, transaction, 1413, token, "reference-exists");

            // 被引用的送货行尚未回执 → 放行
            await Executor.ValidateAsync(connection, transaction, plan, "SAVE", token, ["ZZ", "ZZT2609CB01"]);

            // 该送货行已有回执号 → 拒绝，回报**本单明细**序号（与旧实现取 c.SERIAL_NO 一致）
            await using (var receipt = new SqlCommand(
                "UPDATE dbo.COP_SEND_D SET CALLBACK_NO = N'RC001' WHERE SEND_TYPE = N'ZZ' AND SEND_NO = N'ZZT2609SND01' AND SERIAL_NO = 1;",
                connection, transaction))
            {
                await receipt.ExecuteNonQueryAsync(token);
            }
            var exception = await Assert.ThrowsAsync<EffectValidationException>(() =>
                Executor.ValidateAsync(connection, transaction, plan, "SAVE", token, ["ZZ", "ZZT2609CB01"]));
            Assert.Contains("以下序号项送、退货已有回执", exception.Message);
            Assert.Contains("1", exception.Message);

            // 退货侧同形断言：引用行已有回执同样命中
            await using (var ret = new SqlCommand("""
                UPDATE dbo.COP_SEND_D SET CALLBACK_NO = NULL WHERE SEND_TYPE = N'ZZ' AND SEND_NO = N'ZZT2609SND01' AND SERIAL_NO = 1;
                UPDATE dbo.COP_CALLBACK_D SET S_R_TYPE = N'ZZ', S_R_NO = N'ZZT2609RET01', S_R_SERIAL_NO = 1
                 WHERE CALLBACK_TYPE = N'ZZ' AND CALLBACK_NO = N'ZZT2609CB01' AND SERIAL_NO = 1;
                INSERT INTO dbo.COP_RETURN_D (RETURN_TYPE, RETURN_NO, SERIAL_NO, CALLBACK_NO)
                VALUES (N'ZZ', N'ZZT2609RET01', 1, N'RC002');
                """, connection, transaction))
            {
                await ret.ExecuteNonQueryAsync(token);
            }
            var returned = await Assert.ThrowsAsync<EffectValidationException>(() =>
                Executor.ValidateAsync(connection, transaction, plan, "SAVE", token, ["ZZ", "ZZT2609CB01"]));
            Assert.Contains("以下序号项送、退货已有回执", returned.Message);
        }
        finally
        {
            await transaction.RollbackAsync(token);
        }
    }

    [Fact]
    public async Task 产品制程_用固定时间时固定时间不得为零_条件触发与放行()
    {
        var token = CancellationToken.None;
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
        try
        {
            await using (var seed = new SqlCommand("""
                INSERT INTO dbo.SFC_PROCESS_M (PRO_NO) VALUES (N'ZZT2609SP01');
                INSERT INTO dbo.SFC_PROCESS_D (PRO_NO, SERIAL_NO, STANDARD_TIME_TAG, STANDARD_TIME)
                VALUES (N'ZZT2609SP01', 1, 1, 0);
                """, connection, transaction))
            {
                await seed.ExecuteNonQueryAsync(token);
            }

            var plan = await LoadPlanAsync(connection, transaction, 2703, token, "line-require");

            // 固定时间标记=1 且时间为 0 → 拒绝，文案与旧实现逐字一致（含尾部 " \r\n"）
            var exception = await Assert.ThrowsAsync<EffectValidationException>(() =>
                Executor.ValidateAsync(connection, transaction, plan, "SAVE", token, ["ZZT2609SP01"]));
            Assert.Contains("产品编号使用固定时间时，固定时间不能为0", exception.Message);

            // 填上固定时间 → 放行
            await using (var fill = new SqlCommand(
                "UPDATE dbo.SFC_PROCESS_D SET STANDARD_TIME = 5 WHERE PRO_NO = N'ZZT2609SP01' AND SERIAL_NO = 1;",
                connection, transaction))
            {
                await fill.ExecuteNonQueryAsync(token);
            }
            await Executor.ValidateAsync(connection, transaction, plan, "SAVE", token, ["ZZT2609SP01"]);

            // 不用固定时间（标记=0）时，时间仍为 0 也放行（触发条件不成立）
            await using (var noTag = new SqlCommand(
                "UPDATE dbo.SFC_PROCESS_D SET STANDARD_TIME_TAG = 0, STANDARD_TIME = 0 WHERE PRO_NO = N'ZZT2609SP01' AND SERIAL_NO = 1;",
                connection, transaction))
            {
                await noTag.ExecuteNonQueryAsync(token);
            }
            await Executor.ValidateAsync(connection, transaction, plan, "SAVE", token, ["ZZT2609SP01"]);
        }
        finally
        {
            await transaction.RollbackAsync(token);
        }
    }

    [Fact]
    public async Task 生产出库_受门控的出库不超可出库_门关跳过门开命中_批号必填无门控生效()
    {
        var token = CancellationToken.None;
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
        try
        {
            // 制令单：订单可出库 10、已出库 9；本单出库 5 ⇒ 9+5 > 10 超量（当前 SYSSS.FITOUT_TAG=0 走"订单"分支）
            await using (var seed = new SqlCommand("""
                INSERT INTO dbo.MOC_PRODUCE_M (PRODUCE_TYPE, PRODUCE_NO, QTY, FINISHED_SEND_QTY, FINISHED_SPARE_QTY)
                VALUES (N'ZZ', N'ZZT2609PRD02', 10, 9, 0);
                INSERT INTO dbo.MOC_PRODUCT_OUT_M (PRODUCT_OUT_TYPE, PRODUCT_OUT_NO) VALUES (N'ZZ', N'ZZT2609POUT01');
                INSERT INTO dbo.MOC_PRODUCT_OUT_D (PRODUCT_OUT_TYPE, PRODUCT_OUT_NO, SERIAL_NO, PRODUCE_TYPE, PRODUCE_NO, PRO_NO, QTY, BATCH_NO)
                VALUES (N'ZZ', N'ZZT2609POUT01', 1, N'ZZ', N'ZZT2609PRD02', N'ZZT2609PRO01', 5, NULL);
                INSERT INTO dbo.PRODUCT (PRO_NO, PRO_NAME, MANAGE_BATCH) VALUES (N'ZZT2609PRO01', N'集成测试品号', 0);
                """, connection, transaction))
            {
                await seed.ExecuteNonQueryAsync(token);
            }

            var qtyPlan = await LoadPlanAsync(connection, transaction, 2815, token, "qty-not-exceed");

            // 门控关（库内 2815 的 ERROR_NO_SAVE=0）→ 跳过数量校验
            await Executor.ValidateAsync(connection, transaction, qtyPlan, "SAVE", token, ["ZZ", "ZZT2609POUT01"]);

            // 门控开 → 命中"订单可出库"分支，文案逐字并回报制令单号
            await using (var open = new SqlCommand(
                "UPDATE dbo.MODULES SET ERROR_NO_SAVE=1 WHERE M_IDX=2815;", connection, transaction))
            {
                await open.ExecuteNonQueryAsync(token);
            }
            var exception = await Assert.ThrowsAsync<EffectValidationException>(() =>
                Executor.ValidateAsync(connection, transaction, qtyPlan, "SAVE", token, ["ZZ", "ZZT2609POUT01"]));
            Assert.Contains("以下生产单出库数量超出订单可出库", exception.Message);
            Assert.Contains("ZZT2609PRD02", exception.Message);

            // 批号必填（**无门控**，当前即生效）：批管品未填批号 → 拒绝并回报序号
            await using (var batch = new SqlCommand("""
                UPDATE dbo.MODULES SET ERROR_NO_SAVE=0 WHERE M_IDX=2815;
                UPDATE dbo.PRODUCT SET MANAGE_BATCH = 1 WHERE PRO_NO = N'ZZT2609PRO01';
                """, connection, transaction))
            {
                await batch.ExecuteNonQueryAsync(token);
            }
            var linePlan = await LoadPlanAsync(connection, transaction, 2815, token, "line-require");
            var lineException = await Assert.ThrowsAsync<EffectValidationException>(() =>
                Executor.ValidateAsync(connection, transaction, linePlan, "SAVE", token, ["ZZ", "ZZT2609POUT01"]));
            Assert.Contains("以下序号项需要输入批号", lineException.Message);
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
