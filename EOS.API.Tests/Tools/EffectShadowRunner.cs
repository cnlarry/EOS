using System.Data;
using System.Reflection;
using System.Text;
using System.Text.Json;
using EOS.API.Data;
using EOS.API.Data.Effects;
using EOS.API.Data.Effects.ServiceEffectHandlers;
using EOS.API.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace EOS.API.Tests.Tools;

/// <summary>
/// Shadow comparison runner for module effect configs (approve event): executes the
/// legacy workflow stored procedure and the effect engine against the same record in
/// two independent rolled-back transactions, snapshots the affected tables inside each
/// transaction, then writes a normalized diff report to logs/shadow/. DB-only tool: it
/// never writes workspace configuration, never sends HTTP and never starts services.
/// Run with EOS_SHADOW_RUN=1 and optional EOS_SHADOW_MODULE / EOS_SHADOW_KEYS
/// ("RECEIVE_TYPE|RECEIVE_NO"). The published snapshot must already carry the
/// businessActions section and effectEngine.enabled=true (publish via the admin API
/// before the first run).
/// </summary>
[Trait("Category", "Tool")]
public sealed class EffectShadowRunner
{
    private const string SnapshotDirectory = "logs/shadow";
    private const string SqlDateFormat = "yyyy-MM-dd HH:mm:ss";

