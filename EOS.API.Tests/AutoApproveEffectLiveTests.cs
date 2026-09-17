using System.Data;
using System.Text.Json;
using EOS.API.Data;
using EOS.API.Data.Effects;
using EOS.API.Models;
using EOS.API.Tests.Tools;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 自动批核（MODULES.AUTO_APPROVE=1）接入效果引擎后的真库验收：
/// 正常自动批核（状态翻转 + 审计）、重复触发幂等（不重复累计）、
/// 引擎拦截时状态还原且零残留（ADR §4：阻断 + 效果零写入 + CONFIRM_TAG 还原）。
/// 合成单据一律用 ADR12 专用键，用例结束即删除。
/// 需要 EOS_ERP_TEST_CONNECTION。
/// </summary>
[Collection("live-database")]
public sealed class AutoApproveEffectLiveTests
{
    private static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("EOS_ERP_TEST_CONNECTION")
        ?? throw new InvalidOperationException("真库集成测试需要 EOS_ERP_TEST_CONNECTION；未配置即失败（无连接跳过≠已验证）。");

    private const string BatchNo = "ADR12AUTOBATCH01";
    private const string BatchProNo = "ADR12AUTOBATCHPRO";
    private const string LoanNo = "ADR12AUTOLOAN01";
    private const string ReceiveNo = "ADR12AUTORECV01";
    private const string PurchaseNo = "ADR12AUTOPO01";

    /// <summary>自动批核的经办人应当就是保存人（测试里模拟一个具体的人，不用 SYSTEM 占位）。</summary>
    private const string ConfirmPerson = "测试保存人";
    private const string NoStockProNo = "ADR12AUTONOSTOCKPRO";

    private static DbConnectionFactory Connections()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:ErpDatabase"] = ConnectionString })
            .Build();
        return new DbConnectionFactory(configuration);
    }

    private static WorkbenchApprovalService CreateService(DbConnectionFactory connections, bool engineEnabled)
    {
        var provider = new WorkbenchDefinitionProvider(connections, NullLogger<WorkbenchDefinitionProvider>.Instance);
        var auditWriter = new WorkbenchAuditWriter(connections, new HttpContextAccessor(), provider, Options.Create(new AuditSettings()));
        var sprocs = new ControlledSprocInvoker(connections, NullLogger<ControlledSprocInvoker>.Instance);
        var engine = new EffectEngineInvoker(
            new EffectEngineSettings { Enabled = engineEnabled },
            new EffectPlanLoader(),
            EffectShadowRunner.BuildPipelineFor(ConnectionString),
            NullLogger<EffectEngineInvoker>.Instance);
        var workflow = new WorkflowEngine(connections, sprocs, auditWriter, provider, engine, NullLogger<WorkflowEngine>.Instance);
        return new WorkbenchApprovalService(
            connections, auditWriter, workflow, sprocs, engine, new WorkbenchIdempotency(),
            NullLogger<WorkbenchApprovalService>.Instance);
    }

    /// <summary>手写定义：只覆盖自动批核生效链用到的字段（表/主键/自动批核开关/引擎开关）。</summary>
    private static WorkbenchDefinition Definition(int moduleId, string masterTable, string[] pk, bool autoApprove, bool effectEngine) =>
        new(moduleId, $"测试模块 {moduleId}", masterTable, null, [], [], null, true, true, false,
            pk, string.Empty, false,
            AutoApprove: autoApprove,
            // effectEngine.enabled 是快照里的 JSON 段，EffectEngineEnabled 由它推导。
            EffectEngine: effectEngine ? JsonSerializer.SerializeToElement(new { enabled = true }) : null);

    /// <summary>取当前已发布快照的定义（含真实业务动作），引擎接管链需要它。</summary>
    private static async Task<WorkbenchDefinition> LoadPublishedDefinitionAsync(SqlConnection connection, int moduleId)
    {
        await using var command = new SqlCommand(
            "SELECT TOP 1 DEFINITION_JSON FROM dbo.WORKBENCH_DEFINITION_SNAPSHOT WHERE MODULE_ID=@Id AND IS_CURRENT=1;",
            connection);
        command.Parameters.Add("@Id", SqlDbType.Int).Value = moduleId;
        var json = await command.ExecuteScalarAsync() as string
            ?? throw new InvalidOperationException($"模块 {moduleId} 无已发布快照。");
        return JsonSerializer.Deserialize<WorkbenchDefinition>(json, WorkbenchDefinitionProvider.JsonOptions)!;
    }

    private static async Task ExecAsync(SqlConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        await using var command = new SqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<int> ScalarAsync(SqlConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        await using var command = new SqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }
        return Convert.ToInt32(await command.ExecuteScalarAsync() ?? 0);
    }

    [Fact]
    public async Task 无效果链模块的自动批核_翻转状态并审计_且重复触发幂等()
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await ExecAsync(connection, """
            DELETE FROM dbo.INV_BATCH_M WHERE BATCH_NO=@B;
            INSERT INTO dbo.INV_BATCH_M(BATCH_NO, PRO_NO, CONFIRM_TAG, CONFIRM_PERSON, CONFIRM_DATE)
            VALUES (@B, @P, 0, NULL, NULL);
            """, ("@B", BatchNo), ("@P", BatchProNo));
        try
        {
            // 1302 料件批号资料：自动批核、无批核过程、未启用效果引擎 → 纯状态翻转
            var service = CreateService(Connections(), engineEnabled: false);
            var definition = Definition(1302, "INV_BATCH_M", ["BATCH_NO", "PRO_NO"], autoApprove: true, effectEngine: false);
            // 审计表是追加式的：只统计本次用例新增的事件（历史 ADR12 行不动）
            var auditBaseline = await ScalarAsync(connection, "SELECT ISNULL(MAX(EVENT_ID),0) FROM dbo.AUDIT_EVENT;");

            var first = await service.AutoApproveAsync(definition, [BatchNo, BatchProNo], ConfirmPerson, "tester", CancellationToken.None);
            Assert.Equal(RecordAccessStatus.Ok, first.Status);
            Assert.Equal(1, await ScalarAsync(connection,
                "SELECT ISNULL(CONFIRM_TAG,0) FROM dbo.INV_BATCH_M WHERE BATCH_NO=@B AND PRO_NO=@P;",
                ("@B", BatchNo), ("@P", BatchProNo)));
            // 经办人 = 保存人（不是 SYSTEM 占位）
            Assert.Equal(ConfirmPerson, (await ScalarStringAsync(connection,
                "SELECT CONFIRM_PERSON FROM dbo.INV_BATCH_M WHERE BATCH_NO=@B AND PRO_NO=@P;",
                ("@B", BatchNo), ("@P", BatchProNo))).Trim());
            // 审计摘要保留"自动批核"的区分（状态列已不再承担该信息）
            Assert.Equal(1, await ScalarAsync(connection,
                "SELECT COUNT(*) FROM dbo.AUDIT_EVENT WHERE EVENT_ID > @Baseline AND MODULE_ID=1302 AND ACTION=N'APPROVE' AND SUMMARY=N'自动批核' AND RESOURCE_KEY LIKE @K;",
                ("@Baseline", auditBaseline), ("@K", $"%{BatchNo}%")));

            // 重复触发：仍是成功（幂等），且不产生第二条批核审计
            var second = await service.AutoApproveAsync(definition, [BatchNo, BatchProNo], ConfirmPerson, "tester", CancellationToken.None);
            Assert.Equal(RecordAccessStatus.Ok, second.Status);
            Assert.Equal(1, await ScalarAsync(connection,
                "SELECT COUNT(*) FROM dbo.AUDIT_EVENT WHERE EVENT_ID > @Baseline AND MODULE_ID=1302 AND ACTION=N'APPROVE' AND RESOURCE_KEY LIKE @K;",
                ("@Baseline", auditBaseline), ("@K", $"%{BatchNo}%")));
        }
        finally
        {
            await ExecAsync(connection, "DELETE FROM dbo.INV_BATCH_M WHERE BATCH_NO=@B;", ("@B", BatchNo));
        }
    }

    [Fact]
    public async Task 引擎接管的自动批核_被库存守卫拦截_状态还原且零残留()
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await ExecAsync(connection, """
            DELETE FROM dbo.INV_LOAN_D WHERE LOAN_TYPE=N'ADR12' AND LOAN_NO=@N;
            DELETE FROM dbo.INV_LOAN_M WHERE LOAN_TYPE=N'ADR12' AND LOAN_NO=@N;
            INSERT INTO dbo.INV_LOAN_M(LOAN_TYPE, LOAN_NO, LOAN_DATE, CONFIRM_TAG, CONFIRM_PERSON, CONFIRM_DATE)
            VALUES (N'ADR12', @N, CONVERT(datetime, N'2026-09-17', 120), 0, NULL, NULL);
            INSERT INTO dbo.INV_LOAN_D(LOAN_TYPE, LOAN_NO, SERIAL_NO, PRO_NO, DEPOT_ID, QTY)
            VALUES (N'ADR12', @N, 1, @P, N'CP', 5);
            """, ("@N", LoanNo), ("@P", NoStockProNo));
        try
        {
            var definition = await LoadPublishedDefinitionAsync(connection, 130108);
            Assert.True(definition.EffectEngineEnabled, "130108 快照应已开启效果引擎（本用例前提）");
            var service = CreateService(Connections(), engineEnabled: true);

            var result = await service.AutoApproveAsync(definition, ["ADR12", LoanNo], ConfirmPerson, "tester", CancellationToken.None);

            // 引擎在写入任何效果之前拦截（本夹具的品号不在主档，引擎的引用校验先挡住）
            Assert.NotEqual(RecordAccessStatus.Ok, result.Status);
            Assert.False(string.IsNullOrWhiteSpace(result.ErrorMessage), "拦截必须带回可读原因");
            Assert.Equal("WORKFLOW_FAILED", result.ErrorCode);
            // 状态还原：拦截发生在同一事务内，CONFIRM_TAG 保持未批核
            Assert.Equal(0, await ScalarAsync(connection,
                "SELECT ISNULL(CONFIRM_TAG,0) FROM dbo.INV_LOAN_M WHERE LOAN_TYPE=N'ADR12' AND LOAN_NO=@N;", ("@N", LoanNo)));
            // 效果零写入：既没有库存流水，也没有为该品号建余额行
            Assert.Equal(0, await ScalarAsync(connection,
                "SELECT COUNT(*) FROM dbo.INV_DEPOT_LOG WHERE MUTUALITY_TYPE=N'ADR12' AND MUTUALITY_NO=@N;", ("@N", LoanNo)));
            Assert.Equal(0, await ScalarAsync(connection,
                "SELECT COUNT(*) FROM dbo.INV_PRO_DEPOT WHERE PRO_NO=@P;", ("@P", NoStockProNo)));
        }
        finally
        {
            await ExecAsync(connection, """
                DELETE FROM dbo.INV_DEPOT_LOG WHERE MUTUALITY_TYPE=N'ADR12' AND MUTUALITY_NO=@N;
                DELETE FROM dbo.INV_LOAN_D WHERE LOAN_TYPE=N'ADR12' AND LOAN_NO=@N;
                DELETE FROM dbo.INV_LOAN_M WHERE LOAN_TYPE=N'ADR12' AND LOAN_NO=@N;
                DELETE FROM dbo.INV_PRO_DEPOT WHERE PRO_NO=@P;
                """, ("@N", LoanNo), ("@P", NoStockProNo));
        }
    }

    [Fact]
    public async Task 引擎模块的手工重复批核_仍返回状态冲突()
    {
        // 手工批核与保存触发的自动批核共用生效链，但重复触发的口径不同：
        // 用户再点一次批核要看到显式冲突（E2E 契约 WORKFLOW_STATE_CONFLICT），
        // 而不是像自动批核那样静默幂等成功。
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await ExecAsync(connection, """
            DELETE FROM dbo.PUR_RECEIVE_M WHERE RECEIVE_TYPE=N'ADR12' AND RECEIVE_NO=@N;
            INSERT INTO dbo.PUR_RECEIVE_M(RECEIVE_TYPE, RECEIVE_NO, RECEIVE_DATE, CONFIRM_TAG, CONFIRM_PERSON, CONFIRM_DATE)
            VALUES (N'ADR12', @N, CONVERT(datetime, N'2026-09-17', 120), 1, N'tester', SYSDATETIME());
            """, ("@N", ReceiveNo));
        try
        {
            var definition = await LoadPublishedDefinitionAsync(connection, 1607);
            var service = CreateService(Connections(), engineEnabled: true);

            var result = await service.WorkflowAsync(
                definition, ["ADR12", ReceiveNo], approve: true, "tester", "tester", null, CancellationToken.None);

            Assert.Equal("WORKFLOW_STATE_CONFLICT", result.ErrorCode);
        }
        finally
        {
            await ExecAsync(connection, "DELETE FROM dbo.PUR_RECEIVE_M WHERE RECEIVE_TYPE=N'ADR12' AND RECEIVE_NO=@N;",
                ("@N", ReceiveNo));
        }
    }

    [Fact]
    public async Task 校验目录拦截_批核被阻断且状态与效果都未落库()
    {
        // 1607 收料单的 APPROVE 期目录规则「收料数量超过采购单可收数量」：
        // 自造一张采购行（上限 1）与一张收 5 个的收料单，批核必须被目录校验拦住；
        // 因为校验闸与状态翻转在同一事务，CONFIRM_TAG 保持未批核（零残留）。
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await ExecAsync(connection, """
            DELETE FROM dbo.PUR_RECEIVE_D WHERE RECEIVE_TYPE=N'ADR12' AND RECEIVE_NO=@RN;
            DELETE FROM dbo.PUR_RECEIVE_M WHERE RECEIVE_TYPE=N'ADR12' AND RECEIVE_NO=@RN;
            DELETE FROM dbo.PUR_PURCHASE_D WHERE PURCHASE_TYPE=N'ADR12' AND PURCHASE_NO=@PN;
            DELETE FROM dbo.PUR_PURCHASE_M WHERE PURCHASE_TYPE=N'ADR12' AND PURCHASE_NO=@PN;
            INSERT INTO dbo.PUR_PURCHASE_M(PURCHASE_TYPE, PURCHASE_NO, PURCHASE_DATE, CONFIRM_TAG)
            VALUES (N'ADR12', @PN, CONVERT(datetime, N'2026-09-17', 120), 1);
            INSERT INTO dbo.PUR_PURCHASE_D(PURCHASE_TYPE, PURCHASE_NO, SERIAL_NO, PRO_NO, DEPOT_ID, QTY, RECEIVE_QTY)
            VALUES (N'ADR12', @PN, 1, N'ADR12AUTOPRO', N'CP', 1, 0);
            INSERT INTO dbo.PUR_RECEIVE_M(RECEIVE_TYPE, RECEIVE_NO, RECEIVE_DATE, CONFIRM_TAG, CONFIRM_PERSON, CONFIRM_DATE)
            VALUES (N'ADR12', @RN, CONVERT(datetime, N'2026-09-17', 120), 0, NULL, NULL);
            INSERT INTO dbo.PUR_RECEIVE_D(RECEIVE_TYPE, RECEIVE_NO, SERIAL_NO, PURCHASE_TYPE, PURCHASE_NO, PURCHASE_SERIAL_NO, PRO_NO, DEPOT_ID, QTY)
            VALUES (N'ADR12', @RN, 1, N'ADR12', @PN, 1, N'ADR12AUTOPRO', N'CP', 5);
            """, ("@RN", ReceiveNo), ("@PN", PurchaseNo));
        try
        {
            var definition = await LoadPublishedDefinitionAsync(connection, 1607);
            var service = CreateService(Connections(), engineEnabled: true);

            var result = await service.WorkflowAsync(
                definition, ["ADR12", ReceiveNo], approve: true, "tester", "tester", null, CancellationToken.None);

            Assert.Equal("BUSINESS_VALIDATION_FAILED", result.ErrorCode);
            Assert.Contains("收料数量超过采购单可收数量", result.ErrorMessage ?? string.Empty);
            // 状态未翻转、采购行已收量未被累加（效果零写入）
            Assert.Equal(0, await ScalarAsync(connection,
                "SELECT ISNULL(CONFIRM_TAG,0) FROM dbo.PUR_RECEIVE_M WHERE RECEIVE_TYPE=N'ADR12' AND RECEIVE_NO=@N;",
                ("@N", ReceiveNo)));
            Assert.Equal(0d, await ScalarDoubleAsync(connection,
                "SELECT ISNULL(RECEIVE_QTY,0) FROM dbo.PUR_PURCHASE_D WHERE PURCHASE_TYPE=N'ADR12' AND PURCHASE_NO=@N AND SERIAL_NO=1;",
                ("@N", PurchaseNo)));
        }
        finally
        {
            await ExecAsync(connection, """
                DELETE FROM dbo.PUR_RECEIVE_D WHERE RECEIVE_TYPE=N'ADR12' AND RECEIVE_NO=@RN;
                DELETE FROM dbo.PUR_RECEIVE_M WHERE RECEIVE_TYPE=N'ADR12' AND RECEIVE_NO=@RN;
                DELETE FROM dbo.PUR_PURCHASE_D WHERE PURCHASE_TYPE=N'ADR12' AND PURCHASE_NO=@PN;
                DELETE FROM dbo.PUR_PURCHASE_M WHERE PURCHASE_TYPE=N'ADR12' AND PURCHASE_NO=@PN;
                """, ("@RN", ReceiveNo), ("@PN", PurchaseNo));
        }
    }

    private static async Task<double> ScalarDoubleAsync(SqlConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        await using var command = new SqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }
        return Convert.ToDouble(await command.ExecuteScalarAsync() ?? 0d);
    }

    private static async Task<string> ScalarStringAsync(SqlConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        await using var command = new SqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }
        return await command.ExecuteScalarAsync() as string ?? string.Empty;
    }
}