    private static readonly IReadOnlySet<string> QuantityColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "QTY", "SPARE_QTY", "BASE_QTY", "RECEIVE_QTY", "RECEIVE_SPARE_QTY", "NEED_QTY", "APPLY_QTY",
        "USED_QTY", "PURCHASE_QTY", "LOST_QTY", "REQUIRE_QTY", "INIT_QTY", "USEABLE_QTY", "IN_SUM",
        "OUT_SUM", "MUTUALITY_QTY", "IN_BUY_QTY", "MRP_QTY", "FINISHED_AMOUNT",
    };

    private static readonly IReadOnlySet<string> AmountColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "PRICE", "BASE_PRICE", "COST_PRICE", "AMOUNT", "COST_AMOUNT", "TAX_SUM", "AMOUNT_TAX",
        "CURR_RATE", "TAX_RATE", "MUTUALITY_PRICE", "MUTUALITY_CURR_RATE", "MUTUALITY_AMOUNT", "REBATE",
    };

    // Columns whose values are produced by SYSDATETIME()/GETDATE() during the event.
    // Per the comparison protocol they are compared only as "non-empty + relative order",
    // so any two non-null values on both sides are treated as equivalent.
    private static readonly IReadOnlySet<string> NowLikeColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "CONFIRM_DATE", "FINISHED_DATE", "LATELY_IN_DATE", "LATELY_OUT_DATE", "LAST_IN_DATE",
        "LAST_OUT_DATE", "LAST_TRADE_DATE", "CREATE_DATE", "LAST_UPDATE_DATE", "EFFECT_DATE",
        "BATCH_DATE", "MUTUALITY_DATE", "OCCURRED_AT", "CREATED_DATE",
    };

    private static readonly JsonSerializerOptions ReportJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private static readonly Lazy<string?> ConnectionString = new(ResolveConnectionString);
    private static readonly Lazy<string> RepoRoot = new(FindRepoRoot);

    [Fact]
    public async Task Approve_ShadowCompare_LegacySprocVsEffectEngine()
    {
        if (Environment.GetEnvironmentVariable("EOS_SHADOW_RUN") != "1")
        {
            return;
        }
        var moduleId = int.TryParse(Environment.GetEnvironmentVariable("EOS_SHADOW_MODULE"), out var module)
            ? module
            : 1607;
        var keys = Environment.GetEnvironmentVariable("EOS_SHADOW_KEYS");
        var runId = Environment.GetEnvironmentVariable("EOS_SHADOW_RUN_ID");
        var shadowEvent = Environment.GetEnvironmentVariable("EOS_SHADOW_EVENT") ?? "APPROVE_EFFECT";
        var failure = Environment.GetEnvironmentVariable("EOS_SHADOW_FAILURE") == "1";
        var options = new ShadowOptions(moduleId, shadowEvent, keys, runId, failure);

        var report = await RunAsync(options, Console.Out);
        Console.WriteLine($"shadow verdict={report.Summary.Verdict} run={report.RunId} " +
                          $"old={report.OldPath.Status} new={report.NewPath.Status} " +
                          $"diff={report.Summary.DiffCount} unnormalized={report.Summary.UnnormalizedDiffCount}");

        Assert.True(report.Summary.Verdict == "PASS",
            $"影子对拍未通过：{report.Summary.UnnormalizedDiffCount} 处未归一化差异，报告见 logs/shadow/{report.RunId}.json");
    }

    [Fact]
    public async Task FailureCase_EngineBlocksOverReceipt_WithZeroResidue()
    {
        if (Environment.GetEnvironmentVariable("EOS_SHADOW_RUN") != "1"
            || Environment.GetEnvironmentVariable("EOS_SHADOW_FAILURE") != "1")
        {
            return;
        }
        var moduleId = int.TryParse(Environment.GetEnvironmentVariable("EOS_SHADOW_MODULE"), out var module)
            ? module
            : 1607;
        var keys = Environment.GetEnvironmentVariable("EOS_SHADOW_KEYS");
        var options = new ShadowOptions(moduleId, "APPROVE_EFFECT", keys, RunId: null, Failure: true);
        var report = await RunAsync(options, Console.Out);

        // New-semantics failure gate: the engine must block before executing any action
        // (validation failure), leaving zero residue; the report keeps both side errors.
        Assert.Equal("blocked", report.NewPath.Status);
        Assert.NotEmpty(report.NewPath.Error ?? string.Empty);
        Assert.Empty(report.Tables);
        Console.WriteLine($"failure-case verdict={report.Summary.Verdict} old={report.OldPath.Status} new={report.NewPath.Status}");
    }

    /// <summary>Runs the shadow comparison and writes the JSON report; returns the report.</summary>
    public async Task<ShadowReport> RunAsync(ShadowOptions options, TextWriter log)
    {
        if (options.ModuleId != 1607)
        {
            throw new NotSupportedException("Effect shadow snapshot specs are implemented for module 1607 only.");
        }
        var deapprove = options.Event.Equals("DEAPPROVE", StringComparison.OrdinalIgnoreCase);
        if (!deapprove && !options.Event.Equals("APPROVE_EFFECT", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException($"Event '{options.Event}' is not supported (APPROVE_EFFECT / DEAPPROVE).");
        }
        if (ConnectionString.Value is null)
        {
            throw new InvalidOperationException("无法解析 EOS.API/appsettings.Development.json 连接串。");
        }

        var runId = options.RunId ?? NextRunId(options.ModuleId);
        await using var connection = new SqlConnection(ConnectionString.Value);
        await connection.OpenAsync();

        var (version, definitionJson) = await LoadCurrentSnapshotAsync(connection, options.ModuleId);
        var definition = JsonSerializer.Deserialize<WorkbenchDefinition>(definitionJson, WorkbenchDefinitionProvider.JsonOptions)
            ?? throw new InvalidOperationException("已发布快照无法反序列化。");
        if (definition.EffectEngine is not { ValueKind: JsonValueKind.Object } effectEngine
            || !effectEngine.TryGetProperty("enabled", out var enabled)
            || enabled.ValueKind != JsonValueKind.True)
        {
            throw new InvalidOperationException(
                $"模块 {options.ModuleId} 快照 v{version} 未开启效果引擎（effectEngine.enabled 缺失）。请先发布启用 EFFECT_ENGINE_TAG 的模块快照。");
        }
        if (definition.BusinessActions is not { ValueKind: JsonValueKind.Array } actions || actions.GetArrayLength() == 0)
        {
            throw new InvalidOperationException($"模块 {options.ModuleId} 快照 v{version} 缺少 businessActions 配置段。请先发布。");
        }
        if (definition.BusinessRule?.WorkflowSproc is not { Length: > 0 } workflowSproc)
        {
            throw new InvalidOperationException($"模块 {options.ModuleId} 无 WorkflowSproc，旧路径无法执行。");
        }

        var keys = await ResolveRecordKeysAsync(connection, options.ModuleId, options.Keys, deapprove, options.Failure);
        await log.WriteLineAsync($"shadow run={runId} module={options.ModuleId} event={options.Event} version={version} keys={string.Join("|", keys)} sproc={workflowSproc}");

        var definitionVersion = $"module-{options.ModuleId}-v{version}";
        var legacy = await RunLegacyPathAsync(connection, definition, workflowSproc, keys, deapprove, log);
        var engineResult = await RunEnginePathAsync(definition, keys, deapprove, log);
        var engine = engineResult.Status;
        List<ShadowTableDiff> tables;
        ShadowAudit audit;
        if (legacy.Status == "ok" && engine.Status == "ok")
        {
            (tables, audit) = CompareSnapshots(legacy.Snapshot!, engineResult.Snapshot!, deapprove);
        }
        else
        {
            tables = new List<ShadowTableDiff>();
            audit = new ShadowAudit(legacy.AuditCount, engineResult.AuditCount,
                legacy.AuditActions, engineResult.AuditActions);
        }

        var verdict = "PASS";
        var unnormalized = tables.Sum(table => table.Diffs.Count(diff => diff.Verdict == "diff" && !diff.Normalized));
        var diffCount = tables.Sum(table => table.Diffs.Count(diff => diff.Verdict == "diff"));
        if (legacy.Status == "ok" && engine.Status == "ok" && unnormalized == 0)
        {
            verdict = "PASS";
        }
        else if (legacy.Status == "blocked" && engine.Status == "blocked"
                 && (options.Failure || LegacySameBlock(legacy, engine)))
        {
            verdict = "PASS";
        }
        else
        {
            verdict = "FAIL";
        }

        var report = new ShadowReport(
            runId,
            options.ModuleId,
            definitionVersion,
            options.Event,
            keys,
            new ShadowPathStatus(legacy.Status, legacy.Error),
            engine,
            tables,
            audit,
            new ShadowSummary(verdict, diffCount, unnormalized));
        await WriteReportAsync(report);
        return report;
    }

    private static bool LegacySameBlock(LegacyPathResult legacy, ShadowPathStatus engine) =>
        legacy.Error is not null && engine.Error is not null
        && legacy.Error.Equals(engine.Error, StringComparison.Ordinal);

    private async Task<(int Version, string Json)> LoadCurrentSnapshotAsync(SqlConnection connection, int moduleId)
    {
        await using var command = new SqlCommand(
            "SELECT TOP 1 VERSION, DEFINITION_JSON FROM dbo.WORKBENCH_DEFINITION_SNAPSHOT " +
            "WHERE MODULE_ID=@ModuleId AND IS_CURRENT=1 ORDER BY VERSION DESC;", connection);
        command.Parameters.Add("@ModuleId", SqlDbType.Int).Value = moduleId;
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            throw new InvalidOperationException($"模块 {moduleId} 无已发布快照。");
        }
        return (reader.GetInt32(0), reader.GetString(1));
    }

    private async Task<IReadOnlyList<string>> ResolveRecordKeysAsync(
        SqlConnection connection, int moduleId, string? explicitKeys, bool deapprove, bool failure)
    {
        if (!string.IsNullOrWhiteSpace(explicitKeys))
        {
            return explicitKeys.Split('|', StringSplitOptions.TrimEntries);
        }
        if (failure)
        {
            // A receivable whose quantity exceeds the purchase-order remaining amount;
            // the effect engine validation must block it before any action runs.
            const string failureSql = """
                SELECT TOP 1 M.RECEIVE_TYPE, M.RECEIVE_NO
                FROM dbo.PUR_RECEIVE_M M
                WHERE ISNULL(M.CONFIRM_TAG,0)=0
                  AND NOT EXISTS (SELECT 1 FROM dbo.INV_DEPOT_LOG L
                                  WHERE LTRIM(RTRIM(L.MUTUALITY_TYPE))=LTRIM(RTRIM(M.RECEIVE_TYPE))
                                    AND LTRIM(RTRIM(L.MUTUALITY_NO))=LTRIM(RTRIM(M.RECEIVE_NO)))
                  AND EXISTS (
                      SELECT 1
                      FROM dbo.PUR_RECEIVE_D S
                      JOIN dbo.PUR_PURCHASE_D T
                        ON T.PURCHASE_TYPE=S.PURCHASE_TYPE AND T.PURCHASE_NO=S.PURCHASE_NO
                       AND T.SERIAL_NO=S.PURCHASE_SERIAL_NO
                      WHERE S.RECEIVE_TYPE=M.RECEIVE_TYPE AND S.RECEIVE_NO=M.RECEIVE_NO
                        AND (COALESCE(T.RECEIVE_QTY,0) + COALESCE(S.QTY,0) > COALESCE(T.QTY,0)
                          OR COALESCE(T.RECEIVE_SPARE_QTY,0) + COALESCE(S.SPARE_QTY,0) > COALESCE(T.SPARE_QTY,0)))
                ORDER BY M.RECEIVE_DATE DESC, M.RECEIVE_NO DESC;
                """;
            await using var failureCommand = new SqlCommand(failureSql, connection);
            await using var failureReader = await failureCommand.ExecuteReaderAsync();
            if (!await failureReader.ReadAsync())
            {
                throw new InvalidOperationException("未找到可触发失败分支的超收收料单。");
            }
            return new[] { failureReader.GetString(0).Trim(), failureReader.GetString(1).Trim() };
        }
        if (deapprove)
        {
            // Most recently confirmed receivable that produced inventory-log history and
            // whose lines can still be reversed against current stock (legacy deapprove
            // checks depot/batch sufficiency before rolling quantities back).
            const string deapproveSql = """
                SELECT TOP 1 M.RECEIVE_TYPE, M.RECEIVE_NO
                FROM dbo.PUR_RECEIVE_M M
                WHERE ISNULL(M.CONFIRM_TAG,0)=1
                  AND EXISTS (SELECT 1 FROM dbo.INV_DEPOT_LOG L
                              WHERE LTRIM(RTRIM(L.MUTUALITY_TYPE))=LTRIM(RTRIM(M.RECEIVE_TYPE))
                                AND LTRIM(RTRIM(L.MUTUALITY_NO))=LTRIM(RTRIM(M.RECEIVE_NO)))
                  AND NOT EXISTS (
                      SELECT 1
                      FROM (SELECT D.PRO_NO, D.DEPOT_ID,
                                   SUM(ISNULL(D.QTY,0) + ISNULL(D.SPARE_QTY,0)) QTY
                            FROM dbo.PUR_RECEIVE_D D
                            WHERE D.RECEIVE_TYPE=M.RECEIVE_TYPE AND D.RECEIVE_NO=M.RECEIVE_NO
                            GROUP BY D.PRO_NO, D.DEPOT_ID) X
                      JOIN dbo.INV_PRO_DEPOT S
                        ON S.PRO_NO=X.PRO_NO AND S.DEPOT_ID=X.DEPOT_ID
                      WHERE ISNULL(S.QTY,0) + 0.001 < X.QTY)
                ORDER BY ISNULL(M.CONFIRM_DATE, M.RECEIVE_DATE) DESC, M.RECEIVE_NO DESC;
                """;
            await using var deapproveCommand = new SqlCommand(deapproveSql, connection);
            await using var deapproveReader = await deapproveCommand.ExecuteReaderAsync();
            if (!await deapproveReader.ReadAsync())
            {
                throw new InvalidOperationException("未找到可解批对拍的已批核收料单（自动选单无结果）。");
            }
            return new[] { deapproveReader.GetString(0).Trim(), deapproveReader.GetString(1).Trim() };
        }

        // Pick the most recent receivable that is still unconfirmed, has no residual
        // inventory-log history, and whose purchase lines (when present) pass the engine
        // quantity validation (row-level, no tolerance, spare quantity included).
        const string sql = """
            SELECT TOP 1 M.RECEIVE_TYPE, M.RECEIVE_NO
            FROM dbo.PUR_RECEIVE_M M
            WHERE ISNULL(M.CONFIRM_TAG,0)=0
              AND NOT EXISTS (SELECT 1 FROM dbo.INV_DEPOT_LOG L
                              WHERE LTRIM(RTRIM(L.MUTUALITY_TYPE))=LTRIM(RTRIM(M.RECEIVE_TYPE))
                                AND LTRIM(RTRIM(L.MUTUALITY_NO))=LTRIM(RTRIM(M.RECEIVE_NO)))
              AND EXISTS (SELECT 1 FROM dbo.PUR_RECEIVE_D D
                          WHERE D.RECEIVE_TYPE=M.RECEIVE_TYPE AND D.RECEIVE_NO=M.RECEIVE_NO)
              AND NOT EXISTS (
                  SELECT 1
                  FROM dbo.PUR_RECEIVE_D S
                  JOIN dbo.PUR_PURCHASE_D T
                    ON T.PURCHASE_TYPE=S.PURCHASE_TYPE AND T.PURCHASE_NO=S.PURCHASE_NO
                   AND T.SERIAL_NO=S.PURCHASE_SERIAL_NO
                  WHERE S.RECEIVE_TYPE=M.RECEIVE_TYPE AND S.RECEIVE_NO=M.RECEIVE_NO
                    AND (COALESCE(T.RECEIVE_QTY,0) + COALESCE(S.QTY,0) > COALESCE(T.QTY,0)
                      OR COALESCE(T.RECEIVE_SPARE_QTY,0) + COALESCE(S.SPARE_QTY,0) > COALESCE(T.SPARE_QTY,0)))
            ORDER BY M.RECEIVE_DATE DESC, M.RECEIVE_NO DESC;
            """;
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            throw new InvalidOperationException("未找到可对拍的未批核收料单（自动选单无结果）。");
        }
        return new[] { reader.GetString(0).Trim(), reader.GetString(1).Trim() };
    }

    private async Task<LegacyPathResult> RunLegacyPathAsync(
        SqlConnection connection,
        WorkbenchDefinition definition,
        string workflowSproc,
        IReadOnlyList<string> keys,
        bool deapprove,
        TextWriter log)
    {
        await using var scoped = new SqlConnection(ConnectionString.Value);
        await scoped.OpenAsync();
        var transaction = (SqlTransaction)await scoped.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        try
        {
            await SetConfirmAsync(scoped, transaction, keys, deapprove);
            var keyCondition = BuildKeyCondition(keys);
            await using var command = new SqlCommand(workflowSproc, scoped, transaction)
            {
                CommandType = CommandType.StoredProcedure,
            };
            command.Parameters.Add("@key_value", SqlDbType.VarChar, 200).Value = keyCondition;
            command.Parameters.Add("@approve_tag", SqlDbType.Int).Value = deapprove ? -1 : 1;
            var message = command.Parameters.Add("@msg", SqlDbType.VarChar, 8000);
            message.Direction = ParameterDirection.Output;
            var rc = command.Parameters.Add("@rc", SqlDbType.Int);
            rc.Direction = ParameterDirection.ReturnValue;
            await command.ExecuteNonQueryAsync();
            var success = rc.Value is int code && code == 1;
            var error = success ? null : (message.Value as string);
            if (!success)
            {
                await transaction.RollbackAsync();
                return new LegacyPathResult("blocked", error ?? "旧批核存储过程返回失败。", null, 0, Array.Empty<string>());
            }
            var snapshot = await CaptureSnapshotAsync(scoped, transaction, keys);
            await transaction.RollbackAsync();
            await log.WriteLineAsync($"legacy path ok rows={snapshot.Rows.Sum(entry => entry.Value.Count)}");
            if (Environment.GetEnvironmentVariable("EOS_SHADOW_DEBUG") == "1")
            {
                DumpSnapshot(log, "legacy", snapshot);
            }
            return new LegacyPathResult("ok", null, snapshot, snapshot.AuditCount, snapshot.AuditActions);
        }
        catch (Exception exception)
        {
            try
            {
                await transaction.RollbackAsync();
            }
            catch
            {
                // transaction may already be rolled back by the failing statement
            }
            if (Environment.GetEnvironmentVariable("EOS_SHADOW_DEBUG") == "1")
            {
                await log.WriteLineAsync("legacy path exception: " + exception);
            }
            return new LegacyPathResult("blocked", exception.Message, null, 0, Array.Empty<string>());
        }
    }

    private async Task<EnginePathResult> RunEnginePathAsync(
        WorkbenchDefinition definition,
        IReadOnlyList<string> keys,
        bool deapprove,
        TextWriter log)
    {
        await using var connection = new SqlConnection(ConnectionString.Value);
        await connection.OpenAsync();
        var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        try
        {
            await SetConfirmAsync(connection, transaction, keys, deapprove);
            var plan = new EffectPlanLoader().Load(definition);
            if (Environment.GetEnvironmentVariable("EOS_SHADOW_DEBUG") == "1")
            {
                var executor = new EffectFormulaExecutor();
                foreach (var action in plan.Actions)
                {
                    foreach (var op in action.Ops)
                    {
                        try
                        {
                            var (sql, _) = executor.BuildUpdate(op, plan, keys);
                            await log.WriteLineAsync($"op sql seq={action.Seq} op={op.OpSeq} table={op.TargetTable}: {sql}");
                        }
                        catch (Exception ex)
                        {
                            await log.WriteLineAsync($"op build failed seq={action.Seq} op={op.OpSeq}: {ex.Message}");
                        }
                    }
                }
            }
            var pipeline = BuildPipeline();
            await pipeline.ExecuteWithinTransactionAsync(
                connection, transaction, plan, deapprove ? EffectEvent.Deapprove : EffectEvent.ApproveEffect,
                string.Join(',', keys), "shadow", CancellationToken.None, keys);
            var snapshot = await CaptureSnapshotAsync(connection, transaction, keys);
            await transaction.RollbackAsync();
            await log.WriteLineAsync($"engine path ok rows={snapshot.Rows.Sum(entry => entry.Value.Count)}");
            if (Environment.GetEnvironmentVariable("EOS_SHADOW_DEBUG") == "1")
            {
                DumpSnapshot(log, "engine", snapshot);
            }
            return new EnginePathResult(new ShadowPathStatus("ok", null), snapshot, snapshot.AuditCount, snapshot.AuditActions);
        }
        catch (Exception exception)
        {
            try
            {
                await transaction.RollbackAsync();
            }
            catch
            {
                // transaction may already be rolled back by the failing statement
            }
            if (Environment.GetEnvironmentVariable("EOS_SHADOW_DEBUG") == "1")
            {
                await log.WriteLineAsync("engine path exception: " + exception);
            }
            return new EnginePathResult(new ShadowPathStatus("blocked", exception.Message), null, 0, Array.Empty<string>());
        }
    }

    private EffectPipeline BuildPipeline()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:ErpDatabase"] = ConnectionString.Value,
            })
            .Build();
        var connections = new DbConnectionFactory(configuration);
        var provider = new WorkbenchDefinitionProvider(connections, NullLogger<WorkbenchDefinitionProvider>.Instance);
        var auditSettings = Options.Create(new AuditSettings());
        var auditWriter = new WorkbenchAuditWriter(connections, new HttpContextAccessor(), provider, auditSettings);
        return new EffectPipeline(
            connections,
            new EffectPlanLoader(),
            new EffectFormulaExecutor(),
            new IEffectServiceHandler[] { new InventoryMoveHandler(new EffectPhysicalColumns()) },
            new EffectValidationExecutor(),
            auditWriter,
            NullLogger<EffectPipeline>.Instance);
    }

    private static async Task SetConfirmAsync(
        SqlConnection connection, SqlTransaction transaction, IReadOnlyList<string> keys, bool deapprove)
    {
        const string sql = """
            UPDATE dbo.PUR_RECEIVE_M
            SET CONFIRM_PERSON=@Person, CONFIRM_DATE=SYSDATETIME(), CONFIRM_TAG=@ConfirmTag
            WHERE CONFIRM_TAG=@ExpectTag AND RECEIVE_TYPE=@ReceiveType AND RECEIVE_NO=@ReceiveNo;
            """;
        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@Person", SqlDbType.NVarChar, 50).Value = "shadow";
        command.Parameters.Add("@ConfirmTag", SqlDbType.Bit).Value = deapprove ? false : true;
        command.Parameters.Add("@ExpectTag", SqlDbType.Bit).Value = deapprove ? true : false;
        command.Parameters.Add("@ReceiveType", SqlDbType.NVarChar, 20).Value = keys[0];
        command.Parameters.Add("@ReceiveNo", SqlDbType.NVarChar, 30).Value = keys[1];
        if (await command.ExecuteNonQueryAsync() == 0)
        {
            throw new InvalidOperationException(deapprove
                ? "单据不存在或未批核，无法执行对拍解批。"
                : "单据不存在或已批核，无法执行对拍批核。");
        }
    }

    private static string BuildKeyCondition(IReadOnlyList<string> keys) =>
        $"[RECEIVE_TYPE]='{Escape(keys[0])}' AND [RECEIVE_NO]='{Escape(keys[1])}'";

    private static string Escape(string value) => value.Replace("'", "''");

    private static async Task<ShadowSnapshot> CaptureSnapshotAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        IReadOnlyList<string> keys)
    {
        var master = await ReadMasterContextAsync(connection, transaction, keys);
        var details = await ReadDetailContextAsync(connection, transaction, keys);
        var specs = BuildTableSpecs(master, details);
        var rows = new Dictionary<string, List<ShadowRow>>(StringComparer.Ordinal);
        foreach (var spec in specs)
        {
            var captured = await ReadRowsAsync(connection, transaction, spec);
            if (captured.Count > 0)
            {
                rows[spec.Table] = captured.ToList();
            }
        }
        var (auditCount, auditActions) = await ReadAuditDeltaAsync(connection, transaction, keys);
        return new ShadowSnapshot(rows, auditCount, auditActions);
    }

    private static async Task<MasterContext> ReadMasterContextAsync(
        SqlConnection connection, SqlTransaction transaction, IReadOnlyList<string> keys)
    {
        const string sql = """
            SELECT M.RECEIVE_DATE, LTRIM(RTRIM(ISNULL(M.SUPPLIER_ID,'')))
            FROM dbo.PUR_RECEIVE_M M
            WHERE M.RECEIVE_TYPE=@ReceiveType AND M.RECEIVE_NO=@ReceiveNo;
            """;
        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@ReceiveType", SqlDbType.NVarChar, 20).Value = keys[0];
        command.Parameters.Add("@ReceiveNo", SqlDbType.NVarChar, 30).Value = keys[1];
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            throw new InvalidOperationException("收料主表行不存在。");
        }
        var date = reader.IsDBNull(0) ? null : (DateTime?)reader.GetDateTime(0);
        var supplier = reader.GetString(1);
        return new MasterContext(date, string.IsNullOrWhiteSpace(supplier) ? null : supplier, keys[0], keys[1]);
    }

    private static async Task<IReadOnlyList<DetailRow>> ReadDetailContextAsync(
        SqlConnection connection, SqlTransaction transaction, IReadOnlyList<string> keys)
    {
        const string sql = """
            SELECT LTRIM(RTRIM(ISNULL(D.PURCHASE_TYPE,''))), LTRIM(RTRIM(ISNULL(D.PURCHASE_NO,''))),
                   D.PURCHASE_SERIAL_NO, LTRIM(RTRIM(ISNULL(D.ORDER_TYPE,''))), LTRIM(RTRIM(ISNULL(D.ORDER_NO,''))),
                   LTRIM(RTRIM(ISNULL(D.PRO_NO,''))), LTRIM(RTRIM(ISNULL(D.DEPOT_ID,''))),
                   LTRIM(RTRIM(ISNULL(D.BATCH_NO,''))), D.SERIAL_NO
            FROM dbo.PUR_RECEIVE_D D
            WHERE D.RECEIVE_TYPE=@ReceiveType AND D.RECEIVE_NO=@ReceiveNo
            ORDER BY D.SERIAL_NO;
            """;
        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@ReceiveType", SqlDbType.NVarChar, 20).Value = keys[0];
        command.Parameters.Add("@ReceiveNo", SqlDbType.NVarChar, 30).Value = keys[1];
        await using var reader = await command.ExecuteReaderAsync();
        var result = new List<DetailRow>();
        while (await reader.ReadAsync())
        {
            result.Add(new DetailRow(
                reader.GetString(0), reader.GetString(1),
                reader.IsDBNull(2) ? null : (int?)reader.GetInt16(2),
                reader.GetString(3), reader.GetString(4),
                reader.GetString(5), reader.GetString(6), reader.GetString(7),
                reader.IsDBNull(8) ? null : (int?)reader.GetInt16(8)));
        }
        return result;
    }

    private static IReadOnlyList<TableSpec> BuildTableSpecs(MasterContext master, IReadOnlyList<DetailRow> details)
    {
        var specs = new List<TableSpec>
        {
            new("PUR_RECEIVE_M", new[] { "RECEIVE_TYPE", "RECEIVE_NO" },
                "@rt=RECEIVE_TYPE AND @rn=RECEIVE_NO", new[] { new SqlParameter("@rt", master.ReceiveType), new SqlParameter("@rn", master.ReceiveNo) }),
            new("PUR_RECEIVE_D", new[] { "RECEIVE_TYPE", "RECEIVE_NO", "SERIAL_NO" },
                "@rt=RECEIVE_TYPE AND @rn=RECEIVE_NO", new[] { new SqlParameter("@rt", master.ReceiveType), new SqlParameter("@rn", master.ReceiveNo) }),
        };

        var purchaseKeys = details
            .Where(row => row.PurchaseType.Length > 0 && row.PurchaseNo.Length > 0)
            .Select(row => (row.PurchaseType, row.PurchaseNo))
            .Distinct()
            .ToArray();
        if (purchaseKeys.Length > 0)
        {
            specs.Add(BuildValuesSpec("PUR_PURCHASE_M", new[] { "PURCHASE_TYPE", "PURCHASE_NO" },
                purchaseKeys, "PURCHASE_TYPE", "PURCHASE_NO"));
        }

        var purchaseLineKeys = details
            .Where(row => row.PurchaseType.Length > 0 && row.PurchaseNo.Length > 0 && row.PurchaseSerialNo is not null)
            .Select(row => (row.PurchaseType, row.PurchaseNo, row.PurchaseSerialNo!.Value))
            .Distinct()
            .ToArray();
        if (purchaseLineKeys.Length > 0)
        {
            specs.Add(BuildValuesSpec("PUR_PURCHASE_D",
                new[] { "PURCHASE_TYPE", "PURCHASE_NO", "SERIAL_NO" },
                purchaseLineKeys, "PURCHASE_TYPE", "PURCHASE_NO", "SERIAL_NO"));
        }

        var orderKeys = details
            .Where(row => row.OrderType.Length > 0 && row.OrderNo.Length > 0 && row.ProNo.Length > 0)
            .Select(row => (row.OrderType, row.OrderNo, row.ProNo))
            .Distinct()
            .ToArray();
        if (orderKeys.Length > 0)
        {
            specs.Add(BuildValuesSpec("COP_ORDER_MORE", new[] { "ORDER_TYPE", "ORDER_NO", "PRO_NO" },
                orderKeys, "ORDER_TYPE", "ORDER_NO", "PRO_NO"));
        }

        var productKeys = details.Select(row => row.ProNo).Where(pro => pro.Length > 0).Distinct().ToArray();
        if (productKeys.Length > 0)
        {
            specs.Add(new("PRODUCT", new[] { "PRO_NO" }, InClause("PRO_NO", productKeys), productKeys.Select((value, index) => new SqlParameter("@p" + index, value)).ToArray()));
        }

        if (master.SupplierId is not null)
        {
            specs.Add(new("SUPPLIER", new[] { "SUPPLIER_ID" }, "SUPPLIER_ID=@sid", new[] { new SqlParameter("@sid", master.SupplierId) }));
        }

        var depotKeys = details
            .Where(row => row.ProNo.Length > 0 && row.DepotId.Length > 0)
            .Select(row => (row.ProNo, row.DepotId))
            .Distinct()
            .ToArray();
        if (depotKeys.Length > 0)
        {
            specs.Add(BuildValuesSpec("INV_PRO_DEPOT", new[] { "PRO_NO", "DEPOT_ID" }, depotKeys, "PRO_NO", "DEPOT_ID"));
        }

        var batchKeys = details
            .Where(row => row.ProNo.Length > 0 && row.BatchNo.Length > 0)
            .Select(row => (row.BatchNo, row.ProNo))
            .Distinct()
            .ToArray();
        if (batchKeys.Length > 0)
        {
            specs.Add(BuildValuesSpec("INV_BATCH_M", new[] { "BATCH_NO", "PRO_NO" }, batchKeys, "BATCH_NO", "PRO_NO"));
            if (master.ReceiveDate is not null)
            {
                specs.Add(BuildBatchDetailSpec(master.ReceiveDate.Value, master.ReceiveType, master.ReceiveNo, batchKeys));
            }
        }

        if (master.ReceiveDate is not null
            && details.Any(row => row.SerialNo.HasValue)
            && details.Any(row => row.ProNo.Length > 0)
            && details.Any(row => row.DepotId.Length > 0))
        {
            specs.Add(BuildLogSpec(master, details));
        }
        return specs;
    }

    private static TableSpec BuildLogSpec(MasterContext master, IReadOnlyList<DetailRow> details)
    {
        var date = master.ReceiveDate!.Value.ToString(SqlDateFormat);
        var serials = details.Select(row => row.SerialNo).Where(value => value.HasValue)
            .Select(value => (object)value!.Value).Distinct().ToArray();
        var pros = details.Select(row => row.ProNo).Where(value => value.Length > 0)
            .Select(value => (object)value).Distinct().ToArray();
        var depots = details.Select(row => row.DepotId).Where(value => value.Length > 0)
            .Select(value => (object)value).Distinct().ToArray();
        var parameters = new List<SqlParameter>
        {
            new("@rdate", date),
            new("@rt", master.ReceiveType),
            new("@rn", master.ReceiveNo),
        };
        var builder = new StringBuilder("CONVERT(nvarchar(19),MUTUALITY_DATE,120)=@rdate")
            .Append(" AND ((MUTUALITY_TYPE='1607' AND MUTUALITY_NO=@rt) OR (MUTUALITY_TYPE=@rt AND MUTUALITY_NO=@rn))")
            .Append(" AND ").Append(ParameterizedIn("PRO_NO", pros, "lp", parameters))
            .Append(" AND ").Append(ParameterizedIn("DEPOT_ID", depots, "ld", parameters))
            .Append(" AND ").Append(ParameterizedIn("MUTUALITY_SERIAL_NO", serials, "ls", parameters));
        return new TableSpec(
            "INV_DEPOT_LOG",
            new[] { "PRO_NO", "MUTUALITY_DATE", "IN_OUT", "MUTUALITY_SERIAL_NO", "DEPOT_ID", "BATCH_NO" },
            builder.ToString(),
            parameters);
    }

    private static TableSpec BuildBatchDetailSpec(
        DateTime receiveDate,
        string receiveType,
        string receiveNo,
        IReadOnlyList<(string First, string Second)> batchKeys)
    {
        var date = receiveDate.ToString(SqlDateFormat);
        var parameters = new List<SqlParameter>
        {
            new("@rdate", date),
            new("@rt", receiveType),
            new("@rn", receiveNo),
        };
        var pairFragment = PairExistsFragment(batchKeys, "BATCH_NO", "PRO_NO", "bk", parameters);
        var builder = new StringBuilder("CONVERT(nvarchar(19),BATCH_DATE,120)=@rdate")
            .Append(" AND ((BATCH_ORDER_TYPE='1607' AND BATCH_ORDER_NO=@rt) OR (BATCH_ORDER_TYPE=@rt AND BATCH_ORDER_NO=@rn))")
            .Append(" AND ").Append(pairFragment);
        return new TableSpec(
            "INV_BATCH_D",
            new[] { "BATCH_NO", "PRO_NO", "BATCH_SERIAL_NO", "DEPOT_ID", "EFFECT_DEPOT" },
            builder.ToString(),
            parameters);
    }

    private static string ParameterizedIn(
        string column, IReadOnlyList<object> values, string prefix, ICollection<SqlParameter> parameters)
    {
        var names = new string[values.Count];
        for (var index = 0; index < values.Count; index++)
        {
            var name = "@" + prefix + index;
            names[index] = name;
            parameters.Add(new SqlParameter(name, values[index]));
        }
        return column + " IN (" + string.Join(",", names) + ")";
    }

    private static string PairExistsFragment(
        IReadOnlyList<(string First, string Second)> pairs,
        string firstColumn,
        string secondColumn,
        string prefix,
        ICollection<SqlParameter> parameters)
    {
        var builder = new StringBuilder("EXISTS (SELECT 1 FROM (SELECT ");
        for (var index = 0; index < pairs.Count; index++)
        {
            var firstName = "@" + prefix + index + "a";
            var secondName = "@" + prefix + index + "b";
            if (index > 0)
            {
                builder.Append(" UNION ALL SELECT ");
                builder.Append(firstName).Append(", ").Append(secondName);
            }
            else
            {
                builder.Append(firstName).Append(" FC, ").Append(secondName).Append(" SC");
            }
            parameters.Add(new SqlParameter(firstName, pairs[index].First));
            parameters.Add(new SqlParameter(secondName, pairs[index].Second));
        }
        builder.Append(") V WHERE V.FC=").Append(firstColumn)
            .Append(" AND V.SC=").Append(secondColumn).Append(')');
        return builder.ToString();
    }

    private static TableSpec BuildValuesSpec(
        string table,
        IReadOnlyList<string> keyColumns,
        IReadOnlyList<(string First, string Second)> pairs,
        string firstColumn,
        string secondColumn)
    {
        var builder = new StringBuilder("EXISTS (SELECT 1 FROM (SELECT ");
        var parameters = new List<SqlParameter>();
        for (var index = 0; index < pairs.Count; index++)
        {
            var firstName = "@v" + index + "a";
            var secondName = "@v" + index + "b";
            if (index > 0)
            {
                builder.Append(" UNION ALL SELECT ");
            }
            else
            {
                builder.Append(firstName).Append(" FC, ").Append(secondName).Append(" SC");
                parameters.Add(new SqlParameter(firstName, pairs[index].First));
                parameters.Add(new SqlParameter(secondName, pairs[index].Second));
                continue;
            }
            builder.Append(firstName).Append(", ").Append(secondName);
            parameters.Add(new SqlParameter(firstName, pairs[index].First));
            parameters.Add(new SqlParameter(secondName, pairs[index].Second));
        }
        builder.Append(") V WHERE V.FC=").Append(firstColumn).Append(" AND V.SC=").Append(secondColumn).Append(')');
        return new TableSpec(table, keyColumns, builder.ToString(), parameters);
    }

    private static TableSpec BuildValuesSpec(
        string table,
        IReadOnlyList<string> keyColumns,
        IReadOnlyList<(string First, string Second, int Third)> triples,
        string firstColumn,
        string secondColumn,
        string thirdColumn)
    {
        var builder = new StringBuilder("EXISTS (SELECT 1 FROM (SELECT ");
        var parameters = new List<SqlParameter>();
        for (var index = 0; index < triples.Count; index++)
        {
            var firstName = "@v" + index + "a";
            var secondName = "@v" + index + "b";
            var thirdName = "@v" + index + "c";
            if (index > 0)
            {
                builder.Append(" UNION ALL SELECT ");
                builder.Append(firstName).Append(", ").Append(secondName).Append(", ").Append(thirdName);
            }
            else
            {
                builder.Append(firstName).Append(" FC, ").Append(secondName).Append(" SC, ").Append(thirdName).Append(" TC");
            }
            parameters.Add(new SqlParameter(firstName, triples[index].First));
            parameters.Add(new SqlParameter(secondName, triples[index].Second));
            parameters.Add(new SqlParameter(thirdName, triples[index].Third));
        }
        builder.Append(") V WHERE V.FC=").Append(firstColumn)
            .Append(" AND V.SC=").Append(secondColumn).Append(" AND V.TC=").Append(thirdColumn).Append(')');
        return new TableSpec(table, keyColumns, builder.ToString(), parameters);
    }

    private static TableSpec BuildValuesSpec(
        string table,
        IReadOnlyList<string> keyColumns,
        IReadOnlyList<(string First, string Second, string Third)> triples,
        string firstColumn,
        string secondColumn,
        string thirdColumn)
    {
        var builder = new StringBuilder("EXISTS (SELECT 1 FROM (SELECT ");
        var parameters = new List<SqlParameter>();
        for (var index = 0; index < triples.Count; index++)
        {
            var firstName = "@v" + index + "a";
            var secondName = "@v" + index + "b";
            var thirdName = "@v" + index + "c";
            if (index > 0)
            {
                builder.Append(" UNION ALL SELECT ");
                builder.Append(firstName).Append(", ").Append(secondName).Append(", ").Append(thirdName);
            }
            else
            {
                builder.Append(firstName).Append(" FC, ").Append(secondName).Append(" SC, ").Append(thirdName).Append(" TC");
            }
            parameters.Add(new SqlParameter(firstName, triples[index].First));
            parameters.Add(new SqlParameter(secondName, triples[index].Second));
            parameters.Add(new SqlParameter(thirdName, triples[index].Third));
        }
        builder.Append(") V WHERE V.FC=").Append(firstColumn)
            .Append(" AND V.SC=").Append(secondColumn).Append(" AND V.TC=").Append(thirdColumn).Append(')');
        return new TableSpec(table, keyColumns, builder.ToString(), parameters);
    }

    private static string InClause(string column, IReadOnlyList<string> values)
    {
        var builder = new StringBuilder(column).Append(" IN (");
        for (var index = 0; index < values.Count; index++)
        {
            if (index > 0)
            {
                builder.Append(',');
            }
            builder.Append("@p").Append(index);
        }
        builder.Append(')');
        return builder.ToString();
    }

    private static async Task<IReadOnlyList<ShadowRow>> ReadRowsAsync(
        SqlConnection connection, SqlTransaction transaction, TableSpec spec)
    {
        var columns = await ReadPhysicalColumnsAsync(connection, transaction, spec.Table);
        if (columns.Count == 0)
        {
            return Array.Empty<ShadowRow>();
        }
        var select = "SELECT " + string.Join(",", columns.Select(column => "[" + column + "]"))
            + " FROM dbo.[" + spec.Table + "] WHERE " + spec.FilterSql;
        await using var command = new SqlCommand(select, connection, transaction);
        foreach (var parameter in spec.Parameters)
        {
            command.Parameters.Add(new SqlParameter(parameter.ParameterName, parameter.Value));
        }
        try
        {
            await using var reader = await command.ExecuteReaderAsync();
            var result = new List<ShadowRow>();
            while (await reader.ReadAsync())
            {
                var cells = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                for (var index = 0; index < columns.Count; index++)
                {
                    cells[columns[index]] = NormalizeValue(reader.GetValue(index));
                }
                var key = string.Join("|", spec.KeyColumns.Select(column => cells.TryGetValue(column, out var value) ? ToKeyPart(value) : string.Empty));
                result.Add(new ShadowRow(key, cells));
            }
            return result;
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                $"影子快照查询失败 table={spec.Table} sql={select} error={exception.Message}", exception);
        }
    }

    private static async Task<IReadOnlyList<string>> ReadPhysicalColumnsAsync(
        SqlConnection connection, SqlTransaction transaction, string table)
    {
        const string sql = """
            SELECT c.name
            FROM sys.objects o
            JOIN sys.columns c ON c.object_id=o.object_id
            WHERE o.type='U' AND SCHEMA_NAME(o.schema_id)=N'dbo' AND o.name=@Table AND c.is_computed=0
            ORDER BY c.column_id;
            """;
        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@Table", SqlDbType.NVarChar, 128).Value = table;
        await using var reader = await command.ExecuteReaderAsync();
        var result = new List<string>();
        while (await reader.ReadAsync())
        {
            result.Add(reader.GetString(0));
        }
        return result;
    }

    private static async Task<(int Count, string[] Actions)> ReadAuditDeltaAsync(
        SqlConnection connection, SqlTransaction transaction, IReadOnlyList<string> keys)
    {
        var baseline = await ReadAuditMaxIdAsync(connection, transaction);
        const string sql = """
            SELECT ACTION, COUNT(*)
            FROM dbo.AUDIT_EVENT WITH (NOLOCK)
            WHERE EVENT_ID > @Baseline AND MODULE_ID=@ModuleId AND RESOURCE_KEY=@ResourceKey
            GROUP BY ACTION ORDER BY ACTION;
            """;
        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@Baseline", SqlDbType.BigInt).Value = baseline;
        command.Parameters.Add("@ModuleId", SqlDbType.Int).Value = 1607;
        command.Parameters.Add("@ResourceKey", SqlDbType.NVarChar, 400).Value = string.Join(',', keys);
        await using var reader = await command.ExecuteReaderAsync();
        var actions = new List<string>();
        var count = 0;
        while (await reader.ReadAsync())
        {
            actions.Add(reader.GetString(0));
            count += reader.GetInt32(1);
        }
        return (count, actions.ToArray());
    }

    private static async Task<long> ReadAuditMaxIdAsync(SqlConnection connection, SqlTransaction transaction)
    {
        await using var command = new SqlCommand("SELECT ISNULL(MAX(EVENT_ID),0) FROM dbo.AUDIT_EVENT WITH (NOLOCK);", connection, transaction);
        return Convert.ToInt64(await command.ExecuteScalarAsync() ?? 0L);
    }

    private static (List<ShadowTableDiff> Tables, ShadowAudit Audit) CompareSnapshots(
        ShadowSnapshot legacy, ShadowSnapshot engine, bool deapprove)
    {
        var tables = new List<ShadowTableDiff>();
        foreach (var table in legacy.Rows.Keys.Union(engine.Rows.Keys, StringComparer.Ordinal).OrderBy(name => name, StringComparer.Ordinal))
        {
            var oldRows = legacy.Rows.TryGetValue(table, out var oldList)
                ? oldList.ToList()
                : new List<ShadowRow>();
            var newRows = engine.Rows.TryGetValue(table, out var newList)
                ? newList.ToList()
                : new List<ShadowRow>();
            if (deapprove && table == "INV_DEPOT_LOG")
            {
                // The engine mirrors deapproved movements by writing a compensating row
                // (direction flipped, quantity negated) instead of deleting history.
                // Cancel each paired original/reverse row so the remaining set can be
                // compared with the legacy post-delete set.
                newRows = CancelReverseRows(newRows, "IN_OUT", "QTY",
                    new[] { "PRO_NO", "MUTUALITY_DATE", "MUTUALITY_TYPE", "MUTUALITY_NO",
                        "MUTUALITY_SERIAL_NO", "DEPOT_ID", "BATCH_NO" });
            }
            else if (deapprove && table == "INV_BATCH_D")
            {
                newRows = CancelReverseRows(newRows, "EFFECT_DEPOT", "QTY",
                    new[] { "BATCH_NO", "PRO_NO", "BATCH_SERIAL_NO", "DEPOT_ID",
                        "BATCH_ORDER_TYPE", "BATCH_ORDER_NO", "BATCH_DATE" });
            }
            tables.Add(CompareTable(table, oldRows, newRows));
        }
        var audit = new ShadowAudit(legacy.AuditCount, engine.AuditCount, legacy.AuditActions, engine.AuditActions);
        return (tables, audit);
    }

    private static List<ShadowRow> CancelReverseRows(
        IReadOnlyList<ShadowRow> rows,
        string directionColumn,
        string quantityColumn,
        IReadOnlyList<string> baseKeyColumns)
    {
        var kept = new List<ShadowRow>();
        foreach (var group in rows.GroupBy(row => BaseKey(row, baseKeyColumns)))
        {
            var list = group.ToList();
            var removed = new bool[list.Count];
            for (var index = 0; index < list.Count; index++)
            {
                if (removed[index])
                {
                    continue;
                }
                for (var other = index + 1; other < list.Count; other++)
                {
                    if (removed[other])
                    {
                        continue;
                    }
                    var direction = ToKeyPart(list[index].Cells[directionColumn]);
                    var otherDirection = ToKeyPart(list[other].Cells[directionColumn]);
                    var quantity = ToNumber(list[index].Cells[quantityColumn]);
                    var otherQuantity = ToNumber(list[other].Cells[quantityColumn]);
                    if (quantity is null || otherQuantity is null)
                    {
                        continue;
                    }
                    var oppositeDirection = direction.Equals("I", StringComparison.OrdinalIgnoreCase)
                        ? otherDirection.Equals("O", StringComparison.OrdinalIgnoreCase)
                        : direction.Equals("O", StringComparison.OrdinalIgnoreCase)
                            && otherDirection.Equals("I", StringComparison.OrdinalIgnoreCase);
                    if (oppositeDirection && Math.Abs(quantity.Value + otherQuantity.Value) <= 0.01)
                    {
                        removed[index] = true;
                        removed[other] = true;
                        break;
                    }
                }
            }
            for (var index = 0; index < list.Count; index++)
            {
                if (!removed[index])
                {
                    kept.Add(list[index]);
                }
            }
        }
        return kept;
    }

    private static string BaseKey(ShadowRow row, IReadOnlyList<string> columns) =>
        string.Join("|", columns.Select(column =>
            row.Cells.TryGetValue(column, out var value) ? ToKeyPart(value) : string.Empty));

    private static double? ToNumber(object? value) =>
        value is double number ? number
        : value is decimal number2 ? (double)number2
        : value is float number3 ? number3
        : value is int number4 ? number4
        : value is long number5 ? number5
        : null;

    private static async Task DumpSnapshot(TextWriter log, string side, ShadowSnapshot snapshot)
    {
        foreach (var table in new[] { "PUR_PURCHASE_M", "PUR_PURCHASE_D", "PRODUCT" })
        {
            if (!snapshot.Rows.TryGetValue(table, out var rows))
            {
                continue;
            }
            foreach (var row in rows)
            {
                var fields = new[] { "FINISHED_TAG", "FINISHED_PERSON", "QTY", "RECEIVE_QTY", "IN_BUY_QTY", "MRP_QTY" };
                var summary = string.Join(" ", fields
                    .Where(field => row.Cells.ContainsKey(field))
                    .Select(field => field + "=" + ToKeyPart(row.Cells[field])));
                await log.WriteLineAsync($"snapshot {side} table={table} key={row.Key} {summary}");
            }
        }
    }

    private static ShadowTableDiff CompareTable(string table, IReadOnlyList<ShadowRow> oldRows, IReadOnlyList<ShadowRow> newRows)
    {
        var diffs = new List<ShadowFieldDiff>();
        var oldByKey = oldRows.GroupBy(row => row.Key).ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);
        var newByKey = newRows.GroupBy(row => row.Key).ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);
        foreach (var key in oldByKey.Keys.Union(newByKey.Keys, StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal))
        {
            var old = oldByKey.TryGetValue(key, out var oldGroup) ? oldGroup : new List<ShadowRow>();
            var current = newByKey.TryGetValue(key, out var newGroup) ? newGroup : new List<ShadowRow>();
            var count = Math.Max(old.Count, current.Count);
            for (var index = 0; index < count; index++)
            {
                var oldRow = index < old.Count ? old[index] : null;
                var newRow = index < current.Count ? current[index] : null;
                if (oldRow is null || newRow is null)
                {
                    diffs.Add(new ShadowFieldDiff(
                        key, "*row*",
                        oldRow is null ? null : RowSummary(oldRow),
                        newRow is null ? null : RowSummary(newRow),
                        Normalized: false, Verdict: "diff"));
                    continue;
                }
                foreach (var column in oldRow.Cells.Keys.Union(newRow.Cells.Keys, StringComparer.OrdinalIgnoreCase)
                             .OrderBy(name => name, StringComparer.OrdinalIgnoreCase))
                {
                    oldRow.Cells.TryGetValue(column, out var oldValue);
                    newRow.Cells.TryGetValue(column, out var newValue);
                    var (equal, normalized) = CellsEquivalent(column, oldValue, newValue);
                    if (!equal)
                    {
                        diffs.Add(new ShadowFieldDiff(key, column, oldValue, newValue, normalized, normalized ? "equal" : "diff"));
                    }
                }
            }
        }
        return new ShadowTableDiff(table, oldRows.Count, newRows.Count, diffs);
    }

    private static (bool Equal, bool Normalized) CellsEquivalent(string column, object? oldValue, object? newValue)
    {
        if (oldValue is null && newValue is null)
        {
            return (true, true);
        }
        if (oldValue is null || newValue is null)
        {
            return (false, false);
        }
        if (NowLikeColumns.Contains(column) && oldValue is DateTime && newValue is DateTime)
        {
            return (true, true);
        }
        if (TryToDouble(oldValue, out var oldNumber) && TryToDouble(newValue, out var newNumber))
        {
            var tolerance = QuantityColumns.Contains(column) ? 0.01 : 0.0001;
            var delta = Math.Abs(Math.Round(oldNumber, 8) - Math.Round(newNumber, 8));
            if (delta <= tolerance)
            {
                return (true, true);
            }
            return (false, false);
        }
        var equal = oldValue.Equals(newValue);
        return equal ? (true, true) : (false, false);
    }

    private static object? NormalizeValue(object? raw) => raw switch
    {
        null or DBNull => null,
        string value => value.Trim(),
        decimal value => (double)value,
        float value => (double)value,
        double value => value,
        byte value => value,
        short value => value,
        int value => value,
        long value => value,
        bool value => value,
        DateTime value => value,
        byte[] bytes => Convert.ToBase64String(bytes),
        _ => raw.ToString(),
    };

    private static bool TryToDouble(object value, out double result)
    {
        switch (value)
        {
            case double number:
                result = number;
                return true;
            case decimal number:
                result = (double)number;
                return true;
            case float number:
                result = number;
                return true;
            case byte number:
                result = number;
                return true;
            case short number:
                result = number;
                return true;
            case int number:
                result = number;
                return true;
            case long number:
                result = number;
                return true;
            default:
                result = 0;
                return false;
        }
    }

    private static string ToKeyPart(object? value) =>
        value is null ? string.Empty : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)!.Trim();

    private static string RowSummary(ShadowRow row) =>
        string.Join(";", row.Cells.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => pair.Key + "=" + ToKeyPart(pair.Value)));

    private static string NextRunId(int moduleId)
    {
        var directory = Path.Combine(RepoRoot.Value, SnapshotDirectory);
        Directory.CreateDirectory(directory);
        var prefix = $"shadow-{moduleId}-{DateTime.Now:yyyyMMdd}-";
        var sequence = Directory.EnumerateFiles(directory, prefix + "*.json")
            .Select(Path.GetFileNameWithoutExtension)
            .Select(name => name is null ? 0 : int.TryParse(name.AsSpan(prefix.Length), out var parsed) ? parsed : 0)
            .DefaultIfEmpty(0)
            .Max() + 1;
        return prefix + sequence.ToString("D3");
    }

    private static async Task WriteReportAsync(ShadowReport report)
    {
        var directory = Path.Combine(RepoRoot.Value, SnapshotDirectory);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, report.RunId + ".json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(report, ReportJsonOptions));
    }

    private static string? ResolveConnectionString()
    {
        var root = FindRepoRoot();
        var settingsPath = Path.Combine(root, "EOS.API", "appsettings.Development.json");
        if (!File.Exists(settingsPath))
        {
            return null;
        }
        using var document = JsonDocument.Parse(File.ReadAllText(settingsPath));
        return document.RootElement.TryGetProperty("ConnectionStrings", out var section)
            && section.TryGetProperty("ErpDatabase", out var value)
            ? value.GetString()
            : null;
    }

    private static string FindRepoRoot()
    {
        var attribute = typeof(EffectShadowRunner).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(item => item.Key == "RepoRoot");
        if (attribute?.Value is { Length: > 0 } root && Directory.Exists(root))
        {
            return Path.GetFullPath(root);
        }
        var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "EOS.slnx")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName ?? Directory.GetCurrentDirectory();
    }

    public sealed record ShadowOptions(
        int ModuleId,
        string Event,
        string? Keys = null,
        string? RunId = null,
        bool Failure = false);

    public sealed record ShadowPathStatus(string Status, string? Error);

    public sealed record ShadowReport(
        string RunId,
        int ModuleId,
        string DefinitionVersion,
        string Event,
        IReadOnlyList<string> RecordKeys,
        ShadowPathStatus OldPath,
        ShadowPathStatus NewPath,
        IReadOnlyList<ShadowTableDiff> Tables,
        ShadowAudit Audit,
        ShadowSummary Summary);

    public sealed record ShadowTableDiff(string Table, int RowsOld, int RowsNew, IReadOnlyList<ShadowFieldDiff> Diffs);

    public sealed record ShadowFieldDiff(string Key, string Field, object? Old, object? New, bool Normalized, string Verdict);

    public sealed record ShadowAudit(int OldCount, int NewCount, IReadOnlyList<string> OldActions, IReadOnlyList<string> NewActions);

    public sealed record ShadowSummary(string Verdict, int DiffCount, int UnnormalizedDiffCount);

    private sealed record MasterContext(DateTime? ReceiveDate, string? SupplierId, string ReceiveType, string ReceiveNo);

    private sealed record DetailRow(
        string PurchaseType,
        string PurchaseNo,
        int? PurchaseSerialNo,
        string OrderType,
        string OrderNo,
        string ProNo,
        string DepotId,
        string BatchNo,
        int? SerialNo);

    private sealed record TableSpec(string Table, IReadOnlyList<string> KeyColumns, string FilterSql, IReadOnlyList<SqlParameter> Parameters);

    private sealed record ShadowRow(string Key, Dictionary<string, object?> Cells);

    private sealed record ShadowSnapshot(
        IReadOnlyDictionary<string, List<ShadowRow>> Rows,
        int AuditCount,
        IReadOnlyList<string> AuditActions);

    private sealed record LegacyPathResult(
        string Status,
        string? Error,
        ShadowSnapshot? Snapshot,
        int AuditCount,
        IReadOnlyList<string> AuditActions);

    private sealed record EnginePathResult(
        ShadowPathStatus Status,
        ShadowSnapshot? Snapshot,
        int AuditCount,
        IReadOnlyList<string> AuditActions);
}
