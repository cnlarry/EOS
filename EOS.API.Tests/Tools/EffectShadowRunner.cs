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
/// Shadow comparison runner for module effect configs: executes the
/// legacy workflow stored procedure and the effect engine against the same record in
/// two independent rolled-back transactions, snapshots the affected tables inside each
/// transaction, then writes a normalized diff report to logs/shadow/. DB-only tool: it
/// never writes workspace configuration, never sends HTTP and never starts services.
/// Run with EOS_SHADOW_RUN=1 and optional EOS_SHADOW_MODULE / EOS_SHADOW_KEYS
/// ("KEY1|KEY2"). The published snapshot must already carry the
/// businessActions section and effectEngine.enabled=true (publish via the admin API
/// before the first run). With EOS_SHADOW_ENGINE_ONLY=1 the legacy path is skipped
/// (its stored procedures were retired) and only the engine path runs; the verdict
/// is PASS when the engine executes cleanly with zero residue.
/// Supported modules: 1607 (purchase receipt), 1406 (customer delivery),
/// 1505 (production inbound), 1407 (customer return), 1413 (delivery callback),
/// 1610 (purchase callback), 170101 (customer settlement), 170201 (supplier settlement),
/// 1404 (customer quote), 1604 (supplier quote), 1418 (order change),
/// 1609 (purchase change), 1509 (produce change), 1405 (customer order MRP),
/// 1502 (produce MRP material-provide), 1512/1522/2803/2804 (produce variants),
/// 2816 (outsourced inbound), 1503/1514/1517/2805/2806 (material issue),
/// 1615/1616 (purchase request), 2817/2818 (misc in/out), 2906 (mould batch),
/// 180106/180206/180207/180310/1803101 (HR), 2705/2706/2707/2708 (work-order family),
/// 2815 (product outbound), 2903/2904 (mould apply/accept), 2907/2913 (mould issue),
/// 1423 (production-defect material return).
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
        "NOT_SEND_QTY", "NOT_IN_QTY", "NOT_GET_QTY", "PLAN_QTY", "PLAN_SPARE_QTY", "DEPOT_QTY",
        "PRODUCE_QTY", "FINISHED_PLAN_QTY", "FINISHED_PLAN_SPARE_QTY",
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

    /// <summary>Per-module shadow specification: table/column names and party linkage.</summary>
    private sealed record ModuleShadowSpec(
        int ModuleId,
        string ModuleNumber,
        string MasterTable,
        string DetailTable,
        string Key1Column,
        string Key2Column,
        string DateColumn,
        string? PartyColumn,
        string? PartyTable,
        string? PartyIdColumn,
        string DocName);

    private static ModuleShadowSpec GetSpec(int moduleId) => moduleId switch
    {
        1607 => new(1607, "1607", "PUR_RECEIVE_M", "PUR_RECEIVE_D",
            "RECEIVE_TYPE", "RECEIVE_NO", "RECEIVE_DATE",
            "SUPPLIER_ID", "SUPPLIER", "SUPPLIER_ID", "收料单"),
        1406 => new(1406, "1406", "COP_SEND_M", "COP_SEND_D",
            "SEND_TYPE", "SEND_NO", "SEND_DATE",
            "CLIENT_ID", "CLIENT", "CLIENT_ID", "送货单"),
        1505 => new(1505, "1505", "MOC_PRODUCT_IN_M", "MOC_PRODUCT_IN_D",
            "PRODUCT_IN_TYPE", "PRODUCT_IN_NO", "PRODUCT_IN_DATE",
            null, null, null, "生产入库单"),
        1407 => new(1407, "1407", "COP_RETURN_M", "COP_RETURN_D",
            "RETURN_TYPE", "RETURN_NO", "RETURN_DATE",
            null, null, null, "客户退货单"),
        1413 => new(1413, "1413", "COP_CALLBACK_M", "COP_CALLBACK_D",
            "CALLBACK_TYPE", "CALLBACK_NO", "CALLBACK_DATE",
            null, null, null, "送货单回执"),
        1423 => new(1423, "1423", "COP_BACK_M", "COP_BACK_D",
            "BACK_TYPE", "BACK_NO", "BACK_DATE",
            null, null, null, "退料单(生产不良)"),
        1610 => new(1610, "1610", "PUR_CALLBACK_M", "PUR_CALLBACK_D",
            "CALLBACK_TYPE", "CALLBACK_NO", "CALLBACK_DATE",
            "SUPPLIER_ID", "SUPPLIER", "SUPPLIER_ID", "收料核价单"),
        170101 => new(170101, "170101", "COP_ACCOUNT_M", "COP_ACCOUNT_D",
            "ACCOUNT_TYPE", "ACCOUNT_NO", "ACCOUNT_DATE",
            "CLIENT_ID", "CLIENT", "CLIENT_ID", "客户对账单"),
        170201 => new(170201, "170201", "PUR_DUE_M", "PUR_DUE_D",
            "DUE_TYPE", "DUE_NO", "DUE_DATE",
            "SUPPLIER_ID", "SUPPLIER", "SUPPLIER_ID", "厂商对账单"),
        1404 => new(1404, "1404", "COP_QUOTE_M", "COP_QUOTE_D",
            "QUOTE_TYPE", "QUOTE_NO", "QUOTE_DATE",
            "CLIENT_ID", "CLIENT", "CLIENT_ID", "客户报价单"),
        1604 => new(1604, "1604", "PUR_QUOTE_M", "PUR_QUOTE_D",
            "QUOTE_TYPE", "QUOTE_NO", "QUOTE_DATE",
            "SUPPLIER_ID", "SUPPLIER", "SUPPLIER_ID", "厂商报价单"),
        1418 => new(1418, "1418", "COP_ORDER_CHANGE_M", "COP_ORDER_CHANGE_D",
            "CHANGE_ORDER_TYPE", "CHANGE_ORDER_NO", "CHANGE_ORDER_DATE",
            null, null, null, "订单变更单"),
        1609 => new(1609, "1609", "PUR_PURCHASE_CHANGE_M", "PUR_PURCHASE_CHANGE_D",
            "CHANGE_PURCHASE_TYPE", "CHANGE_PURCHASE_NO", "CHANGE_PURCHASE_DATE",
            null, null, null, "采购变更单"),
        1509 => new(1509, "1509", "MOC_PRODUCE_CHANGE_M", "MOC_PRODUCE_CHANGE_D",
            "CHANGE_PRODUCE_TYPE", "CHANGE_PRODUCE_NO", "CHANGE_PRODUCE_DATE",
            null, null, null, "制令变更单"),
        1405 => new(1405, "1405", "COP_ORDER_M", "COP_ORDER_D",
            "ORDER_TYPE", "ORDER_NO", "ORDER_DATE",
            "CLIENT_ID", "CLIENT", "CLIENT_ID", "客户订单"),
        1502 => new(1502, "1502", "MOC_PRODUCE_M", "MOC_PRODUCE_D",
            "PRODUCE_TYPE", "PRODUCE_NO", "PRODUCE_DATE",
            null, null, null, "制令单"),
        1512 => new(1512, "1512", "MOC_PRODUCE_M", "MOC_PRODUCE_D",
            "PRODUCE_TYPE", "PRODUCE_NO", "PRODUCE_DATE",
            null, null, null, "重工生产单"),
        1522 => new(1522, "1522", "MOC_PRODUCE_M", "MOC_PRODUCE_D",
            "PRODUCE_TYPE", "PRODUCE_NO", "PRODUCE_DATE",
            null, null, null, "半成品制令单"),
        2803 => new(2803, "2803", "MOC_PRODUCE_M", "MOC_PRODUCE_D",
            "PRODUCE_TYPE", "PRODUCE_NO", "PRODUCE_DATE",
            null, null, null, "托外生产单"),
        2804 => new(2804, "2804", "MOC_PRODUCE_M", "MOC_PRODUCE_D",
            "PRODUCE_TYPE", "PRODUCE_NO", "PRODUCE_DATE",
            null, null, null, "托外重工单"),
        2816 => new(2816, "2816", "MOC_PRODUCT_IN_M", "MOC_PRODUCT_IN_D",
            "PRODUCT_IN_TYPE", "PRODUCT_IN_NO", "PRODUCT_IN_DATE",
            null, null, null, "托外入库单"),
        1503 => new(1503, "1503", "MOC_GET_M", "MOC_GET_D",
            "GET_TYPE", "GET_NO", "GET_DATE",
            null, null, null, "生产领料单"),
        1514 => new(1514, "1514", "MOC_GET_M", "MOC_GET_D",
            "GET_TYPE", "GET_NO", "GET_DATE",
            null, null, null, "生产补料单"),
        1517 => new(1517, "1517", "MOC_GET_M", "MOC_GET_D",
            "GET_TYPE", "GET_NO", "GET_DATE",
            null, null, null, "生产领料单(外)"),
        2805 => new(2805, "2805", "MOC_GET_M", "MOC_GET_D",
            "GET_TYPE", "GET_NO", "GET_DATE",
            null, null, null, "托外领料单"),
        2806 => new(2806, "2806", "MOC_GET_M", "MOC_GET_D",
            "GET_TYPE", "GET_NO", "GET_DATE",
            null, null, null, "托外补料单"),
        1615 => new(1615, "1615", "PUR_APPLY_M", "PUR_APPLY_D",
            "APPLY_TYPE", "APPLY_NO", "APPLY_DATE",
            null, null, null, "成品请购单"),
        1616 => new(1616, "1616", "PUR_APPLY_M", "PUR_APPLY_D",
            "APPLY_TYPE", "APPLY_NO", "APPLY_DATE",
            null, null, null, "备料单"),
        2817 => new(2817, "2817", "INV_OCCUR_IN_M", "INV_OCCUR_IN_D",
            "OCCUR_TYPE", "OCCUR_NO", "OCCUR_DATE",
            null, null, null, "品检收料入库"),
        2818 => new(2818, "2818", "INV_OCCUR_OUT_M", "INV_OCCUR_OUT_D",
            "OCCUR_TYPE", "OCCUR_NO", "OCCUR_DATE",
            null, null, null, "品检良品出库单"),
        130101 => new(130101, "130101", "INV_CHECK_STOCK_M", "INV_CHECK_STOCK_D",
            "CHECK_STOCK_TYPE", "CHECK_STOCK_NO", "CHECK_DATE",
            null, null, null, "库存盘点单"),
        130103 => new(130103, "130103", "INV_OCCUR_IN_M", "INV_OCCUR_IN_D",
            "OCCUR_TYPE", "OCCUR_NO", "OCCUR_DATE",
            null, null, null, "其它入库单"),
        130104 => new(130104, "130104", "INV_OCCUR_OUT_M", "INV_OCCUR_OUT_D",
            "OCCUR_TYPE", "OCCUR_NO", "OCCUR_DATE",
            null, null, null, "其它出库单"),
        130105 => new(130105, "130105", "INV_OCCUR_TRANSFER_M", "INV_OCCUR_TRANSFER_D",
            "OCCUR_TYPE", "OCCUR_NO", "OCCUR_DATE",
            null, null, null, "仓库调拔单"),
        130110 => new(130110, "130110", "INV_OCCUR_OUT_M", "INV_OCCUR_OUT_D",
            "OCCUR_TYPE", "OCCUR_NO", "OCCUR_DATE",
            null, null, null, "样品出库单"),
        3303 => new(3303, "3303", "QC_ANALYSIS_M", "QC_ANALYSIS_D",
            "ANALYSIS_TYPE", "ANALYSIS_NO", "ANALYSIS_DATE",
            null, null, null, "品质日分析单"),
        3901 => new(3901, "3901", "INV_OCCUR_OUT_M", "INV_OCCUR_OUT_D",
            "OCCUR_TYPE", "OCCUR_NO", "OCCUR_DATE",
            null, null, null, "品检不良品出库"),
        170103 => new(170103, "170103", "COP_PREPAY_M", "COP_PREPAY_D",
            "PREPAY_TYPE", "PREPAY_NO", "PREPAY_DATE",
            null, null, null, "预收帐款单"),
        170203 => new(170203, "170203", "PUR_PREPAY_M", "PUR_PREPAY_D",
            "PREPAY_TYPE", "PREPAY_NO", "PREPAY_DATE",
            null, null, null, "预付帐款单"),
        300301 => new(300301, "300301", "CUS_EXPORT_M", "CUS_EXPORT_D",
            "EXPORT_TYPE", "EXPORT_NO", "EXPORT_DATE",
            null, null, null, "出口报关单(直接出口)"),
        300302 => new(300302, "300302", "CUS_IMPORT_M", "CUS_IMPORT_D",
            "IMPORT_TYPE", "IMPORT_NO", "IMPORT_DATE",
            null, null, null, "进口报关单(直接进口)"),
        300304 => new(300304, "300304", "CUS_EXPORT_M", "CUS_EXPORT_D",
            "EXPORT_TYPE", "EXPORT_NO", "EXPORT_DATE",
            null, null, null, "出口报关单(转厂)"),
        300305 => new(300305, "300305", "CUS_IMPORT_M", "CUS_IMPORT_D",
            "IMPORT_TYPE", "IMPORT_NO", "IMPORT_DATE",
            null, null, null, "进口报关单(转厂)"),
        2906 => new(2906, "2906", "MOU_BATCH_M", "MOU_BATCH_D",
            "BATCH_TYPE", "BATCH_NO", "BATCH_DATE",
            null, null, null, "量产模具（开模完工）单"),
        180106 => new(180106, "180106", "HR_CONTRACT_M", "HR_CONTRACT_D",
            "CONT_TYPE", "CONT_NO", "CONT_DATE",
            null, null, null, "合同签订"),
        180206 => new(180206, "180206", "HR_APPLY_M", "HR_APPLY_D",
            "APPLY_TYPE", "APPLY_NO", "APPLY_DATE",
            null, null, null, "加班申请单"),
        180207 => new(180207, "180207", "HR_WORKTIME_M", "HR_WORKTIME_D",
            "WORKTIME_TYPE", "WORKTIME_NO", "WORKTIME_DATE",
            null, null, null, "工时录入表"),
        2705 => new(2705, "2705", "MOC_WORK_M", "MOC_WORK_D",
            "WORK_TYPE", "WORK_NO", "WORK_DATE",
            null, null, null, "工序工单"),
        2706 => new(2706, "2706", "MOC_WORK_IN_M", "MOC_WORK_IN_D",
            "WORK_IN_TYPE", "WORK_IN_NO", "WORK_IN_DATE",
            null, null, null, "工序完工单"),
        2707 => new(2707, "2707", "MOC_WORK_OUT_M", "MOC_WORK_OUT_D",
            "WORK_OUT_TYPE", "WORK_OUT_NO", "WORK_OUT_DATE",
            null, null, null, "工序发料单"),
        2708 => new(2708, "2708", "SFC_PLAN_M", "SFC_PLAN_D",
            "PLAN_TYPE", "PLAN_NO", "PLAN_DATE",
            null, null, null, "工序生产计划"),
        2815 => new(2815, "2815", "MOC_PRODUCT_OUT_M", "MOC_PRODUCT_OUT_D",
            "PRODUCT_OUT_TYPE", "PRODUCT_OUT_NO", "PRODUCT_OUT_DATE",
            null, null, null, "生产出库单"),
        2903 => new(2903, "2903", "MOU_APPLY_M", "MOU_APPLY_D",
            "APPLY_TYPE", "APPLY_NO", "APPLY_DATE",
            null, null, null, "开模申请单"),
        // 2904 has no physical detail table (MOU_ACCEPT_D does not exist in sys.objects):
        // the placeholder is never queried (master-only resolve, dedicated snapshot builder).
        2904 => new(2904, "2904", "MOU_ACCEPT_M", "MOU_ACCEPT_D",
            "ACCEPT_TYPE", "ACCEPT_NO", "ACCEPT_DATE",
            null, null, null, "模具承认单"),
        2907 => new(2907, "2907", "MOU_GET_M", "MOU_GET_D",
            "GET_TYPE", "GET_NO", "GET_DATE",
            null, null, null, "模房领料单"),
        2913 => new(2913, "2913", "MOU_GET2_M", "MOU_GET2_D",
            "GET_TYPE", "GET_NO", "GET_DATE",
            null, null, null, "模具耗料单"),
        180310 => new(180310, "180310", "HR_WAGE_M", "HR_WAGE_D",
            "WAGE_TYPE", "WAGE_NO", "WAGE_DATE",
            null, null, null, "离职工资表"),
        1803101 => new(1803101, "1803101", "HR_WAGE_M", "HR_WAGE_D",
            "WAGE_TYPE", "WAGE_NO", "WAGE_DATE",
            null, null, null, "保密离职工资表"),
        _ => throw new NotSupportedException(
            $"Effect shadow snapshot specs are implemented for modules 1607/1406/1505/1407/1413/1610/170101/170201/1404/1604/1418/1609/1509/1405/1502/1512/1522/2803/2804/2816/1503/1514/1517/2805/2806/1615/1616/2817/2818/2906/180106/180206/180207/2705/2706/2707/2708/2815/2903/2904/2907/2913/180310/1803101 only (requested {moduleId})."),
    };

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

    [Fact]
    public async Task EngineOnly_Regression_OkWithZeroResidue()
    {
        if (Environment.GetEnvironmentVariable("EOS_SHADOW_RUN") != "1"
            || Environment.GetEnvironmentVariable("EOS_SHADOW_ENGINE_ONLY") != "1")
        {
            return;
        }
        var moduleId = int.TryParse(Environment.GetEnvironmentVariable("EOS_SHADOW_MODULE"), out var module)
            ? module
            : 1607;
        var keys = Environment.GetEnvironmentVariable("EOS_SHADOW_KEYS");
        var runId = Environment.GetEnvironmentVariable("EOS_SHADOW_RUN_ID");
        var shadowEvent = Environment.GetEnvironmentVariable("EOS_SHADOW_EVENT") ?? "APPROVE_EFFECT";
        var deapprove = shadowEvent.Equals("DEAPPROVE", StringComparison.OrdinalIgnoreCase);
        var connectionString = ConnectionString.Value
            ?? throw new InvalidOperationException("无法解析 EOS.API/appsettings.Development.json 连接串。");
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        var spec = GetSpec(moduleId);
        var resolved = await ResolveRecordKeysAsync(connection, spec, moduleId, keys, deapprove, failure: false);
        var before = await ReadConfirmTagAsync(connection, spec, resolved);
        var options = new ShadowOptions(moduleId, shadowEvent, string.Join('|', resolved), runId, Failure: false, EngineOnly: true);
        var report = await RunAsync(options, Console.Out);
        var after = await ReadConfirmTagAsync(connection, spec, resolved);

        // Engine-only regression gate: the engine must execute the full chain cleanly
        // inside its rolled-back transaction, leaving the document status untouched.
        Assert.Equal("skipped", report.OldPath.Status);
        Assert.Equal("ok", report.NewPath.Status);
        Assert.True(string.IsNullOrEmpty(report.NewPath.Error));
        Assert.Equal(before, after);
        Console.WriteLine($"engine-only verdict={report.Summary.Verdict} new={report.NewPath.Status} confirm={before}->{after}");
    }

    private static async Task<int> ReadConfirmTagAsync(SqlConnection connection, ModuleShadowSpec spec, IReadOnlyList<string> keys)
    {
        await using var command = new SqlCommand(
            $"SELECT ISNULL(CONFIRM_TAG,0) FROM dbo.{spec.MasterTable} WHERE {spec.Key1Column}=@Type AND {spec.Key2Column}=@No;",
            connection);
        command.Parameters.Add("@Type", SqlDbType.NChar, 10).Value = keys[0];
        command.Parameters.Add("@No", SqlDbType.NChar, 20).Value = keys[1];
        var value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? -1 : Convert.ToInt32(value);
    }

    /// <summary>Runs the shadow comparison and writes the JSON report; returns the report.</summary>
    public async Task<ShadowReport> RunAsync(ShadowOptions options, TextWriter log)
    {
        if (options.ModuleId is not (1607 or 1406 or 1505 or 1407 or 1413 or 1610 or 170101 or 170201 or 1404 or 1604 or 1418 or 1609 or 1509 or 1405 or 1502 or 2906 or 180106 or 180206 or 180207 or 1512 or 1522 or 2803 or 2804 or 2816 or 1503 or 1514 or 1517 or 2805 or 2806 or 1615 or 1616 or 2817 or 2818 or 2705 or 2706 or 2707 or 2708 or 2815 or 2903 or 2904 or 2907 or 2913 or 130101 or 130103 or 130104 or 130105 or 130110 or 3303 or 3901 or 170103 or 170203 or 300301 or 300302 or 300304 or 300305 or 180310 or 1803101 or 1423))
        {
            throw new NotSupportedException("Effect shadow snapshot specs are implemented for modules 1607/1406/1505/1407/1413/1610/170101/170201/1404/1604/1418/1609/1509/1405/1502/1512/1522/2803/2804/2816/1503/1514/1517/2805/2806/1615/1616/2817/2818/2906/180106/180206/180207/2705/2706/2707/2708/2815/2903/2904/2907/2913/180310/1803101 only.");
        }
        var spec = GetSpec(options.ModuleId);
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

        var keys = await ResolveRecordKeysAsync(connection, spec, options.ModuleId, options.Keys, deapprove, options.Failure);
        await log.WriteLineAsync($"shadow run={runId} module={options.ModuleId} event={options.Event} version={version} keys={string.Join("|", keys)} sproc={workflowSproc}");

        var definitionVersion = $"module-{options.ModuleId}-v{version}";
        var legacy = options.EngineOnly
            ? new LegacyPathResult("skipped", "旧路径已退役（061），引擎单跑模式跳过。", null, 0, Array.Empty<string>())
            : await RunLegacyPathAsync(connection, definition, spec, workflowSproc, keys, deapprove, log);
        var engineResult = await RunEnginePathAsync(definition, spec, keys, deapprove, log);
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
        else if (options.EngineOnly && engine.Status == "ok")
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
        SqlConnection connection, ModuleShadowSpec spec, int moduleId, string? explicitKeys, bool deapprove, bool failure)
    {
        if (!string.IsNullOrWhiteSpace(explicitKeys))
        {
            return explicitKeys.Split('|', StringSplitOptions.TrimEntries);
        }
        if (spec.ModuleId == 1406)
        {
            return await ResolveRecordKeys1406Async(connection, deapprove, failure);
        }
        if (spec.ModuleId == 1505)
        {
            return await ResolveRecordKeys1505Async(connection, deapprove, failure);
        }
        if (spec.ModuleId == 1407)
        {
            return await ResolveRecordKeys1407Async(connection, deapprove, failure);
        }
        if (spec.ModuleId == 1413)
        {
            return await ResolveRecordKeys1413Async(connection, deapprove, failure);
        }
        if (spec.ModuleId == 1610)
        {
            return await ResolveRecordKeys1610Async(connection, deapprove, failure);
        }
        if (spec.ModuleId == 170101)
        {
            return await ResolveRecordKeys170101Async(connection, deapprove, failure);
        }
        if (spec.ModuleId == 170201)
        {
            return await ResolveRecordKeys170201Async(connection, deapprove, failure);
        }
        if (spec.ModuleId == 1418)
        {
            return await ResolveRecordKeys1418Async(connection, deapprove, failure);
        }
        if (spec.ModuleId == 1609)
        {
            return await ResolveRecordKeys1609Async(connection, deapprove, failure);
        }
        if (spec.ModuleId == 1509)
        {
            return await ResolveRecordKeys1509Async(connection, deapprove, failure);
        }
        if (spec.ModuleId == 1405)
        {
            return await ResolveRecordKeys1405Async(connection, deapprove, failure);
        }
        if (spec.ModuleId == 1502)
        {
            return await ResolveRecordKeys1502Async(connection, deapprove, failure);
        }
        if (spec.ModuleId is 1512 or 1522 or 2803 or 2804)
        {
            return await ResolveRecordKeys1502Async(connection, deapprove, failure);
        }
        if (spec.ModuleId == 2816)
        {
            return await ResolveRecordKeys1505Async(connection, deapprove, failure);
        }
        if (spec.ModuleId is 180310 or 1803101)
        {
            return await ResolveRecordKeys180310Async(connection, spec, deapprove, failure);
        }
        if (spec.ModuleId is 2906 or 180106 or 180206 or 180207 or 1503 or 1514 or 1517 or 2805 or 2806 or 1615 or 1616 or 2817 or 2818
            or 2705 or 2706 or 2707 or 2708 or 2815 or 2903 or 2907 or 2913
            or 130101 or 130103 or 130104 or 130105 or 130110 or 3303 or 3901 or 170103 or 170203
            or 300301 or 300302 or 300304 or 300305 or 1423)
        {
            return await ResolveRecordKeysByConfirmAsync(connection, spec, deapprove, failure);
        }
        if (spec.ModuleId == 2904)
        {
            return await ResolveRecordKeys2904Async(connection, deapprove, failure);
        }
        if (spec.ModuleId == 1404)
        {
            return await ResolveRecordKeys1404Async(connection, deapprove, failure);
        }
        if (spec.ModuleId == 1604)
        {
            return await ResolveRecordKeys1604Async(connection, deapprove, failure);
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

    /// <summary>
    /// Violation predicate shared by 1406 approve (NOT ...) and failure (...) selection:
    /// the document violates at least one currently ACTIVE quantity check (each check
    /// carries its own SYSSS switch; the shipment check is unconditional).
    /// </summary>
    private const string SendOverActiveChecks = """
        (
          (EXISTS(SELECT 1 FROM dbo.SYSSS WHERE SEND_ORDER_TAG=1) AND EXISTS(
            SELECT 1 FROM dbo.COP_SEND_D S JOIN dbo.COP_ORDER_D T
              ON T.ORDER_TYPE=S.ORDER_TYPE AND T.ORDER_NO=S.ORDER_NO AND T.SERIAL_NO=S.ORDER_SERIAL_NO
             WHERE S.SEND_TYPE=M.SEND_TYPE AND S.SEND_NO=M.SEND_NO
               AND (COALESCE(T.FINISHED_SEND_QTY,0)+COALESCE(T.BACK_MATERIAL,0)+COALESCE(T.BACK_BAD,0)+COALESCE(S.QTY,0) > COALESCE(T.QTY,0)
                 OR COALESCE(T.FINISHED_SPARE_QTY,0)+COALESCE(S.SPARE_QTY,0) > COALESCE(T.SPARE_QTY,0))))
          OR EXISTS(
            SELECT 1 FROM dbo.COP_SEND_D S JOIN dbo.COP_SHIPMENT_D T
              ON T.SHIPMENT_TYPE=S.SHIPMENT_TYPE AND T.SHIPMENT_NO=S.SHIPMENT_NO AND T.SERIAL_NO=S.SHIPMENT_SERIAL_NO
             WHERE S.SEND_TYPE=M.SEND_TYPE AND S.SEND_NO=M.SEND_NO
               AND COALESCE(T.FINISHED_QTY,0)+COALESCE(S.QTY,0) > COALESCE(T.QTY,0))
          OR (EXISTS(SELECT 1 FROM dbo.SYSSS WHERE SEND_ORDER_FITOUT_TAG=1) AND EXISTS(
            SELECT 1 FROM dbo.COP_SEND_D S JOIN dbo.COP_ORDER_D T
              ON T.ORDER_TYPE=S.ORDER_TYPE AND T.ORDER_NO=S.ORDER_NO AND T.SERIAL_NO=S.ORDER_SERIAL_NO
             WHERE S.SEND_TYPE=M.SEND_TYPE AND S.SEND_NO=M.SEND_NO
               AND (COALESCE(T.FINISHED_SEND_QTY,0)+COALESCE(S.QTY,0) > COALESCE(T.FINISHED_FITOUT_QTY,0)
                 OR COALESCE(T.FINISHED_SPARE_QTY,0)+COALESCE(S.SPARE_QTY,0) > COALESCE(T.FINISHED_FITOUT_SPARE_QTY,0))))
          OR (EXISTS(SELECT 1 FROM dbo.SYSSS WHERE SEND_PRODUCE_FITOUT_TAG=1) AND EXISTS(
            SELECT 1 FROM dbo.COP_SEND_D S JOIN dbo.MOC_PRODUCE_M T
              ON T.PRODUCE_TYPE=S.PRODUCE_TYPE AND T.PRODUCE_NO=S.PRODUCE_NO
             WHERE S.SEND_TYPE=M.SEND_TYPE AND S.SEND_NO=M.SEND_NO
               AND (COALESCE(T.FINISHED_SEND_QTY,0)+COALESCE(S.QTY,0) > COALESCE(T.FINISHED_FITOUT_QTY,0)
                 OR COALESCE(T.FINISHED_SEND_SPARE_QTY,0)+COALESCE(S.SPARE_QTY,0) > COALESCE(T.FINISHED_FITOUT_SPARE_QTY,0))))
          OR (EXISTS(SELECT 1 FROM dbo.SYSSS WHERE SEND_FITOUT_TAG=1) AND EXISTS(
            SELECT 1 FROM dbo.COP_SEND_D S JOIN dbo.COP_FITOUT_D T
              ON T.FITOUT_TYPE=S.FITOUT_TYPE AND T.FITOUT_NO=S.FITOUT_NO AND T.SERIAL_NO=S.FITOUT_SERIAL_NO
             WHERE S.SEND_TYPE=M.SEND_TYPE AND S.SEND_NO=M.SEND_NO
               AND (COALESCE(T.FINISHED_QTY,0)+COALESCE(T.RETURN_QTY,0)+COALESCE(S.QTY,0) > COALESCE(T.QTY,0)
                 OR COALESCE(T.FINISHED_SPARE_QTY,0)+COALESCE(T.RETURN_SPARE_QTY,0)+COALESCE(S.SPARE_QTY,0) > COALESCE(T.SPARE_QTY,0))))
          OR (EXISTS(SELECT 1 FROM dbo.SYSSS WHERE SEND_PRODUCE_TAG=1) AND EXISTS(
            SELECT 1 FROM dbo.COP_SEND_D S JOIN dbo.MOC_PRODUCE_M T
              ON T.PRODUCE_TYPE=S.PRODUCE_TYPE AND T.PRODUCE_NO=S.PRODUCE_NO
             WHERE S.SEND_TYPE=M.SEND_TYPE AND S.SEND_NO=M.SEND_NO
               AND (COALESCE(T.FINISHED_SEND_QTY,0)+COALESCE(S.QTY,0) > COALESCE(T.FINISHED_QTY,0)
                 OR COALESCE(T.FINISHED_SEND_SPARE_QTY,0)+COALESCE(S.SPARE_QTY,0) > COALESCE(T.FINISHED_SPARE_QTY,0))))
          OR (EXISTS(SELECT 1 FROM dbo.SYSSS WHERE SEND_PRODUCE_TRANSFER_TAG=1) AND EXISTS(
            SELECT 1 FROM dbo.COP_SEND_D S JOIN dbo.MOC_PRODUCE_M T
              ON T.PRODUCE_TYPE=S.PRODUCE_TYPE AND T.PRODUCE_NO=S.PRODUCE_NO
             WHERE S.SEND_TYPE=M.SEND_TYPE AND S.SEND_NO=M.SEND_NO
               AND (COALESCE(T.FINISHED_SEND_QTY,0)+COALESCE(S.QTY,0) > COALESCE(T.FINISHED_TRANSFER_QTY,0)
                 OR COALESCE(T.FINISHED_SEND_SPARE_QTY,0)+COALESCE(S.SPARE_QTY,0) > COALESCE(T.FINISHED_TRANSFER_SPARE_QTY,0))))
        )
        """;

    private static async Task<IReadOnlyList<string>> ResolveRecordKeys1406Async(
        SqlConnection connection, bool deapprove, bool failure)
    {
        const string noLogHistory = """
            NOT EXISTS (SELECT 1 FROM dbo.INV_DEPOT_LOG L
                        WHERE LTRIM(RTRIM(L.MUTUALITY_TYPE))=LTRIM(RTRIM(M.SEND_TYPE))
                          AND LTRIM(RTRIM(L.MUTUALITY_NO))=LTRIM(RTRIM(M.SEND_NO)))
            """;
        string sql;
        string emptyMessage;
        if (failure)
        {
            sql = "SELECT TOP 1 M.SEND_TYPE, M.SEND_NO FROM dbo.COP_SEND_M M "
                + "WHERE ISNULL(M.CONFIRM_TAG,0)=0 AND " + noLogHistory
                + " AND " + SendOverActiveChecks
                + " ORDER BY M.SEND_DATE DESC, M.SEND_NO DESC;";
            emptyMessage = "未找到可触发失败分支的超送送货单。";
        }
        else if (deapprove)
        {
            // Outbound reversal writes stock back, so no sufficiency gate is needed;
            // pick the most recently confirmed delivery with inventory-log history.
            sql = """
                SELECT TOP 1 M.SEND_TYPE, M.SEND_NO
                FROM dbo.COP_SEND_M M
                WHERE ISNULL(M.CONFIRM_TAG,0)=1
                  AND EXISTS (SELECT 1 FROM dbo.INV_DEPOT_LOG L
                              WHERE LTRIM(RTRIM(L.MUTUALITY_TYPE))=LTRIM(RTRIM(M.SEND_TYPE))
                                AND LTRIM(RTRIM(L.MUTUALITY_NO))=LTRIM(RTRIM(M.SEND_NO)))
                ORDER BY ISNULL(M.CONFIRM_DATE, M.SEND_DATE) DESC, M.SEND_NO DESC;
                """;
            emptyMessage = "未找到可解批对拍的已批核送货单（自动选单无结果）。";
        }
        else
        {
            // Outbound approval additionally requires on-hand stock: exclude documents
            // that would trip the inventory sufficiency gate on either path.
            sql = "SELECT TOP 1 M.SEND_TYPE, M.SEND_NO FROM dbo.COP_SEND_M M "
                + "WHERE ISNULL(M.CONFIRM_TAG,0)=0 AND " + noLogHistory
                + " AND EXISTS (SELECT 1 FROM dbo.COP_SEND_D D"
                + " WHERE D.SEND_TYPE=M.SEND_TYPE AND D.SEND_NO=M.SEND_NO)"
                + " AND NOT " + SendOverActiveChecks
                + " AND NOT EXISTS ("
                + "SELECT 1 FROM (SELECT D.PRO_NO, D.DEPOT_ID,"
                + " SUM(ISNULL(D.QTY,0)+ISNULL(D.SPARE_QTY,0)) QTY"
                + " FROM dbo.COP_SEND_D D WHERE D.SEND_TYPE=M.SEND_TYPE AND D.SEND_NO=M.SEND_NO"
                + " GROUP BY D.PRO_NO, D.DEPOT_ID) X"
                + " LEFT JOIN dbo.INV_PRO_DEPOT S ON S.PRO_NO=X.PRO_NO AND S.DEPOT_ID=X.DEPOT_ID"
                + " WHERE ISNULL(S.QTY,0) + 0.001 < X.QTY)"
                + " ORDER BY M.SEND_DATE DESC, M.SEND_NO DESC;";
            emptyMessage = "未找到可对拍的未批核送货单（自动选单无结果）。";
        }
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            throw new InvalidOperationException(emptyMessage);
        }
        return new[] { reader.GetString(0).Trim(), reader.GetString(1).Trim() };
    }

    private async Task<LegacyPathResult> RunLegacyPathAsync(
        SqlConnection connection,
        WorkbenchDefinition definition,
        ModuleShadowSpec spec,
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
            await SetConfirmAsync(scoped, transaction, spec, keys, deapprove);
            var keyCondition = BuildKeyCondition(spec, keys);
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
            var snapshot = await CaptureSnapshotAsync(scoped, transaction, spec, keys);
            await transaction.RollbackAsync();
            await log.WriteLineAsync($"legacy path ok rows={snapshot.Rows.Sum(entry => entry.Value.Count)}");
            if (Environment.GetEnvironmentVariable("EOS_SHADOW_DEBUG") == "1")
            {
                await DumpSnapshot(log, "legacy", snapshot);
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
        ModuleShadowSpec spec,
        IReadOnlyList<string> keys,
        bool deapprove,
        TextWriter log)
    {
        await using var connection = new SqlConnection(ConnectionString.Value);
        await connection.OpenAsync();
        var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        try
        {
            await SetConfirmAsync(connection, transaction, spec, keys, deapprove);
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
            var snapshot = await CaptureSnapshotAsync(connection, transaction, spec, keys);
            await transaction.RollbackAsync();
            await log.WriteLineAsync($"engine path ok rows={snapshot.Rows.Sum(entry => entry.Value.Count)}");
            if (Environment.GetEnvironmentVariable("EOS_SHADOW_DEBUG") == "1")
            {
                await DumpSnapshot(log, "engine", snapshot);
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
            var detail = Environment.GetEnvironmentVariable("EOS_SHADOW_DEBUG") == "1"
                ? exception.ToString()
                : exception.Message;
            return new EnginePathResult(new ShadowPathStatus("blocked", detail), null, 0, Array.Empty<string>());
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
            new IEffectServiceHandler[]
            {
                new InventoryMoveHandler(new EffectPhysicalColumns()),
                new CallbackRepriceHandler(),
                new PaymentDateCalcHandler(),
                new ClientPriceSyncHandler(),
                new SupplierPriceSyncHandler(),
                new QuoteParameterRecalcHandler(),
                new LinkStampHandler(),
                new StampLastActivityHandler(),
                new MrpPlanAllocHandler(),
                new FieldCopyHandler(),
                new SetStateHandler(),
                new BalanceAdjustHandler(),
                new OrderChangeApplyHandler(),
                new PurchaseChangeApplyHandler(),
                new ProduceChangeApplyHandler(),
                new ContractSyncHandler(),
                new HrUsageSyncHandler(),
                new MouldBatchApplyHandler(),
                new DimissionSyncHandler(),
            },
            new EffectValidationExecutor(),
            auditWriter,
            NullLogger<EffectPipeline>.Instance);
    }

    private static async Task SetConfirmAsync(
        SqlConnection connection, SqlTransaction transaction, ModuleShadowSpec spec, IReadOnlyList<string> keys, bool deapprove)
    {
        var sql = $"""
            UPDATE dbo.{spec.MasterTable}
            SET CONFIRM_PERSON=@Person, CONFIRM_DATE=SYSDATETIME(), CONFIRM_TAG=@ConfirmTag
            WHERE CONFIRM_TAG=@ExpectTag AND {spec.Key1Column}=@Key1 AND {spec.Key2Column}=@Key2;
            """;
        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@Person", SqlDbType.NVarChar, 50).Value = "shadow";
        command.Parameters.Add("@ConfirmTag", SqlDbType.Bit).Value = deapprove ? false : true;
        command.Parameters.Add("@ExpectTag", SqlDbType.Bit).Value = deapprove ? true : false;
        command.Parameters.Add("@Key1", SqlDbType.NVarChar, 20).Value = keys[0];
        command.Parameters.Add("@Key2", SqlDbType.NVarChar, 30).Value = keys[1];
        if (await command.ExecuteNonQueryAsync() == 0)
        {
            throw new InvalidOperationException(deapprove
                ? $"单据不存在或未批核，无法执行对拍解批（{spec.DocName}）。"
                : $"单据不存在或已批核，无法执行对拍批核（{spec.DocName}）。");
        }
    }

    private static string BuildKeyCondition(ModuleShadowSpec spec, IReadOnlyList<string> keys) =>
        $"[{spec.Key1Column}]='{Escape(keys[0])}' AND [{spec.Key2Column}]='{Escape(keys[1])}'";

    private static string Escape(string value) => value.Replace("'", "''");

    private static async Task<ShadowSnapshot> CaptureSnapshotAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        ModuleShadowSpec spec,
        IReadOnlyList<string> keys)
    {
        var master = await ReadMasterContextAsync(connection, transaction, spec, keys);
        var details = await ReadDetailContextAsync(connection, transaction, spec, keys);
        var specs = BuildTableSpecs(spec, master, details);
        var rows = new Dictionary<string, List<ShadowRow>>(StringComparer.Ordinal);
        foreach (var table in specs)
        {
            var captured = await ReadRowsAsync(connection, transaction, table);
            if (captured.Count > 0)
            {
                rows[table.Table] = captured.ToList();
            }
        }
        var (auditCount, auditActions) = await ReadAuditDeltaAsync(connection, transaction, keys);
        return new ShadowSnapshot(rows, auditCount, auditActions);
    }

    private static async Task<MasterContext> ReadMasterContextAsync(
        SqlConnection connection, SqlTransaction transaction, ModuleShadowSpec spec, IReadOnlyList<string> keys)
    {
        var partySelect = spec.PartyColumn is null ? "''" : $"LTRIM(RTRIM(ISNULL(M.{spec.PartyColumn},'')))";
        var sql = $"""
            SELECT M.{spec.DateColumn}, {partySelect}
            FROM dbo.{spec.MasterTable} M
            WHERE M.{spec.Key1Column}=@Key1 AND M.{spec.Key2Column}=@Key2;
            """;
        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@Key1", SqlDbType.NVarChar, 20).Value = keys[0];
        command.Parameters.Add("@Key2", SqlDbType.NVarChar, 30).Value = keys[1];
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            throw new InvalidOperationException($"{spec.DocName}主表行不存在。");
        }
        var date = reader.IsDBNull(0) ? null : (DateTime?)reader.GetDateTime(0);
        var party = reader.GetString(1);
        return new MasterContext(date, string.IsNullOrWhiteSpace(party) ? null : party, keys[0], keys[1]);
    }

    private static async Task<IReadOnlyList<DetailRow>> ReadDetailContextAsync(
        SqlConnection connection, SqlTransaction transaction, ModuleShadowSpec spec, IReadOnlyList<string> keys)
    {
        if (spec.ModuleId == 1406)
        {
            return await ReadDetailContext1406Async(connection, transaction, keys);
        }
        if (spec.ModuleId == 1505)
        {
            return await ReadDetailContext1505Async(connection, transaction, keys);
        }
        if (spec.ModuleId == 2816)
        {
            return await ReadDetailContext1505Async(connection, transaction, keys);
        }
        if (spec.ModuleId == 1407)
        {
            return await ReadDetailContext1407Async(connection, transaction, keys);
        }
        if (spec.ModuleId == 1413)
        {
            return await ReadDetailContext1413Async(connection, transaction, keys);
        }
        if (spec.ModuleId == 1610)
        {
            // Purchase callback detail rows reference external receive/cancel lines via
            // S_R_TYPE/S_R_NO/S_R_SERIAL_NO; the snapshot specs query those tables directly
            // (see BuildTableSpecs1610), so detail context rows are not materialised here.
            return Array.Empty<DetailRow>();
        }
        if (spec.ModuleId == 1404)
        {
            return await ReadDetailContext1404Async(connection, transaction, keys);
        }
        if (spec.ModuleId == 1604)
        {
            return await ReadDetailContext1604Async(connection, transaction, keys);
        }
        if (spec.ModuleId is 170101 or 170201)
        {
            // Settlement detail rows reference external delivery/receive lines via
            // S_R_* / R_C_*; the snapshot specs query those tables directly (see
            // BuildTableSpecs170101/BuildTableSpecs170201), so no context rows here.
            return Array.Empty<DetailRow>();
        }        if (spec.ModuleId == 1418)
        {
            // Change detail rows reference the original order lines via the master's
            // ORDER_TYPE/NO; the snapshot specs query those tables directly.
            return Array.Empty<DetailRow>();
        }        if (spec.ModuleId == 1609)
        {
            // Change detail rows reference the original purchase lines via the master's
            // PURCHASE_TYPE/NO; the snapshot specs query those tables directly.
            return Array.Empty<DetailRow>();
        }
        if (spec.ModuleId == 1509)
        {
            // Produce-change detail rows reference the original produce rows via the
            // master's PRODUCE_TYPE/NO; the snapshot specs query those tables directly.
            return Array.Empty<DetailRow>();
        }
        if (spec.ModuleId is 1405 or 1502)
        {
            // MRP snapshot specs filter target tables by the document keys on the
            // master rows; no detail context rows are materialised here.
            return Array.Empty<DetailRow>();
        }
        if (spec.ModuleId is 2906 or 180106 or 180206 or 180207 or 1503 or 1514 or 1517 or 2805 or 2806 or 1615 or 1616 or 2817 or 2818
            or 2705 or 2706 or 2707 or 2708 or 2815 or 2903 or 2904 or 2907 or 2913
            or 130101 or 130103 or 130104 or 130105 or 130110 or 3303 or 3901 or 170103 or 170203
            or 300301 or 300302 or 300304 or 300305 or 180310 or 1803101 or 1423)
        {
            // 2xxx snapshot specs filter target tables by document keys and EXISTS
            // subqueries off the master; no detail context rows are materialised.
            return Array.Empty<DetailRow>();
        }
        const string sql = """
            SELECT LTRIM(RTRIM(ISNULL(D.PURCHASE_TYPE,''))), LTRIM(RTRIM(ISNULL(D.PURCHASE_NO,''))),
                   D.PURCHASE_SERIAL_NO, LTRIM(RTRIM(ISNULL(D.ORDER_TYPE,''))), LTRIM(RTRIM(ISNULL(D.ORDER_NO,''))),
                   LTRIM(RTRIM(ISNULL(D.PRO_NO,''))), LTRIM(RTRIM(ISNULL(D.DEPOT_ID,''))),
                   LTRIM(RTRIM(ISNULL(D.BATCH_NO,''))), D.SERIAL_NO
            FROM dbo.PUR_RECEIVE_D D
            WHERE D.RECEIVE_TYPE=@Key1 AND D.RECEIVE_NO=@Key2
            ORDER BY D.SERIAL_NO;
            """;
        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@Key1", SqlDbType.NVarChar, 20).Value = keys[0];
        command.Parameters.Add("@Key2", SqlDbType.NVarChar, 30).Value = keys[1];
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

    private static async Task<IReadOnlyList<DetailRow>> ReadDetailContext1406Async(
        SqlConnection connection, SqlTransaction transaction, IReadOnlyList<string> keys)
    {
        const string sql = """
            SELECT LTRIM(RTRIM(ISNULL(D.ORDER_TYPE,''))), LTRIM(RTRIM(ISNULL(D.ORDER_NO,''))),
                   D.ORDER_SERIAL_NO, LTRIM(RTRIM(ISNULL(D.SHIPMENT_TYPE,''))), LTRIM(RTRIM(ISNULL(D.SHIPMENT_NO,''))),
                   D.SHIPMENT_SERIAL_NO, LTRIM(RTRIM(ISNULL(D.PRODUCE_TYPE,''))), LTRIM(RTRIM(ISNULL(D.PRODUCE_NO,''))),
                   LTRIM(RTRIM(ISNULL(D.FITOUT_TYPE,''))), LTRIM(RTRIM(ISNULL(D.FITOUT_NO,''))), D.FITOUT_SERIAL_NO,
                   LTRIM(RTRIM(ISNULL(D.PRO_NO,''))), LTRIM(RTRIM(ISNULL(D.DEPOT_ID,''))),
                   LTRIM(RTRIM(ISNULL(D.BATCH_NO,''))), D.SERIAL_NO
            FROM dbo.COP_SEND_D D
            WHERE D.SEND_TYPE=@Key1 AND D.SEND_NO=@Key2
            ORDER BY D.SERIAL_NO;
            """;
        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@Key1", SqlDbType.NVarChar, 20).Value = keys[0];
        command.Parameters.Add("@Key2", SqlDbType.NVarChar, 30).Value = keys[1];
        await using var reader = await command.ExecuteReaderAsync();
        var result = new List<DetailRow>();
        static int? SmallInt(SqlDataReader reader, int ordinal) =>
            reader.IsDBNull(ordinal) ? null : (int?)reader.GetInt16(ordinal);
        while (await reader.ReadAsync())
        {
            result.Add(new DetailRow(
                string.Empty, string.Empty, null,
                reader.GetString(0), reader.GetString(1),
                reader.GetString(11), reader.GetString(12), reader.GetString(13),
                SmallInt(reader, 14),
                OrderSerialNo: SmallInt(reader, 2),
                ShipmentType: reader.GetString(3), ShipmentNo: reader.GetString(4),
                ShipmentSerialNo: SmallInt(reader, 5),
                ProduceType: reader.GetString(6), ProduceNo: reader.GetString(7),
                FitoutType: reader.GetString(8), FitoutNo: reader.GetString(9),
                FitoutSerialNo: SmallInt(reader, 10)));
        }
        return result;
    }

    private static IReadOnlyList<TableSpec> BuildTableSpecs(ModuleShadowSpec spec, MasterContext master, IReadOnlyList<DetailRow> details)
    {
        if (spec.ModuleId == 1406)
        {
            return BuildTableSpecs1406(master, details);
        }
        if (spec.ModuleId == 1505)
        {
            return BuildTableSpecs1505(master, details);
        }
        if (spec.ModuleId == 1407)
        {
            return BuildTableSpecs1407(master, details);
        }
        if (spec.ModuleId == 1413)
        {
            return BuildTableSpecs1413(master, details);
        }
        if (spec.ModuleId == 1610)
        {
            return BuildTableSpecs1610(master, details);
        }
        if (spec.ModuleId == 170101)
        {
            return BuildTableSpecs170101(master);
        }
        if (spec.ModuleId == 170201)
        {
            return BuildTableSpecs170201(master);
        }
        if (spec.ModuleId == 1418)
        {
            return BuildTableSpecs1418(master);
        }
        if (spec.ModuleId == 1609)
        {
            return BuildTableSpecs1609(master);
        }
        if (spec.ModuleId == 1509)
        {
            return BuildTableSpecs1509(master);
        }
        if (spec.ModuleId == 1405)
        {
            return BuildTableSpecs1405(master);
        }
        if (spec.ModuleId == 1502)
        {
            return BuildTableSpecs1502(master);
        }
        if (spec.ModuleId is 1512 or 1522 or 2803 or 2804)
        {
            return BuildTableSpecs1502(master);
        }
        if (spec.ModuleId == 2816)
        {
            return BuildTableSpecs1505(master, details);
        }
        if (spec.ModuleId is 1503 or 1514 or 1517 or 2805 or 2806)
        {
            return BuildTableSpecsMaterialIssue(master);
        }
        if (spec.ModuleId is 1615 or 1616)
        {
            return BuildTableSpecs1615(master);
        }
        if (spec.ModuleId == 2817)
        {
            return BuildTableSpecsOccurInventory(master, "INV_OCCUR_IN_M", "INV_OCCUR_IN_D");
        }
        if (spec.ModuleId == 2818)
        {
            return BuildTableSpecsOccurInventory(master, "INV_OCCUR_OUT_M", "INV_OCCUR_OUT_D");
        }
        if (spec.ModuleId == 130101)
        {
            return BuildTableSpecs130101(master);
        }
        if (spec.ModuleId is 130103 or 130104 or 130110 or 3901)
        {
            return spec.ModuleId == 130103
                ? BuildTableSpecsOccurInventory(master, "INV_OCCUR_IN_M", "INV_OCCUR_IN_D")
                : BuildTableSpecsOccurInventory(master, "INV_OCCUR_OUT_M", "INV_OCCUR_OUT_D");
        }
        if (spec.ModuleId == 130105)
        {
            return BuildTableSpecs130105(master);
        }
        if (spec.ModuleId == 3303)
        {
            return BuildBasicTableSpecs(master, "QC_ANALYSIS_M", "QC_ANALYSIS_D", "ANALYSIS_TYPE", "ANALYSIS_NO", "ANALYSIS_DATE");
        }
        if (spec.ModuleId == 170103)
        {
            return BuildBasicTableSpecs(master, "COP_PREPAY_M", "COP_PREPAY_D", "PREPAY_TYPE", "PREPAY_NO", "PREPAY_DATE");
        }
        if (spec.ModuleId == 170203)
        {
            return BuildBasicTableSpecs(master, "PUR_PREPAY_M", "PUR_PREPAY_D", "PREPAY_TYPE", "PREPAY_NO", "PREPAY_DATE");
        }
        if (spec.ModuleId is 300301 or 300304)
        {
            return BuildBasicTableSpecs(master, "CUS_EXPORT_M", "CUS_EXPORT_D", "EXPORT_TYPE", "EXPORT_NO", "EXPORT_DATE");
        }
        if (spec.ModuleId is 300302 or 300305)
        {
            return BuildBasicTableSpecs(master, "CUS_IMPORT_M", "CUS_IMPORT_D", "IMPORT_TYPE", "IMPORT_NO", "IMPORT_DATE");
        }
        if (spec.ModuleId == 2705)
        {
            return BuildTableSpecs2705(master);
        }
        if (spec.ModuleId == 2706)
        {
            return BuildTableSpecs2706(master);
        }
        if (spec.ModuleId == 2707)
        {
            return BuildTableSpecs2707(master);
        }
        if (spec.ModuleId == 2708)
        {
            return BuildTableSpecs2708(master);
        }
        if (spec.ModuleId == 2815)
        {
            return BuildTableSpecs2815(master);
        }
        if (spec.ModuleId == 1423)
        {
            return BuildTableSpecs1423(master);
        }
        if (spec.ModuleId == 2903)
        {
            return BuildTableSpecs2903(master);
        }
        if (spec.ModuleId == 2904)
        {
            return BuildTableSpecs2904(master);
        }
        if (spec.ModuleId == 2907)
        {
            return BuildTableSpecs2907(master);
        }
        if (spec.ModuleId == 2913)
        {
            return BuildTableSpecs2913(master);
        }
        if (spec.ModuleId == 2906)
        {
            return BuildTableSpecs2906(master);
        }
        if (spec.ModuleId == 180106)
        {
            return BuildTableSpecs180106(master);
        }
        if (spec.ModuleId == 180206)
        {
            return BuildTableSpecs180206(master);
        }
        if (spec.ModuleId == 180207)
        {
            return BuildTableSpecs180207(master);
        }
        if (spec.ModuleId is 180310 or 1803101)
        {
            return BuildTableSpecs180310(master);
        }
        if (spec.ModuleId == 1404)
        {
            return BuildTableSpecs1404(master, details);
        }
        if (spec.ModuleId == 1604)
        {
            return BuildTableSpecs1604(master, details);
        }
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
                specs.Add(BuildBatchDetailSpec(spec.ModuleNumber, master.ReceiveDate.Value, master.ReceiveType, master.ReceiveNo, batchKeys));
            }
        }

        if (master.ReceiveDate is not null
            && details.Any(row => row.SerialNo.HasValue)
            && details.Any(row => row.ProNo.Length > 0)
            && details.Any(row => row.DepotId.Length > 0))
        {
            specs.Add(BuildLogSpec(spec.ModuleNumber, master, details));
        }
        return specs;
    }

    private static async Task<IReadOnlyList<DetailRow>> ReadDetailContext1505Async(
        SqlConnection connection, SqlTransaction transaction, IReadOnlyList<string> keys)
    {
        const string sql = """
            SELECT LTRIM(RTRIM(ISNULL(D.PRODUCE_TYPE,''))), LTRIM(RTRIM(ISNULL(D.PRODUCE_NO,''))),
                   LTRIM(RTRIM(ISNULL(D.ORDER_TYPE,''))), LTRIM(RTRIM(ISNULL(D.ORDER_NO,''))),
                   D.ORDER_SERIAL_NO,
                   LTRIM(RTRIM(ISNULL(D.PRO_NO,''))), LTRIM(RTRIM(ISNULL(D.DEPOT_ID,''))),
                   LTRIM(RTRIM(ISNULL(D.BATCH_NO,''))), D.SERIAL_NO
            FROM dbo.MOC_PRODUCT_IN_D D
            WHERE D.PRODUCT_IN_TYPE=@Key1 AND D.PRODUCT_IN_NO=@Key2
            ORDER BY D.SERIAL_NO;
            """;
        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@Key1", SqlDbType.NVarChar, 20).Value = keys[0];
        command.Parameters.Add("@Key2", SqlDbType.NVarChar, 30).Value = keys[1];
        await using var reader = await command.ExecuteReaderAsync();
        var result = new List<DetailRow>();
        static int? SmallInt(SqlDataReader reader, int ordinal) =>
            reader.IsDBNull(ordinal) ? null : (int?)reader.GetInt16(ordinal);
        while (await reader.ReadAsync())
        {
            result.Add(new DetailRow(
                string.Empty, string.Empty, null,
                reader.GetString(2), reader.GetString(3),
                reader.GetString(5), reader.GetString(6), reader.GetString(7),
                SmallInt(reader, 8),
                OrderSerialNo: SmallInt(reader, 4),
                ProduceType: reader.GetString(0), ProduceNo: reader.GetString(1)));
        }
        return result;
    }

    private static IReadOnlyList<TableSpec> BuildTableSpecs1505(MasterContext master, IReadOnlyList<DetailRow> details)
    {
        var specs = new List<TableSpec>
        {
            new("MOC_PRODUCT_IN_M", new[] { "PRODUCT_IN_TYPE", "PRODUCT_IN_NO" },
                "@t1=PRODUCT_IN_TYPE AND @t2=PRODUCT_IN_NO", new[] { new SqlParameter("@t1", master.ReceiveType), new SqlParameter("@t2", master.ReceiveNo) }),
            new("MOC_PRODUCT_IN_D", new[] { "PRODUCT_IN_TYPE", "PRODUCT_IN_NO", "SERIAL_NO" },
                "@t1=PRODUCT_IN_TYPE AND @t2=PRODUCT_IN_NO", new[] { new SqlParameter("@t1", master.ReceiveType), new SqlParameter("@t2", master.ReceiveNo) }),
        };

        var produceKeys = details
            .Where(row => row.ProduceType.Length > 0 && row.ProduceNo.Length > 0)
            .Select(row => (row.ProduceType, row.ProduceNo))
            .Distinct()
            .ToArray();
        if (produceKeys.Length > 0)
        {
            specs.Add(BuildValuesSpec("MOC_PRODUCE_M", new[] { "PRODUCE_TYPE", "PRODUCE_NO" },
                produceKeys, "PRODUCE_TYPE", "PRODUCE_NO"));
            specs.Add(BuildValuesSpec("MOC_PRODUCE_D", new[] { "PRODUCE_TYPE", "PRODUCE_NO" },
                produceKeys, "PRODUCE_TYPE", "PRODUCE_NO"));
        }

        var orderLineKeys = details
            .Where(row => row.OrderType.Length > 0 && row.OrderNo.Length > 0 && row.OrderSerialNo is not null)
            .Select(row => (row.OrderType, row.OrderNo, row.OrderSerialNo!.Value))
            .Distinct()
            .ToArray();
        if (orderLineKeys.Length > 0)
        {
            specs.Add(BuildValuesSpec("COP_ORDER_D",
                new[] { "ORDER_TYPE", "ORDER_NO", "SERIAL_NO" },
                orderLineKeys, "ORDER_TYPE", "ORDER_NO", "SERIAL_NO"));
        }

        var productKeys = details.Select(row => row.ProNo).Where(pro => pro.Length > 0).Distinct().ToArray();
        if (productKeys.Length > 0)
        {
            specs.Add(new("PRODUCT", new[] { "PRO_NO" }, InClause("PRO_NO", productKeys), productKeys.Select((value, index) => new SqlParameter("@p" + index, value)).ToArray()));
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
                specs.Add(BuildBatchDetailSpec("1505", master.ReceiveDate.Value, master.ReceiveType, master.ReceiveNo, batchKeys));
            }
        }

        if (master.ReceiveDate is not null
            && details.Any(row => row.SerialNo.HasValue)
            && details.Any(row => row.ProNo.Length > 0)
            && details.Any(row => row.DepotId.Length > 0))
        {
            specs.Add(BuildLogSpec("1505", master, details));
        }
        return specs;
    }

    private static async Task<IReadOnlyList<DetailRow>> ReadDetailContext1407Async(
        SqlConnection connection, SqlTransaction transaction, IReadOnlyList<string> keys)
    {
        const string sql = """
            SELECT LTRIM(RTRIM(ISNULL(D.ORDER_TYPE,''))), LTRIM(RTRIM(ISNULL(D.ORDER_NO,''))),
                   D.ORDER_SERIAL_NO, LTRIM(RTRIM(ISNULL(D.SEND_TYPE,''))), LTRIM(RTRIM(ISNULL(D.SEND_NO,''))),
                   D.SEND_SERIAL_NO, LTRIM(RTRIM(ISNULL(D.PRODUCE_TYPE,''))), LTRIM(RTRIM(ISNULL(D.PRODUCE_NO,''))),
                   LTRIM(RTRIM(ISNULL(D.PRO_NO,''))), LTRIM(RTRIM(ISNULL(D.DEPOT_ID,''))),
                   LTRIM(RTRIM(ISNULL(D.BAD_DEPOT_ID,''))), LTRIM(RTRIM(ISNULL(D.BATCH_NO,''))), D.SERIAL_NO
            FROM dbo.COP_RETURN_D D
            WHERE D.RETURN_TYPE=@Key1 AND D.RETURN_NO=@Key2
            ORDER BY D.SERIAL_NO;
            """;
        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@Key1", SqlDbType.NVarChar, 20).Value = keys[0];
        command.Parameters.Add("@Key2", SqlDbType.NVarChar, 30).Value = keys[1];
        await using var reader = await command.ExecuteReaderAsync();
        var result = new List<DetailRow>();
        static int? SmallInt(SqlDataReader reader, int ordinal) =>
            reader.IsDBNull(ordinal) ? null : (int?)reader.GetInt16(ordinal);
        while (await reader.ReadAsync())
        {
            result.Add(new DetailRow(
                string.Empty, string.Empty, null,
                reader.GetString(0), reader.GetString(1),
                reader.GetString(8), reader.GetString(9), reader.GetString(11),
                SmallInt(reader, 12),
                OrderSerialNo: SmallInt(reader, 2),
                ProduceType: reader.GetString(6), ProduceNo: reader.GetString(7),
                ShipmentType: reader.GetString(3), ShipmentNo: reader.GetString(4),
                ShipmentSerialNo: SmallInt(reader, 5),
                BadDepotId: reader.GetString(10)));
        }
        return result;
    }

    private static IReadOnlyList<TableSpec> BuildTableSpecs1407(MasterContext master, IReadOnlyList<DetailRow> details)
    {
        var specs = new List<TableSpec>
        {
            new("COP_RETURN_M", new[] { "RETURN_TYPE", "RETURN_NO" },
                "@rt=RETURN_TYPE AND @rn=RETURN_NO", new[] { new SqlParameter("@rt", master.ReceiveType), new SqlParameter("@rn", master.ReceiveNo) }),
            new("COP_RETURN_D", new[] { "RETURN_TYPE", "RETURN_NO", "SERIAL_NO" },
                "@rt=RETURN_TYPE AND @rn=RETURN_NO", new[] { new SqlParameter("@rt", master.ReceiveType), new SqlParameter("@rn", master.ReceiveNo) }),
        };

        var orderLineKeys = details
            .Where(row => row.OrderType.Length > 0 && row.OrderNo.Length > 0 && row.OrderSerialNo is not null)
            .Select(row => (row.OrderType, row.OrderNo, row.OrderSerialNo!.Value))
            .Distinct().ToArray();
        if (orderLineKeys.Length > 0)
        {
            specs.Add(BuildValuesSpec("COP_ORDER_D", new[] { "ORDER_TYPE", "ORDER_NO", "SERIAL_NO" },
                orderLineKeys, "ORDER_TYPE", "ORDER_NO", "SERIAL_NO"));
        }
        var sendLineKeys = details
            .Where(row => row.ShipmentType.Length > 0 && row.ShipmentNo.Length > 0 && row.ShipmentSerialNo is not null)
            .Select(row => (row.ShipmentType, row.ShipmentNo, row.ShipmentSerialNo!.Value))
            .Distinct().ToArray();
        if (sendLineKeys.Length > 0)
        {
            specs.Add(BuildValuesSpec("COP_SEND_D", new[] { "SEND_TYPE", "SEND_NO", "SERIAL_NO" },
                sendLineKeys, "SEND_TYPE", "SEND_NO", "SERIAL_NO"));
        }
        var produceKeys = details
            .Where(row => row.ProduceType.Length > 0 && row.ProduceNo.Length > 0)
            .Select(row => (row.ProduceType, row.ProduceNo))
            .Distinct().ToArray();
        if (produceKeys.Length > 0)
        {
            specs.Add(BuildValuesSpec("MOC_PRODUCE_M", new[] { "PRODUCE_TYPE", "PRODUCE_NO" },
                produceKeys, "PRODUCE_TYPE", "PRODUCE_NO"));
        }
        var productKeys = details.Select(row => row.ProNo).Where(pro => pro.Length > 0).Distinct().ToArray();
        if (productKeys.Length > 0)
        {
            specs.Add(new("PRODUCT", new[] { "PRO_NO" }, InClause("PRO_NO", productKeys), productKeys.Select((value, index) => new SqlParameter("@p" + index, value)).ToArray()));
        }
        // Inventory rows cover the normal depot and the bad depot of each line.
        var depotPairs = details
            .Where(row => row.ProNo.Length > 0)
            .SelectMany(row => new[] { (row.ProNo, row.DepotId), (row.ProNo, row.BadDepotId) })
            .Where(pair => pair.Item2.Length > 0)
            .Distinct()
            .ToArray();
        if (depotPairs.Length > 0)
        {
            specs.Add(BuildValuesSpec("INV_PRO_DEPOT", new[] { "PRO_NO", "DEPOT_ID" }, depotPairs, "PRO_NO", "DEPOT_ID"));
        }
        var batchKeys = details
            .Where(row => row.ProNo.Length > 0 && row.BatchNo.Length > 0)
            .Select(row => (row.BatchNo, row.ProNo))
            .Distinct().ToArray();
        if (batchKeys.Length > 0)
        {
            specs.Add(BuildValuesSpec("INV_BATCH_M", new[] { "BATCH_NO", "PRO_NO" }, batchKeys, "BATCH_NO", "PRO_NO"));
        }
        if (master.ReceiveDate is not null && details.Any(row => row.ProNo.Length > 0 && (row.DepotId.Length > 0 || row.BadDepotId.Length > 0)))
        {
            specs.Add(BuildLogSpec1407(master, details));
        }
        return specs;
    }

    private static TableSpec BuildLogSpec1407(MasterContext master, IReadOnlyList<DetailRow> details)
    {
        var date = master.ReceiveDate!.Value.ToString(SqlDateFormat);
        var serials = details.Where(row => row.SerialNo.HasValue)
            .Select(row => (object)row.SerialNo!.Value).Distinct().ToArray();
        var pros = details.Select(row => row.ProNo).Where(value => value.Length > 0)
            .Select(value => (object)value).Distinct().ToArray();
        var depots = details.SelectMany(row => new[] { row.DepotId, row.BadDepotId })
            .Where(value => value.Length > 0).Select(value => (object)value).Distinct().ToArray();
        var parameters = new List<SqlParameter>
        {
            new("@rdate", date),
            new("@rt", master.ReceiveType),
            new("@rn", master.ReceiveNo),
        };
        var builder = new StringBuilder("CONVERT(nvarchar(19),MUTUALITY_DATE,120)=@rdate")
            .Append(" AND ((MUTUALITY_TYPE='1407' AND MUTUALITY_NO=@rt) OR (MUTUALITY_TYPE=@rt AND MUTUALITY_NO=@rn))")
            .Append(" AND ").Append(ParameterizedIn("PRO_NO", pros, "lp", parameters))
            .Append(" AND ").Append(ParameterizedIn("DEPOT_ID", depots, "ld", parameters))
            .Append(" AND ").Append(ParameterizedIn("MUTUALITY_SERIAL_NO", serials, "ls", parameters));
        return new TableSpec(
            "INV_DEPOT_LOG",
            new[] { "PRO_NO", "MUTUALITY_DATE", "IN_OUT", "MUTUALITY_SERIAL_NO", "DEPOT_ID", "BATCH_NO" },
            builder.ToString(),
            parameters);
    }

    /// <summary>
    /// Over-return predicate for 1407 selection: the return total on an order/send line
    /// exceeds what was delivered (this-not-exceed against the delivered/send-returned caps).
    /// </summary>
    private const string ReturnOverDelivered = """
        EXISTS(
          SELECT 1
          FROM (SELECT S.ORDER_TYPE, S.ORDER_NO, S.ORDER_SERIAL_NO,
                       SUM(COALESCE(S.QTY,0)) QTY, SUM(COALESCE(S.SPARE_QTY,0)) SPARE_QTY
                FROM dbo.COP_RETURN_D S
                WHERE S.RETURN_TYPE=M.RETURN_TYPE AND S.RETURN_NO=M.RETURN_NO
                  AND ISNULL(S.ORDER_NO,'') <> ''
                GROUP BY S.ORDER_TYPE, S.ORDER_NO, S.ORDER_SERIAL_NO) G
          JOIN dbo.COP_ORDER_D T
            ON T.ORDER_TYPE=G.ORDER_TYPE AND T.ORDER_NO=G.ORDER_NO AND T.SERIAL_NO=G.ORDER_SERIAL_NO
         WHERE G.QTY > COALESCE(T.FINISHED_SEND_QTY,0)
            OR G.SPARE_QTY > COALESCE(T.FINISHED_SPARE_QTY,0))
        """;

    private static async Task<IReadOnlyList<string>> ResolveRecordKeys1407Async(
        SqlConnection connection, bool deapprove, bool failure)
    {
        const string noLogHistory = """
            NOT EXISTS (SELECT 1 FROM dbo.INV_DEPOT_LOG L
                        WHERE LTRIM(RTRIM(L.MUTUALITY_TYPE))=LTRIM(RTRIM(M.RETURN_TYPE))
                          AND LTRIM(RTRIM(L.MUTUALITY_NO))=LTRIM(RTRIM(M.RETURN_NO)))
            """;
        string sql;
        string emptyMessage;
        if (failure)
        {
            sql = "SELECT TOP 1 M.RETURN_TYPE, M.RETURN_NO FROM dbo.COP_RETURN_M M "
                + "WHERE ISNULL(M.CONFIRM_TAG,0)=0 AND " + noLogHistory
                + " AND " + ReturnOverDelivered
                + " ORDER BY M.RETURN_DATE DESC, M.RETURN_NO DESC;";
            emptyMessage = "未找到可触发失败分支的超退客户退货单。";
        }
        else if (deapprove)
        {
            sql = """
                SELECT TOP 1 M.RETURN_TYPE, M.RETURN_NO
                FROM dbo.COP_RETURN_M M
                WHERE ISNULL(M.CONFIRM_TAG,0)=1
                  AND EXISTS (SELECT 1 FROM dbo.INV_DEPOT_LOG L
                              WHERE LTRIM(RTRIM(L.MUTUALITY_TYPE))=LTRIM(RTRIM(M.RETURN_TYPE))
                                AND LTRIM(RTRIM(L.MUTUALITY_NO))=LTRIM(RTRIM(M.RETURN_NO)))
                ORDER BY ISNULL(M.CONFIRM_DATE, M.RETURN_DATE) DESC, M.RETURN_NO DESC;
                """;
            emptyMessage = "未找到可解批对拍的已批核退货单（自动选单无结果）。";
        }
        else
        {
            sql = "SELECT TOP 1 M.RETURN_TYPE, M.RETURN_NO FROM dbo.COP_RETURN_M M "
                + "WHERE ISNULL(M.CONFIRM_TAG,0)=0 AND " + noLogHistory
                + " AND EXISTS (SELECT 1 FROM dbo.COP_RETURN_D D"
                + " WHERE D.RETURN_TYPE=M.RETURN_TYPE AND D.RETURN_NO=M.RETURN_NO)"
                + " AND NOT " + ReturnOverDelivered
                + " ORDER BY M.RETURN_DATE DESC, M.RETURN_NO DESC;";
            emptyMessage = "未找到可对拍的未批核退货单（自动选单无结果）。";
        }
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            throw new InvalidOperationException(emptyMessage);
        }
        return new[] { reader.GetString(0).Trim(), reader.GetString(1).Trim() };
    }

    private static async Task<IReadOnlyList<string>> ResolveRecordKeys1413Async(
        SqlConnection connection, bool deapprove, bool failure)
    {
        if (deapprove || failure)
            throw new NotSupportedException("1413 影子规格仅支持 APPROVE（失败/解批分支未规格化）。");
        const string sql = """
            SELECT TOP 1 M.CALLBACK_TYPE, M.CALLBACK_NO
            FROM dbo.COP_CALLBACK_M M
            WHERE ISNULL(M.CONFIRM_TAG,0)=0
              AND EXISTS (SELECT 1 FROM dbo.COP_CALLBACK_D D
                          WHERE D.CALLBACK_TYPE=M.CALLBACK_TYPE AND D.CALLBACK_NO=M.CALLBACK_NO)
            ORDER BY M.CALLBACK_DATE DESC, M.CALLBACK_NO DESC;
            """;
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
            throw new InvalidOperationException("未找到可对拍的未批核回执单（1413）。");
        return new[] { reader.GetString(0).Trim(), reader.GetString(1).Trim() };
    }

    private static async Task<IReadOnlyList<DetailRow>> ReadDetailContext1413Async(
        SqlConnection connection, SqlTransaction transaction, IReadOnlyList<string> keys)
    {
        // Callback detail rows reference external delivery/return lines via S_R_TYPE/NO/SERIAL;
        // the snapshot specs query those tables directly (see BuildTableSpecs1413), so detail
        // context rows are not materialised here.
        return Array.Empty<DetailRow>();
    }

    private static async Task<IReadOnlyList<DetailRow>> ReadDetailContext1404Async(
        SqlConnection connection, SqlTransaction transaction, IReadOnlyList<string> keys)
    {
        const string sql = """
            SELECT LTRIM(RTRIM(ISNULL(D.PRO_NO,''))),
                   LTRIM(RTRIM(ISNULL(D.CHAFFER_TYPE,''))), LTRIM(RTRIM(ISNULL(D.CHAFFER_NO,''))),
                   D.CHAFFER_SERIAL_NO, D.SERIAL_NO
            FROM dbo.COP_QUOTE_D D
            WHERE D.QUOTE_TYPE=@Key1 AND D.QUOTE_NO=@Key2
            ORDER BY D.SERIAL_NO;
            """;
        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@Key1", SqlDbType.NVarChar, 20).Value = keys[0];
        command.Parameters.Add("@Key2", SqlDbType.NVarChar, 30).Value = keys[1];
        await using var reader = await command.ExecuteReaderAsync();
        var result = new List<DetailRow>();
        static int? SmallInt(SqlDataReader reader, int ordinal) =>
            reader.IsDBNull(ordinal) ? null : (int?)reader.GetInt16(ordinal);
        while (await reader.ReadAsync())
        {
            // The chaffer (inquiry) references ride in the Shipment* slots, the same
            // convention 1407 uses for its send references; the snapshot builder
            // resolves COP_CHAFFER_D rows from those slots.
            result.Add(new DetailRow(
                string.Empty, string.Empty, null,
                string.Empty, string.Empty,
                reader.GetString(0), string.Empty, string.Empty,
                SmallInt(reader, 4),
                ShipmentType: reader.GetString(1), ShipmentNo: reader.GetString(2),
                ShipmentSerialNo: SmallInt(reader, 3)));
        }
        return result;
    }

    private static async Task<IReadOnlyList<DetailRow>> ReadDetailContext1604Async(
        SqlConnection connection, SqlTransaction transaction, IReadOnlyList<string> keys)
    {
        const string sql = """
            SELECT LTRIM(RTRIM(ISNULL(D.PRO_NO,''))), D.SERIAL_NO
            FROM dbo.PUR_QUOTE_D D
            WHERE D.QUOTE_TYPE=@Key1 AND D.QUOTE_NO=@Key2
            ORDER BY D.SERIAL_NO;
            """;
        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@Key1", SqlDbType.NVarChar, 20).Value = keys[0];
        command.Parameters.Add("@Key2", SqlDbType.NVarChar, 30).Value = keys[1];
        await using var reader = await command.ExecuteReaderAsync();
        var result = new List<DetailRow>();
        while (await reader.ReadAsync())
        {
            result.Add(new DetailRow(
                string.Empty, string.Empty, null,
                string.Empty, string.Empty,
                reader.GetString(0), string.Empty, string.Empty,
                reader.IsDBNull(1) ? null : (int?)reader.GetInt16(1)));
        }
        return result;
    }

    private static IReadOnlyList<TableSpec> BuildTableSpecs1413(MasterContext master, IReadOnlyList<DetailRow> details)
    {
        var specs = new List<TableSpec>
        {
            new("COP_CALLBACK_M", new[] { "CALLBACK_TYPE", "CALLBACK_NO" },
                "@ct=CALLBACK_TYPE AND @cn=CALLBACK_NO", new[] { new SqlParameter("@ct", master.ReceiveType), new SqlParameter("@cn", master.ReceiveNo) }),
            new("COP_CALLBACK_D", new[] { "CALLBACK_TYPE", "CALLBACK_NO", "SERIAL_NO" },
                "@ct=CALLBACK_TYPE AND @cn=CALLBACK_NO", new[] { new SqlParameter("@ct", master.ReceiveType), new SqlParameter("@cn", master.ReceiveNo) }),
        };
        // Snapshot every delivery and return line the callback references (S_R_*), plus their masters.
        var refSql = "SELECT DISTINCT S_R_TYPE, S_R_NO FROM dbo.COP_CALLBACK_D WHERE CALLBACK_TYPE=@ct AND CALLBACK_NO=@cn";
        specs.Add(new("COP_SEND_D", new[] { "SEND_TYPE", "SEND_NO", "SERIAL_NO" },
            "EXISTS (SELECT 1 FROM dbo.COP_CALLBACK_D R WHERE R.CALLBACK_TYPE=@ct AND R.CALLBACK_NO=@cn "
            + "AND COP_SEND_D.SEND_TYPE=R.S_R_TYPE AND COP_SEND_D.SEND_NO=R.S_R_NO AND COP_SEND_D.SERIAL_NO=R.S_R_SERIAL_NO)",
            new[] { new SqlParameter("@ct", master.ReceiveType), new SqlParameter("@cn", master.ReceiveNo) }));
        specs.Add(new("COP_SEND_M", new[] { "SEND_TYPE", "SEND_NO" },
            "EXISTS (SELECT 1 FROM dbo.COP_CALLBACK_D R WHERE R.CALLBACK_TYPE=@ct AND R.CALLBACK_NO=@cn "
            + "AND COP_SEND_M.SEND_TYPE=R.S_R_TYPE AND COP_SEND_M.SEND_NO=R.S_R_NO)",
            new[] { new SqlParameter("@ct", master.ReceiveType), new SqlParameter("@cn", master.ReceiveNo) }));
        specs.Add(new("COP_RETURN_D", new[] { "RETURN_TYPE", "RETURN_NO", "SERIAL_NO" },
            "EXISTS (SELECT 1 FROM dbo.COP_CALLBACK_D R WHERE R.CALLBACK_TYPE=@ct AND R.CALLBACK_NO=@cn "
            + "AND COP_RETURN_D.RETURN_TYPE=R.S_R_TYPE AND COP_RETURN_D.RETURN_NO=R.S_R_NO AND COP_RETURN_D.SERIAL_NO=R.S_R_SERIAL_NO)",
            new[] { new SqlParameter("@ct", master.ReceiveType), new SqlParameter("@cn", master.ReceiveNo) }));
        specs.Add(new("COP_RETURN_M", new[] { "RETURN_TYPE", "RETURN_NO" },
            "EXISTS (SELECT 1 FROM dbo.COP_CALLBACK_D R WHERE R.CALLBACK_TYPE=@ct AND R.CALLBACK_NO=@cn "
            + "AND COP_RETURN_M.RETURN_TYPE=R.S_R_TYPE AND COP_RETURN_M.RETURN_NO=R.S_R_NO)",
            new[] { new SqlParameter("@ct", master.ReceiveType), new SqlParameter("@cn", master.ReceiveNo) }));
        return specs;
    }

    /// <summary>
    /// 1610 (收料核价单) sample selection. A callback is comparable when its detail rows
    /// point at existing receive or cancel lines — otherwise both paths would update
    /// nothing and the comparison would be meaningless. Deapprove is a no-op on both
    /// sides (the action is configured with deapprove=none and the legacy procedure only
    /// resets the confirm flag), so it proves zero residue rather than equality of an
    /// effect. The failure branch does not exist: the module carries no validation rule
    /// and the legacy procedure has no failure exit.
    /// </summary>
    private static async Task<IReadOnlyList<string>> ResolveRecordKeys1610Async(
        SqlConnection connection, bool deapprove, bool failure)
    {
        if (failure)
        {
            throw new NotSupportedException(
                "1610 影子规格不支持失败分支（模块无校验规则，旧存储过程亦无失败出口）。");
        }
        const string approveSql = """
            SELECT TOP 1 M.CALLBACK_TYPE, M.CALLBACK_NO
            FROM dbo.PUR_CALLBACK_M M
            WHERE ISNULL(M.CONFIRM_TAG,0)=0
              AND EXISTS (SELECT 1 FROM dbo.PUR_CALLBACK_D D
                          WHERE D.CALLBACK_TYPE=M.CALLBACK_TYPE AND D.CALLBACK_NO=M.CALLBACK_NO
                            AND (EXISTS (SELECT 1 FROM dbo.PUR_RECEIVE_D R
                                         WHERE R.RECEIVE_TYPE=D.S_R_TYPE AND R.RECEIVE_NO=D.S_R_NO
                                           AND R.SERIAL_NO=D.S_R_SERIAL_NO)
                              OR EXISTS (SELECT 1 FROM dbo.PUR_CANCEL_D C
                                         WHERE C.CANCEL_TYPE=D.S_R_TYPE AND C.CANCEL_NO=D.S_R_NO
                                           AND C.SERIAL_NO=D.S_R_SERIAL_NO)))
            ORDER BY M.CALLBACK_DATE DESC, M.CALLBACK_NO DESC;
            """;
        const string deapproveSql = """
            SELECT TOP 1 M.CALLBACK_TYPE, M.CALLBACK_NO
            FROM dbo.PUR_CALLBACK_M M
            WHERE ISNULL(M.CONFIRM_TAG,0)=1
              AND EXISTS (SELECT 1 FROM dbo.PUR_CALLBACK_D D
                          WHERE D.CALLBACK_TYPE=M.CALLBACK_TYPE AND D.CALLBACK_NO=M.CALLBACK_NO)
            ORDER BY ISNULL(M.CONFIRM_DATE, M.CALLBACK_DATE) DESC, M.CALLBACK_NO DESC;
            """;
        await using var command = new SqlCommand(deapprove ? deapproveSql : approveSql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            throw new InvalidOperationException(deapprove
                ? "未找到可对拍的已批核收料核价单（1610，自动选单无结果）。"
                : "未找到可对拍的未批核收料核价单（1610，明细引用行不存在）。");
        }
        return new[] { reader.GetString(0).Trim(), reader.GetString(1).Trim() };
    }

    /// <summary>
    /// 1610 snapshot scope: the callback document itself plus every receive and cancel
    /// line it references (S_R_*) and their masters. The legacy procedure reprices the
    /// receive lines and — only when no receive master was touched (its @@ROWCOUNT gate)
    /// — the cancel lines, while the engine reprices both target families.
    /// </summary>
    private static IReadOnlyList<TableSpec> BuildTableSpecs1610(MasterContext master, IReadOnlyList<DetailRow> details)
    {
        var parameters = new[]
        {
            new SqlParameter("@ct", master.ReceiveType),
            new SqlParameter("@cn", master.ReceiveNo),
        };
        return new List<TableSpec>
        {
            new("PUR_CALLBACK_M", new[] { "CALLBACK_TYPE", "CALLBACK_NO" },
                "@ct=CALLBACK_TYPE AND @cn=CALLBACK_NO", parameters),
            new("PUR_CALLBACK_D", new[] { "CALLBACK_TYPE", "CALLBACK_NO", "SERIAL_NO" },
                "@ct=CALLBACK_TYPE AND @cn=CALLBACK_NO", parameters),
            new("PUR_RECEIVE_D", new[] { "RECEIVE_TYPE", "RECEIVE_NO", "SERIAL_NO" },
                "EXISTS (SELECT 1 FROM dbo.PUR_CALLBACK_D R WHERE R.CALLBACK_TYPE=@ct AND R.CALLBACK_NO=@cn "
                + "AND PUR_RECEIVE_D.RECEIVE_TYPE=R.S_R_TYPE AND PUR_RECEIVE_D.RECEIVE_NO=R.S_R_NO "
                + "AND PUR_RECEIVE_D.SERIAL_NO=R.S_R_SERIAL_NO)", parameters),
            new("PUR_RECEIVE_M", new[] { "RECEIVE_TYPE", "RECEIVE_NO" },
                "EXISTS (SELECT 1 FROM dbo.PUR_CALLBACK_D R WHERE R.CALLBACK_TYPE=@ct AND R.CALLBACK_NO=@cn "
                + "AND PUR_RECEIVE_M.RECEIVE_TYPE=R.S_R_TYPE AND PUR_RECEIVE_M.RECEIVE_NO=R.S_R_NO)", parameters),
            new("PUR_CANCEL_D", new[] { "CANCEL_TYPE", "CANCEL_NO", "SERIAL_NO" },
                "EXISTS (SELECT 1 FROM dbo.PUR_CALLBACK_D R WHERE R.CALLBACK_TYPE=@ct AND R.CALLBACK_NO=@cn "
                + "AND PUR_CANCEL_D.CANCEL_TYPE=R.S_R_TYPE AND PUR_CANCEL_D.CANCEL_NO=R.S_R_NO "
                + "AND PUR_CANCEL_D.SERIAL_NO=R.S_R_SERIAL_NO)", parameters),
            new("PUR_CANCEL_M", new[] { "CANCEL_TYPE", "CANCEL_NO" },
                "EXISTS (SELECT 1 FROM dbo.PUR_CALLBACK_D R WHERE R.CALLBACK_TYPE=@ct AND R.CALLBACK_NO=@cn "
                + "AND PUR_CANCEL_M.CANCEL_TYPE=R.S_R_TYPE AND PUR_CANCEL_M.CANCEL_NO=R.S_R_NO)", parameters),
        };
    }

    private static async Task<IReadOnlyList<string>> ResolveRecordKeys170101Async(
        SqlConnection connection, bool deapprove, bool failure)
    {
        if (deapprove || failure)
        {
            throw new NotSupportedException(
                "170101 影子规格仅支持 APPROVE（解批含新语义清空预计收款日，与旧 SP 不回滚不同；失败分支未规格化）。");
        }
        const string sql = """
            SELECT TOP 1 M.ACCOUNT_TYPE, M.ACCOUNT_NO
            FROM dbo.COP_ACCOUNT_M M
            WHERE ISNULL(M.CONFIRM_TAG,0)=0
              AND EXISTS (SELECT 1 FROM dbo.COP_ACCOUNT_D D
                          WHERE D.ACCOUNT_TYPE=M.ACCOUNT_TYPE AND D.ACCOUNT_NO=M.ACCOUNT_NO)
              AND NOT EXISTS (
                  SELECT 1 FROM dbo.COP_ACCOUNT_D A
                  JOIN dbo.COP_SEND_D S
                    ON S.SEND_TYPE=A.S_R_TYPE AND S.SEND_NO=A.S_R_NO AND S.SERIAL_NO=A.S_R_SERIAL_NO
                  WHERE A.ACCOUNT_TYPE=M.ACCOUNT_TYPE AND A.ACCOUNT_NO=M.ACCOUNT_NO
                    AND ISNULL(S.FINISHED_QTY,0) + A.QTY > ISNULL(S.QTY,0))
              AND NOT EXISTS (
                  SELECT 1 FROM dbo.COP_ACCOUNT_D A
                  JOIN dbo.COP_RETURN_D R
                    ON R.RETURN_TYPE=A.S_R_TYPE AND R.RETURN_NO=A.S_R_NO AND R.SERIAL_NO=A.S_R_SERIAL_NO
                  WHERE A.ACCOUNT_TYPE=M.ACCOUNT_TYPE AND A.ACCOUNT_NO=M.ACCOUNT_NO
                    AND ISNULL(R.FINISHED_QTY,0) + A.QTY > ISNULL(R.QTY,0))
            ORDER BY M.ACCOUNT_DATE DESC, M.ACCOUNT_NO DESC;
            """;
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            throw new InvalidOperationException("未找到可对拍的未批核客户对账单（自动选单无结果）。");
        }
        return new[] { reader.GetString(0).Trim(), reader.GetString(1).Trim() };
    }

    private static async Task<IReadOnlyList<string>> ResolveRecordKeys170201Async(
        SqlConnection connection, bool deapprove, bool failure)
    {
        if (deapprove || failure)
        {
            throw new NotSupportedException(
                "170201 影子规格仅支持 APPROVE（解批含新语义清空预计付款日，与旧 SP 不回滚不同；失败分支未规格化）。");
        }
        const string sql = """
            SELECT TOP 1 M.DUE_TYPE, M.DUE_NO
            FROM dbo.PUR_DUE_M M
            WHERE ISNULL(M.CONFIRM_TAG,0)=0
              AND EXISTS (SELECT 1 FROM dbo.PUR_DUE_D D
                          WHERE D.DUE_TYPE=M.DUE_TYPE AND D.DUE_NO=M.DUE_NO)
              AND NOT EXISTS (
                  SELECT 1 FROM dbo.PUR_DUE_D A
                  JOIN dbo.PUR_RECEIVE_D S
                    ON S.RECEIVE_TYPE=A.R_C_TYPE AND S.RECEIVE_NO=A.R_C_NO AND S.SERIAL_NO=A.R_C_SERIAL_NO
                  WHERE A.DUE_TYPE=M.DUE_TYPE AND A.DUE_NO=M.DUE_NO
                    AND ISNULL(S.FINISHED_QTY,0) + A.QTY > ISNULL(S.QTY,0))
              AND NOT EXISTS (
                  SELECT 1 FROM dbo.PUR_DUE_D A
                  JOIN dbo.PUR_CANCEL_D R
                    ON R.CANCEL_TYPE=A.R_C_TYPE AND R.CANCEL_NO=A.R_C_NO AND R.SERIAL_NO=A.R_C_SERIAL_NO
                  WHERE A.DUE_TYPE=M.DUE_TYPE AND A.DUE_NO=M.DUE_NO
                    AND ISNULL(R.FINISHED_QTY,0) + A.QTY > ISNULL(R.QTY,0))
            ORDER BY M.DUE_DATE DESC, M.DUE_NO DESC;
            """;
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            throw new InvalidOperationException("未找到可对拍的未批核厂商对账单（自动选单无结果）。");
        }
        return new[] { reader.GetString(0).Trim(), reader.GetString(1).Trim() };
    }

    private static async Task<IReadOnlyList<string>> ResolveRecordKeys1404Async(
        SqlConnection connection, bool deapprove, bool failure)
    {
        if (deapprove || failure)
        {
            // Deapprove restores the stored old price behind a latest-quote guard and
            // leaves the chaffer link stamps in place, while the engine reverse kinds
            // (restore-old-price / clear-refs) have not been compared against that
            // legacy behaviour yet; no APPROVE validation rule is configured either,
            // so only the approve path is specified (same trade-off as 170101/170201).
            throw new NotSupportedException(
                "1404 影子规格仅支持 APPROVE（解批/失败分支的等价性尚未分析）。");
        }
        const string sql = """
            SELECT TOP 1 M.QUOTE_TYPE, M.QUOTE_NO
            FROM dbo.COP_QUOTE_M M
            WHERE ISNULL(M.CONFIRM_TAG,0)=0
              AND EXISTS (SELECT 1 FROM dbo.COP_QUOTE_D D
                          WHERE D.QUOTE_TYPE=M.QUOTE_TYPE AND D.QUOTE_NO=M.QUOTE_NO)
            ORDER BY M.QUOTE_DATE DESC, M.QUOTE_NO DESC;
            """;
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            throw new InvalidOperationException("未找到可对拍的未批核客户报价单（自动选单无结果）。");
        }
        return new[] { reader.GetString(0).Trim(), reader.GetString(1).Trim() };
    }

    private static async Task<IReadOnlyList<string>> ResolveRecordKeys1604Async(
        SqlConnection connection, bool deapprove, bool failure)
    {
        if (deapprove || failure)
        {
            // Deapprove shares the recalc-confirmed cost-parameter semantics with
            // approve and restores the stored old price; that equivalence has not
            // been compared against the legacy procedure yet, and no APPROVE
            // validation rule is configured, so only approve is specified.
            throw new NotSupportedException(
                "1604 影子规格仅支持 APPROVE（解批/失败分支的等价性尚未分析）。");
        }
        const string sql = """
            SELECT TOP 1 M.QUOTE_TYPE, M.QUOTE_NO
            FROM dbo.PUR_QUOTE_M M
            WHERE ISNULL(M.CONFIRM_TAG,0)=0
              AND EXISTS (SELECT 1 FROM dbo.PUR_QUOTE_D D
                          WHERE D.QUOTE_TYPE=M.QUOTE_TYPE AND D.QUOTE_NO=M.QUOTE_NO)
            ORDER BY M.QUOTE_DATE DESC, M.QUOTE_NO DESC;
            """;
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            throw new InvalidOperationException("未找到可对拍的未批核厂商报价单（自动选单无结果）。");
        }
        return new[] { reader.GetString(0).Trim(), reader.GetString(1).Trim() };
    }

    private static IReadOnlyList<TableSpec> BuildTableSpecs1404(MasterContext master, IReadOnlyList<DetailRow> details)
    {
        var specs = new List<TableSpec>
        {
            new("COP_QUOTE_M", new[] { "QUOTE_TYPE", "QUOTE_NO" },
                "@qt=QUOTE_TYPE AND @qn=QUOTE_NO", new[] { new SqlParameter("@qt", master.ReceiveType), new SqlParameter("@qn", master.ReceiveNo) }),
            new("COP_QUOTE_D", new[] { "QUOTE_TYPE", "QUOTE_NO", "SERIAL_NO" },
                "@qt=QUOTE_TYPE AND @qn=QUOTE_NO", new[] { new SqlParameter("@qt", master.ReceiveType), new SqlParameter("@qn", master.ReceiveNo) }),
        };
        // The whole party price book is snapshotted: the sync updates matched lines
        // by key and inserts missing ones, so scoping to the quoted products only
        // would miss pre-existing rows the legacy procedure also rewrites.
        if (master.SupplierId is not null)
        {
            specs.Add(new("CLIENT_PRICE_M", new[] { "CLIENT_ID" }, "CLIENT_ID=@cid", new[] { new SqlParameter("@cid", master.SupplierId) }));
            specs.Add(new("CLIENT_PRICE_D", new[] { "CLIENT_ID", "PRO_NO", "CURR_ID", "TAX_ID", "TAX_TYPE", "UNIT_ID", "REBATE" },
                "CLIENT_ID=@cid", new[] { new SqlParameter("@cid", master.SupplierId) }));
            specs.Add(new("CLIENT", new[] { "CLIENT_ID" }, "CLIENT_ID=@cid", new[] { new SqlParameter("@cid", master.SupplierId) }));
        }
        var productKeys = details.Select(row => row.ProNo).Where(pro => pro.Length > 0).Distinct().ToArray();
        if (productKeys.Length > 0)
        {
            specs.Add(new("PRODUCT", new[] { "PRO_NO" }, InClause("PRO_NO", productKeys), productKeys.Select((value, index) => new SqlParameter("@p" + index, value)).ToArray()));
        }
        // Inquiry lines the quote answers (link-stamp target), located through the
        // chaffer references carried in the Shipment* detail slots.
        var chafferKeys = details
            .Where(row => row.ShipmentType.Length > 0 && row.ShipmentNo.Length > 0 && row.ShipmentSerialNo is not null)
            .Select(row => (row.ShipmentType, row.ShipmentNo, row.ShipmentSerialNo!.Value))
            .Distinct()
            .ToArray();
        if (chafferKeys.Length > 0)
        {
            specs.Add(BuildValuesSpec("COP_CHAFFER_D",
                new[] { "CHAFFER_TYPE", "CHAFFER_NO", "SERIAL_NO" },
                chafferKeys, "CHAFFER_TYPE", "CHAFFER_NO", "SERIAL_NO"));
        }
        return specs;
    }

    private static IReadOnlyList<TableSpec> BuildTableSpecs1604(MasterContext master, IReadOnlyList<DetailRow> details)
    {
        var specs = new List<TableSpec>
        {
            new("PUR_QUOTE_M", new[] { "QUOTE_TYPE", "QUOTE_NO" },
                "@qt=QUOTE_TYPE AND @qn=QUOTE_NO", new[] { new SqlParameter("@qt", master.ReceiveType), new SqlParameter("@qn", master.ReceiveNo) }),
            new("PUR_QUOTE_D", new[] { "QUOTE_TYPE", "QUOTE_NO", "SERIAL_NO" },
                "@qt=QUOTE_TYPE AND @qn=QUOTE_NO", new[] { new SqlParameter("@qt", master.ReceiveType), new SqlParameter("@qn", master.ReceiveNo) }),
        };
        if (master.SupplierId is not null)
        {
            specs.Add(new("SUPPLIER_PRICE_M", new[] { "SUPPLIER_ID" }, "SUPPLIER_ID=@sid", new[] { new SqlParameter("@sid", master.SupplierId) }));
            specs.Add(new("SUPPLIER_PRICE_D", new[] { "SUPPLIER_ID", "PRO_NO", "CURR_ID", "TAX_ID", "TAX_TYPE", "UNIT_ID", "REBATE" },
                "SUPPLIER_ID=@sid", new[] { new SqlParameter("@sid", master.SupplierId) }));
            specs.Add(new("SUPPLIER", new[] { "SUPPLIER_ID" }, "SUPPLIER_ID=@sid", new[] { new SqlParameter("@sid", master.SupplierId) }));
        }
        var productKeys = details.Select(row => row.ProNo).Where(pro => pro.Length > 0).Distinct().ToArray();
        if (productKeys.Length > 0)
        {
            // Material-quoted lines update every product sharing the same STUFF_ID,
            // so the snapshot covers both direct and material-matched products.
            var parameters = new List<SqlParameter>();
            var proFilter = ParameterizedIn("PRO_NO", productKeys.Select(value => (object)value).ToArray(), "qp", parameters);
            var stuffFilter = ParameterizedIn("STUFF_ID", productKeys.Select(value => (object)value).ToArray(), "qs", parameters);
            specs.Add(new TableSpec("PRODUCT", new[] { "PRO_NO" }, proFilter + " OR " + stuffFilter, parameters));
            var paramParameters = new List<SqlParameter>();
            var paramFilter = ParameterizedIn("STUFF_ID", productKeys.Select(value => (object)value).ToArray(), "cp", paramParameters);
            specs.Add(new TableSpec("COP_QUOTE_PARAMETER", new[] { "STUFF_ID" }, paramFilter, paramParameters));
        }
        return specs;
    }

    private static IReadOnlyList<TableSpec> BuildTableSpecs170101(MasterContext master)
    {
        var specs = new List<TableSpec>
        {
            new("COP_ACCOUNT_M", new[] { "ACCOUNT_TYPE", "ACCOUNT_NO" },
                "@at=ACCOUNT_TYPE AND @an=ACCOUNT_NO", new[] { new SqlParameter("@at", master.ReceiveType), new SqlParameter("@an", master.ReceiveNo) }),
            new("COP_ACCOUNT_D", new[] { "ACCOUNT_TYPE", "ACCOUNT_NO", "SERIAL_NO" },
                "@at=ACCOUNT_TYPE AND @an=ACCOUNT_NO", new[] { new SqlParameter("@at", master.ReceiveType), new SqlParameter("@an", master.ReceiveNo) }),
        };
        // Snapshot every delivery and return line the settlement references (S_R_*),
        // plus their masters; the write-back and completion effects land there.
        specs.Add(new("COP_SEND_D", new[] { "SEND_TYPE", "SEND_NO", "SERIAL_NO" },
            "EXISTS (SELECT 1 FROM dbo.COP_ACCOUNT_D R WHERE R.ACCOUNT_TYPE=@at AND R.ACCOUNT_NO=@an "
            + "AND COP_SEND_D.SEND_TYPE=R.S_R_TYPE AND COP_SEND_D.SEND_NO=R.S_R_NO AND COP_SEND_D.SERIAL_NO=R.S_R_SERIAL_NO)",
            new[] { new SqlParameter("@at", master.ReceiveType), new SqlParameter("@an", master.ReceiveNo) }));
        specs.Add(new("COP_SEND_M", new[] { "SEND_TYPE", "SEND_NO" },
            "EXISTS (SELECT 1 FROM dbo.COP_ACCOUNT_D R WHERE R.ACCOUNT_TYPE=@at AND R.ACCOUNT_NO=@an "
            + "AND COP_SEND_M.SEND_TYPE=R.S_R_TYPE AND COP_SEND_M.SEND_NO=R.S_R_NO)",
            new[] { new SqlParameter("@at", master.ReceiveType), new SqlParameter("@an", master.ReceiveNo) }));
        specs.Add(new("COP_RETURN_D", new[] { "RETURN_TYPE", "RETURN_NO", "SERIAL_NO" },
            "EXISTS (SELECT 1 FROM dbo.COP_ACCOUNT_D R WHERE R.ACCOUNT_TYPE=@at AND R.ACCOUNT_NO=@an "
            + "AND COP_RETURN_D.RETURN_TYPE=R.S_R_TYPE AND COP_RETURN_D.RETURN_NO=R.S_R_NO AND COP_RETURN_D.SERIAL_NO=R.S_R_SERIAL_NO)",
            new[] { new SqlParameter("@at", master.ReceiveType), new SqlParameter("@an", master.ReceiveNo) }));
        specs.Add(new("COP_RETURN_M", new[] { "RETURN_TYPE", "RETURN_NO" },
            "EXISTS (SELECT 1 FROM dbo.COP_ACCOUNT_D R WHERE R.ACCOUNT_TYPE=@at AND R.ACCOUNT_NO=@an "
            + "AND COP_RETURN_M.RETURN_TYPE=R.S_R_TYPE AND COP_RETURN_M.RETURN_NO=R.S_R_NO)",
            new[] { new SqlParameter("@at", master.ReceiveType), new SqlParameter("@an", master.ReceiveNo) }));
        return specs;
    }

    private static IReadOnlyList<TableSpec> BuildTableSpecs170201(MasterContext master)
    {
        var specs = new List<TableSpec>
        {
            new("PUR_DUE_M", new[] { "DUE_TYPE", "DUE_NO" },
                "@dt=DUE_TYPE AND @dn=DUE_NO", new[] { new SqlParameter("@dt", master.ReceiveType), new SqlParameter("@dn", master.ReceiveNo) }),
            new("PUR_DUE_D", new[] { "DUE_TYPE", "DUE_NO", "SERIAL_NO" },
                "@dt=DUE_TYPE AND @dn=DUE_NO", new[] { new SqlParameter("@dt", master.ReceiveType), new SqlParameter("@dn", master.ReceiveNo) }),
        };
        // Snapshot every receive and cancel line the settlement references (R_C_*),
        // plus their masters; the write-back and completion effects land there.
        specs.Add(new("PUR_RECEIVE_D", new[] { "RECEIVE_TYPE", "RECEIVE_NO", "SERIAL_NO" },
            "EXISTS (SELECT 1 FROM dbo.PUR_DUE_D R WHERE R.DUE_TYPE=@dt AND R.DUE_NO=@dn "
            + "AND PUR_RECEIVE_D.RECEIVE_TYPE=R.R_C_TYPE AND PUR_RECEIVE_D.RECEIVE_NO=R.R_C_NO AND PUR_RECEIVE_D.SERIAL_NO=R.R_C_SERIAL_NO)",
            new[] { new SqlParameter("@dt", master.ReceiveType), new SqlParameter("@dn", master.ReceiveNo) }));
        specs.Add(new("PUR_RECEIVE_M", new[] { "RECEIVE_TYPE", "RECEIVE_NO" },
            "EXISTS (SELECT 1 FROM dbo.PUR_DUE_D R WHERE R.DUE_TYPE=@dt AND R.DUE_NO=@dn "
            + "AND PUR_RECEIVE_M.RECEIVE_TYPE=R.R_C_TYPE AND PUR_RECEIVE_M.RECEIVE_NO=R.R_C_NO)",
            new[] { new SqlParameter("@dt", master.ReceiveType), new SqlParameter("@dn", master.ReceiveNo) }));
        specs.Add(new("PUR_CANCEL_D", new[] { "CANCEL_TYPE", "CANCEL_NO", "SERIAL_NO" },
            "EXISTS (SELECT 1 FROM dbo.PUR_DUE_D R WHERE R.DUE_TYPE=@dt AND R.DUE_NO=@dn "
            + "AND PUR_CANCEL_D.CANCEL_TYPE=R.R_C_TYPE AND PUR_CANCEL_D.CANCEL_NO=R.R_C_NO AND PUR_CANCEL_D.SERIAL_NO=R.R_C_SERIAL_NO)",
            new[] { new SqlParameter("@dt", master.ReceiveType), new SqlParameter("@dn", master.ReceiveNo) }));
        specs.Add(new("PUR_CANCEL_M", new[] { "CANCEL_TYPE", "CANCEL_NO" },
            "EXISTS (SELECT 1 FROM dbo.PUR_DUE_D R WHERE R.DUE_TYPE=@dt AND R.DUE_NO=@dn "
            + "AND PUR_CANCEL_M.CANCEL_TYPE=R.R_C_TYPE AND PUR_CANCEL_M.CANCEL_NO=R.R_C_NO)",
            new[] { new SqlParameter("@dt", master.ReceiveType), new SqlParameter("@dn", master.ReceiveNo) }));
        return specs;
    }

    private static async Task<IReadOnlyList<string>> ResolveRecordKeys1418Async(
        SqlConnection connection, bool deapprove, bool failure)
    {
        if (failure)
        {
            throw new NotSupportedException("1418 影子规格未规格化失败分支（模块无校验规则）。");
        }
        if (deapprove)
        {
            const string sql = """
                SELECT TOP 1 M.CHANGE_ORDER_TYPE, M.CHANGE_ORDER_NO
                FROM dbo.COP_ORDER_CHANGE_M M
                WHERE ISNULL(M.CONFIRM_TAG,0)=1
                  AND EXISTS (SELECT 1 FROM dbo.COP_ORDER_CHANGE_D D
                              WHERE D.CHANGE_ORDER_TYPE=M.CHANGE_ORDER_TYPE AND D.CHANGE_ORDER_NO=M.CHANGE_ORDER_NO)
                ORDER BY ISNULL(M.CONFIRM_DATE, M.CHANGE_ORDER_DATE) DESC;
                """;
            await using var command = new SqlCommand(sql, connection);
            await using var reader = await command.ExecuteReaderAsync();
            if (!await reader.ReadAsync())
                throw new InvalidOperationException("未找到可解批对拍的已批核订单变更单（自动选单无结果）。");
            return new[] { reader.GetString(0).Trim(), reader.GetString(1).Trim() };
        }
        const string approveSql = """
            SELECT TOP 1 M.CHANGE_ORDER_TYPE, M.CHANGE_ORDER_NO
            FROM dbo.COP_ORDER_CHANGE_M M
            WHERE ISNULL(M.CONFIRM_TAG,0)=0
              AND EXISTS (SELECT 1 FROM dbo.COP_ORDER_CHANGE_D D
                          WHERE D.CHANGE_ORDER_TYPE=M.CHANGE_ORDER_TYPE AND D.CHANGE_ORDER_NO=M.CHANGE_ORDER_NO)
            ORDER BY M.CHANGE_ORDER_DATE DESC;
            """;
        await using var approveCommand = new SqlCommand(approveSql, connection);
        await using var approveReader = await approveCommand.ExecuteReaderAsync();
        if (!await approveReader.ReadAsync())
            throw new InvalidOperationException("未找到可对拍的未批核订单变更单（自动选单无结果）。");
        return new[] { approveReader.GetString(0).Trim(), approveReader.GetString(1).Trim() };
    }

    private static IReadOnlyList<TableSpec> BuildTableSpecs1418(MasterContext master)
    {
        var ct = new SqlParameter("@ct", master.ReceiveType);
        var cn = new SqlParameter("@cn", master.ReceiveNo);
        var specs = new List<TableSpec>
        {
            new("COP_ORDER_CHANGE_M", new[] { "CHANGE_ORDER_TYPE", "CHANGE_ORDER_NO" },
                "@ct=CHANGE_ORDER_TYPE AND @cn=CHANGE_ORDER_NO", new[] { ct, cn }),
            new("COP_ORDER_CHANGE_D", new[] { "CHANGE_ORDER_TYPE", "CHANGE_ORDER_NO", "SERIAL_NO" },
                "@ct=CHANGE_ORDER_TYPE AND @cn=CHANGE_ORDER_NO", new[] { ct, cn }),
        };
        // Original order master/detail rows referenced by the change master, plus the
        // documents whose CLIENT_ORDER_NO is stamped by field-copy.
        specs.Add(new("COP_ORDER_M", new[] { "ORDER_TYPE", "ORDER_NO" },
            "EXISTS (SELECT 1 FROM dbo.COP_ORDER_CHANGE_M R WHERE R.CHANGE_ORDER_TYPE=@ct AND R.CHANGE_ORDER_NO=@cn "
            + "AND COP_ORDER_M.ORDER_TYPE=R.ORDER_TYPE AND COP_ORDER_M.ORDER_NO=R.ORDER_NO)",
            new[] { ct, cn }));
        specs.Add(new("COP_ORDER_D", new[] { "ORDER_TYPE", "ORDER_NO", "SERIAL_NO" },
            "EXISTS (SELECT 1 FROM dbo.COP_ORDER_CHANGE_M R WHERE R.CHANGE_ORDER_TYPE=@ct AND R.CHANGE_ORDER_NO=@cn "
            + "AND COP_ORDER_D.ORDER_TYPE=R.ORDER_TYPE AND COP_ORDER_D.ORDER_NO=R.ORDER_NO)",
            new[] { ct, cn }));
        specs.Add(new("CLIENT", new[] { "CLIENT_ID" },
            "EXISTS (SELECT 1 FROM dbo.COP_ORDER_CHANGE_M R WHERE R.CHANGE_ORDER_TYPE=@ct AND R.CHANGE_ORDER_NO=@cn "
            + "AND CLIENT.CLIENT_ID=R.CLIENT_ID)",
            new[] { ct, cn }));
        specs.Add(new("PRODUCT", new[] { "PRO_NO" },
            "EXISTS (SELECT 1 FROM dbo.COP_ORDER_CHANGE_D R WHERE R.CHANGE_ORDER_TYPE=@ct AND R.CHANGE_ORDER_NO=@cn "
            + "AND PRODUCT.PRO_NO=R.PRO_NO)",
            new[] { ct, cn }));
        foreach (var table in new[] { "PUR_APPLY_D", "PUR_PURCHASE_D", "PUR_PURCHASE_CHANGE_D" })
        {
            specs.Add(new(table, new[] { "ORDER_TYPE", "ORDER_NO", "SERIAL_NO" },
                "EXISTS (SELECT 1 FROM dbo.COP_ORDER_CHANGE_M R WHERE R.CHANGE_ORDER_TYPE=@ct AND R.CHANGE_ORDER_NO=@cn "
                + "AND " + table + ".ORDER_TYPE=R.ORDER_TYPE AND " + table + ".ORDER_NO=R.ORDER_NO)",
                new[] { ct, cn }));
        }
        return specs;
    }

    private static async Task<IReadOnlyList<string>> ResolveRecordKeys1609Async(
        SqlConnection connection, bool deapprove, bool failure)
    {
        if (failure)
        {
            throw new NotSupportedException("1609 影子规格未规格化失败分支（模块无校验规则）。");
        }
        var confirm = deapprove ? "1" : "0";
        const string sql = """
            SELECT TOP 1 M.CHANGE_PURCHASE_TYPE, M.CHANGE_PURCHASE_NO
            FROM dbo.PUR_PURCHASE_CHANGE_M M
            WHERE ISNULL(M.CONFIRM_TAG,0)=@confirm
              AND EXISTS (SELECT 1 FROM dbo.PUR_PURCHASE_CHANGE_D D
                          WHERE D.CHANGE_PURCHASE_TYPE=M.CHANGE_PURCHASE_TYPE AND D.CHANGE_PURCHASE_NO=M.CHANGE_PURCHASE_NO)
            ORDER BY ISNULL(M.CONFIRM_DATE, M.CHANGE_PURCHASE_DATE) DESC;
            """;
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@confirm", confirm);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
            throw new InvalidOperationException(deapprove
                ? "未找到可解批对拍的已批核采购变更单（自动选单无结果）。"
                : "未找到可对拍的未批核采购变更单（自动选单无结果）。");
        return new[] { reader.GetString(0).Trim(), reader.GetString(1).Trim() };
    }

    private static IReadOnlyList<TableSpec> BuildTableSpecs1609(MasterContext master)
    {
        var ct = new SqlParameter("@ct", master.ReceiveType);
        var cn = new SqlParameter("@cn", master.ReceiveNo);
        var specs = new List<TableSpec>
        {
            new("PUR_PURCHASE_CHANGE_M", new[] { "CHANGE_PURCHASE_TYPE", "CHANGE_PURCHASE_NO" },
                "@ct=CHANGE_PURCHASE_TYPE AND @cn=CHANGE_PURCHASE_NO", new[] { ct, cn }),
            new("PUR_PURCHASE_CHANGE_D", new[] { "CHANGE_PURCHASE_TYPE", "CHANGE_PURCHASE_NO", "SERIAL_NO" },
                "@ct=CHANGE_PURCHASE_TYPE AND @cn=CHANGE_PURCHASE_NO", new[] { ct, cn }),
        };
        specs.Add(new("PUR_PURCHASE_M", new[] { "PURCHASE_TYPE", "PURCHASE_NO" },
            "EXISTS (SELECT 1 FROM dbo.PUR_PURCHASE_CHANGE_M R WHERE R.CHANGE_PURCHASE_TYPE=@ct AND R.CHANGE_PURCHASE_NO=@cn "
            + "AND PUR_PURCHASE_M.PURCHASE_TYPE=R.PURCHASE_TYPE AND PUR_PURCHASE_M.PURCHASE_NO=R.PURCHASE_NO)",
            new[] { ct, cn }));
        specs.Add(new("PUR_PURCHASE_D", new[] { "PURCHASE_TYPE", "PURCHASE_NO", "SERIAL_NO" },
            "EXISTS (SELECT 1 FROM dbo.PUR_PURCHASE_CHANGE_M R WHERE R.CHANGE_PURCHASE_TYPE=@ct AND R.CHANGE_PURCHASE_NO=@cn "
            + "AND PUR_PURCHASE_D.PURCHASE_TYPE=R.PURCHASE_TYPE AND PUR_PURCHASE_D.PURCHASE_NO=R.PURCHASE_NO)",
            new[] { ct, cn }));
        specs.Add(new("SUPPLIER", new[] { "SUPPLIER_ID" },
            "EXISTS (SELECT 1 FROM dbo.PUR_PURCHASE_CHANGE_M R WHERE R.CHANGE_PURCHASE_TYPE=@ct AND R.CHANGE_PURCHASE_NO=@cn "
            + "AND SUPPLIER.SUPPLIER_ID=R.SUPPLIER_ID)",
            new[] { ct, cn }));
        specs.Add(new("COP_ORDER_D", new[] { "ORDER_TYPE", "ORDER_NO", "SERIAL_NO" },
            "EXISTS (SELECT 1 FROM dbo.PUR_PURCHASE_CHANGE_D R WHERE R.CHANGE_PURCHASE_TYPE=@ct AND R.CHANGE_PURCHASE_NO=@cn "
            + "AND COP_ORDER_D.ORDER_TYPE=R.ORDER_TYPE AND COP_ORDER_D.ORDER_NO=R.ORDER_NO "
            + "AND COP_ORDER_D.SERIAL_NO=R.ORDER_SERIAL_NO)",
            new[] { ct, cn }));
        return specs;
    }

    private static async Task<IReadOnlyList<string>> ResolveRecordKeys1509Async(
        SqlConnection connection, bool deapprove, bool failure)
    {
        if (failure)
        {
            throw new NotSupportedException("1509 影子规格未规格化失败分支（模块无校验规则）。");
        }
        var confirm = deapprove ? "1" : "0";
        const string sql = """
            SELECT TOP 1 M.CHANGE_PRODUCE_TYPE, M.CHANGE_PRODUCE_NO
            FROM dbo.MOC_PRODUCE_CHANGE_M M
            WHERE ISNULL(M.CONFIRM_TAG,0)=@confirm
              AND EXISTS (SELECT 1 FROM dbo.MOC_PRODUCE_CHANGE_D D
                          WHERE D.CHANGE_PRODUCE_TYPE=M.CHANGE_PRODUCE_TYPE AND D.CHANGE_PRODUCE_NO=M.CHANGE_PRODUCE_NO)
            ORDER BY ISNULL(M.CONFIRM_DATE, M.CHANGE_PRODUCE_DATE) DESC;
            """;
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@confirm", confirm);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
            throw new InvalidOperationException(deapprove
                ? "未找到可解批对拍的已批核制令变更单（自动选单无结果）。"
                : "未找到可对拍的未批核制令变更单（自动选单无结果）。");
        return new[] { reader.GetString(0).Trim(), reader.GetString(1).Trim() };
    }

    private static async Task<IReadOnlyList<string>> ResolveRecordKeys1405Async(
        SqlConnection connection, bool deapprove, bool failure)
    {
        if (failure)
        {
            throw new NotSupportedException("1405 影子规格未规格化失败分支（模块无校验规则）。");
        }
        var confirm = deapprove ? "1" : "0";
        // Prefer orders whose lines have a positive MRP footprint so the intended
        // segmentation is exercised (the legacy cursor degenerates to PLAN=QTY/DEPOT=0;
        // see mrp-plan-alloc-design.md).
        const string sql = """
            SELECT TOP 1 M.ORDER_TYPE, M.ORDER_NO
            FROM dbo.COP_ORDER_M M
            WHERE ISNULL(M.CONFIRM_TAG,0)=@confirm
              AND EXISTS (SELECT 1 FROM dbo.COP_ORDER_D D
                          WHERE D.ORDER_TYPE=M.ORDER_TYPE AND D.ORDER_NO=M.ORDER_NO)
              AND EXISTS (SELECT 1 FROM dbo.COP_ORDER_D D
                          JOIN dbo.PRODUCT P ON P.PRO_NO=D.PRO_NO
                          WHERE D.ORDER_TYPE=M.ORDER_TYPE AND D.ORDER_NO=M.ORDER_NO
                            AND ISNULL(P.MRP_QTY,0)>0)
            ORDER BY M.ORDER_DATE DESC, M.ORDER_NO DESC;
            """;
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@confirm", confirm);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
            throw new InvalidOperationException(deapprove
                ? "未找到可解批对拍的已批核客户订单（自动选单无结果）。"
                : "未找到可对拍的未批核客户订单（自动选单无结果）。");
        return new[] { reader.GetString(0).Trim(), reader.GetString(1).Trim() };
    }

    private static async Task<IReadOnlyList<string>> ResolveRecordKeys1502Async(
        SqlConnection connection, bool deapprove, bool failure)
    {
        if (failure)
        {
            throw new NotSupportedException("1502 影子规格未规格化失败分支（模块无校验规则）。");
        }
        var confirm = deapprove ? "1" : "0";
        const string sql = """
            SELECT TOP 1 M.PRODUCE_TYPE, M.PRODUCE_NO
            FROM dbo.MOC_PRODUCE_M M
            WHERE ISNULL(M.CONFIRM_TAG,0)=@confirm
              AND EXISTS (SELECT 1 FROM dbo.MOC_PRODUCE_D D
                          WHERE D.PRODUCE_TYPE=M.PRODUCE_TYPE AND D.PRODUCE_NO=M.PRODUCE_NO)
            ORDER BY ISNULL(M.CONFIRM_DATE, M.PRODUCE_DATE) DESC, M.PRODUCE_NO DESC;
            """;
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@confirm", confirm);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
            throw new InvalidOperationException(deapprove
                ? "未找到可解批对拍的已批核制令单（自动选单无结果）。"
                : "未找到可对拍的未批核制令单（自动选单无结果）。");
        return new[] { reader.GetString(0).Trim(), reader.GetString(1).Trim() };
    }

    private static async Task<IReadOnlyList<string>> ResolveRecordKeys180310Async(
        SqlConnection connection, ModuleShadowSpec spec, bool deapprove, bool failure)
    {
        if (failure)
        {
            throw new NotSupportedException($"{spec.ModuleId} 影子规格未规格化失败分支（模块无校验规则）。");
        }
        // The dimission-sync effect is meaningful only when at least one detail row both
        // references an existing dimission document and points at an existing employee;
        // otherwise approve is a no-op on both paths and proves nothing.
        var confirm = deapprove ? "1" : "0";
        var secrecy = spec.ModuleId == 1803101 ? "1" : "0";
        const string sql = """
            SELECT TOP 1 M.WAGE_TYPE, M.WAGE_NO
            FROM dbo.HR_WAGE_M M
            WHERE ISNULL(M.CONFIRM_TAG,0)=@confirm
              AND ISNULL(M.IF_SECRECY,0)=@secrecy
              AND EXISTS (
                  SELECT 1 FROM dbo.HR_WAGE_D D
                  WHERE D.WAGE_TYPE=M.WAGE_TYPE AND D.WAGE_NO=M.WAGE_NO
                    AND EXISTS (
                        SELECT 1 FROM dbo.HR_DIMISSION_M DM
                        WHERE DM.DIMISSION_TYPE=D.DIMISSION_TYPE
                          AND DM.DIMISSION_NO=D.DIMISSION_NO)
                    AND EXISTS (
                        SELECT 1 FROM dbo.HR_EMPLOYEE E WHERE E.EMP_ID=D.EMP_ID))
            ORDER BY ISNULL(M.CONFIRM_DATE, M.WAGE_DATE) DESC, M.WAGE_NO DESC;
            """;
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@confirm", confirm);
        command.Parameters.AddWithValue("@secrecy", secrecy);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
            throw new InvalidOperationException(deapprove
                ? $"未找到可解批对拍的已批核离职工资表（{spec.DocName}，自动选单无结果）。"
                : $"未找到可对拍的未批核离职工资表（{spec.DocName}，自动选单无结果）。");
        return new[] { reader.GetString(0).Trim(), reader.GetString(1).Trim() };
    }

    private static IReadOnlyList<TableSpec> BuildTableSpecs1509(MasterContext master)
    {
        var ct = new SqlParameter("@ct", master.ReceiveType);
        var cn = new SqlParameter("@cn", master.ReceiveNo);
        var specs = new List<TableSpec>
        {
            new("MOC_PRODUCE_CHANGE_M", new[] { "CHANGE_PRODUCE_TYPE", "CHANGE_PRODUCE_NO" },
                "@ct=CHANGE_PRODUCE_TYPE AND @cn=CHANGE_PRODUCE_NO", new[] { ct, cn }),
            new("MOC_PRODUCE_CHANGE_D", new[] { "CHANGE_PRODUCE_TYPE", "CHANGE_PRODUCE_NO", "SERIAL_NO" },
                "@ct=CHANGE_PRODUCE_TYPE AND @cn=CHANGE_PRODUCE_NO", new[] { ct, cn }),
        };
        // Original produce master/detail rows referenced by the change master.
        specs.Add(new("MOC_PRODUCE_M", new[] { "PRODUCE_TYPE", "PRODUCE_NO" },
            "EXISTS (SELECT 1 FROM dbo.MOC_PRODUCE_CHANGE_M R WHERE R.CHANGE_PRODUCE_TYPE=@ct AND R.CHANGE_PRODUCE_NO=@cn "
            + "AND MOC_PRODUCE_M.PRODUCE_TYPE=R.PRODUCE_TYPE AND MOC_PRODUCE_M.PRODUCE_NO=R.PRODUCE_NO)",
            new[] { ct, cn }));
        specs.Add(new("MOC_PRODUCE_D", new[] { "PRODUCE_TYPE", "PRODUCE_NO", "SERIAL_NO" },
            "EXISTS (SELECT 1 FROM dbo.MOC_PRODUCE_CHANGE_M R WHERE R.CHANGE_PRODUCE_TYPE=@ct AND R.CHANGE_PRODUCE_NO=@cn "
            + "AND MOC_PRODUCE_D.PRODUCE_TYPE=R.PRODUCE_TYPE AND MOC_PRODUCE_D.PRODUCE_NO=R.PRODUCE_NO)",
            new[] { ct, cn }));
        // MRP footprints: finished product (produce master PRO_NO) and raw materials
        // (produce detail PRO_NO) on PRODUCT, plan and order planned quantities.
        specs.Add(new("PRODUCT", new[] { "PRO_NO" },
            "EXISTS (SELECT 1 FROM dbo.MOC_PRODUCE_CHANGE_M R JOIN dbo.MOC_PRODUCE_M P "
            + "ON P.PRODUCE_TYPE=R.PRODUCE_TYPE AND P.PRODUCE_NO=R.PRODUCE_NO "
            + "WHERE R.CHANGE_PRODUCE_TYPE=@ct AND R.CHANGE_PRODUCE_NO=@cn AND PRODUCT.PRO_NO=P.PRO_NO) "
            + "OR EXISTS (SELECT 1 FROM dbo.MOC_PRODUCE_CHANGE_M R JOIN dbo.MOC_PRODUCE_D D "
            + "ON D.PRODUCE_TYPE=R.PRODUCE_TYPE AND D.PRODUCE_NO=R.PRODUCE_NO "
            + "WHERE R.CHANGE_PRODUCE_TYPE=@ct AND R.CHANGE_PRODUCE_NO=@cn AND PRODUCT.PRO_NO=D.PRO_NO)",
            new[] { ct, cn }));
        specs.Add(new("MOC_PLAN_MOC", new[] { "PLAN_TYPE", "PLAN_NO", "SERIAL_NO" },
            "EXISTS (SELECT 1 FROM dbo.MOC_PRODUCE_CHANGE_M R JOIN dbo.MOC_PRODUCE_M P "
            + "ON P.PRODUCE_TYPE=R.PRODUCE_TYPE AND P.PRODUCE_NO=R.PRODUCE_NO "
            + "WHERE R.CHANGE_PRODUCE_TYPE=@ct AND R.CHANGE_PRODUCE_NO=@cn "
            + "AND MOC_PLAN_MOC.PLAN_TYPE=P.PLAN_TYPE AND MOC_PLAN_MOC.PLAN_NO=P.PLAN_NO "
            + "AND MOC_PLAN_MOC.SERIAL_NO=P.PLAN_SERIAL_NO)",
            new[] { ct, cn }));
        specs.Add(new("COP_ORDER_D", new[] { "ORDER_TYPE", "ORDER_NO", "SERIAL_NO" },
            "EXISTS (SELECT 1 FROM dbo.MOC_PRODUCE_CHANGE_M R JOIN dbo.MOC_PRODUCE_M P "
            + "ON P.PRODUCE_TYPE=R.PRODUCE_TYPE AND P.PRODUCE_NO=R.PRODUCE_NO "
            + "WHERE R.CHANGE_PRODUCE_TYPE=@ct AND R.CHANGE_PRODUCE_NO=@cn "
            + "AND COP_ORDER_D.ORDER_TYPE=P.ORDER_TYPE AND COP_ORDER_D.ORDER_NO=P.ORDER_NO "
            + "AND COP_ORDER_D.SERIAL_NO=P.ORDER_SERIAL_NO)",
            new[] { ct, cn }));
        return specs;
    }

    private static IReadOnlyList<TableSpec> BuildTableSpecs1405(MasterContext master)
    {
        var ot = new SqlParameter("@ot", master.ReceiveType);
        var on = new SqlParameter("@on", master.ReceiveNo);
        var specs = new List<TableSpec>
        {
            new("COP_ORDER_M", new[] { "ORDER_TYPE", "ORDER_NO" },
                "@ot=ORDER_TYPE AND @on=ORDER_NO", new[] { ot, on }),
            new("COP_ORDER_D", new[] { "ORDER_TYPE", "ORDER_NO", "SERIAL_NO" },
                "@ot=ORDER_TYPE AND @on=ORDER_NO", new[] { ot, on }),
        };
        specs.Add(new("CLIENT", new[] { "CLIENT_ID" },
            "EXISTS (SELECT 1 FROM dbo.COP_ORDER_M R WHERE R.ORDER_TYPE=@ot AND R.ORDER_NO=@on "
            + "AND CLIENT.CLIENT_ID=R.CLIENT_ID)",
            new[] { ot, on }));
        specs.Add(new("PRODUCT", new[] { "PRO_NO" },
            "EXISTS (SELECT 1 FROM dbo.COP_ORDER_D R WHERE R.ORDER_TYPE=@ot AND R.ORDER_NO=@on "
            + "AND PRODUCT.PRO_NO=R.PRO_NO)",
            new[] { ot, on }));
        return specs;
    }

    private static IReadOnlyList<TableSpec> BuildTableSpecs1502(MasterContext master)
    {
        var pt = new SqlParameter("@pt", master.ReceiveType);
        var pn = new SqlParameter("@pn", master.ReceiveNo);
        var specs = new List<TableSpec>
        {
            new("MOC_PRODUCE_M", new[] { "PRODUCE_TYPE", "PRODUCE_NO" },
                "@pt=PRODUCE_TYPE AND @pn=PRODUCE_NO", new[] { pt, pn }),
            new("MOC_PRODUCE_D", new[] { "PRODUCE_TYPE", "PRODUCE_NO", "SERIAL_NO" },
                "@pt=PRODUCE_TYPE AND @pn=PRODUCE_NO", new[] { pt, pn }),
        };
        // Finished product (produce master PRO_NO) and raw materials (detail PRO_NO).
        specs.Add(new("PRODUCT", new[] { "PRO_NO" },
            "EXISTS (SELECT 1 FROM dbo.MOC_PRODUCE_M R WHERE R.PRODUCE_TYPE=@pt AND R.PRODUCE_NO=@pn "
            + "AND PRODUCT.PRO_NO=R.PRO_NO) "
            + "OR EXISTS (SELECT 1 FROM dbo.MOC_PRODUCE_D R WHERE R.PRODUCE_TYPE=@pt AND R.PRODUCE_NO=@pn "
            + "AND PRODUCT.PRO_NO=R.PRO_NO)",
            new[] { pt, pn }));
        // Order/plan lines stamped by field-accumulate on produce approval.
        specs.Add(new("COP_ORDER_D", new[] { "ORDER_TYPE", "ORDER_NO", "SERIAL_NO" },
            "EXISTS (SELECT 1 FROM dbo.MOC_PRODUCE_M R WHERE R.PRODUCE_TYPE=@pt AND R.PRODUCE_NO=@pn "
            + "AND COP_ORDER_D.ORDER_TYPE=R.ORDER_TYPE AND COP_ORDER_D.ORDER_NO=R.ORDER_NO "
            + "AND COP_ORDER_D.SERIAL_NO=R.ORDER_SERIAL_NO)",
            new[] { pt, pn }));
        specs.Add(new("MOC_PLAN_MOC", new[] { "PLAN_TYPE", "PLAN_NO", "SERIAL_NO" },
            "EXISTS (SELECT 1 FROM dbo.MOC_PRODUCE_M R WHERE R.PRODUCE_TYPE=@pt AND R.PRODUCE_NO=@pn "
            + "AND MOC_PLAN_MOC.PLAN_TYPE=R.PLAN_TYPE AND MOC_PLAN_MOC.PLAN_NO=R.PLAN_NO "
            + "AND MOC_PLAN_MOC.SERIAL_NO=R.PLAN_SERIAL_NO)",
            new[] { pt, pn }));
        return specs;
    }

    /// <summary>
    /// Minimal snapshot spec for modules whose side effects are still being validated:
    /// the module master and detail rows only. Used by engine-only regression runs to
    /// prove the published chain executes cleanly with zero residue; expand the table
    /// set once shadow comparison of each module is scheduled.
    /// </summary>
    private static IReadOnlyList<TableSpec> BuildBasicTableSpecs(
        MasterContext master, string masterTable, string detailTable,
        string key1, string key2, string dateColumn)
    {
        var k1 = new SqlParameter("@k1", master.ReceiveType);
        var k2 = new SqlParameter("@k2", master.ReceiveNo);
        return new List<TableSpec>
        {
            new(masterTable, new[] { key1, key2 },
                $"@k1={key1} AND @k2={key2}", new[] { k1, k2 }),
            new(detailTable, new[] { key1, key2, "SERIAL_NO" },
                $"@k1={key1} AND @k2={key2}", new[] { k1, k2 }),
        };
    }

    /// <summary>
    /// Generic confirm-state resolver for B-bucket single-keyed documents: approve
    /// picks an unconfirmed master with detail rows, deapprove a confirmed one.
    /// </summary>
    private static async Task<IReadOnlyList<string>> ResolveRecordKeysByConfirmAsync(
        SqlConnection connection, ModuleShadowSpec spec, bool deapprove, bool failure)
    {
        if (failure)
        {
            throw new NotSupportedException($"{spec.ModuleId} 影子规格未规格化失败分支（校验由领域规则承载）。");
        }
        var confirm = deapprove ? "1" : "0";
        var sql = $"SELECT TOP 1 M.[{spec.Key1Column}], M.[{spec.Key2Column}] "
            + $"FROM dbo.[{spec.MasterTable}] M "
            + $"WHERE ISNULL(M.CONFIRM_TAG,0)=@confirm "
            + $"AND EXISTS (SELECT 1 FROM dbo.[{spec.DetailTable}] D "
            + $"WHERE D.[{spec.Key1Column}]=M.[{spec.Key1Column}] AND D.[{spec.Key2Column}]=M.[{spec.Key2Column}]) "
            + $"ORDER BY ISNULL(M.CONFIRM_DATE, M.[{spec.DateColumn}]) DESC;";
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@confirm", confirm);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
            throw new InvalidOperationException(deapprove
                ? $"未找到可解批对拍的已批核单据（{spec.DocName}，自动选单无结果）。"
                : $"未找到可对拍的未批核单据（{spec.DocName}，自动选单无结果）。");
        return new[] { reader.GetString(0).Trim(), reader.GetString(1).Trim() };
    }

    /// <summary>
    /// 2904 (mould accept) has no physical detail table: approve picks an
    /// unconfirmed accept master, deapprove a confirmed one.
    /// </summary>
    private static async Task<IReadOnlyList<string>> ResolveRecordKeys2904Async(
        SqlConnection connection, bool deapprove, bool failure)
    {
        if (failure)
        {
            throw new NotSupportedException("2904 影子规格未规格化失败分支（模块无校验规则）。");
        }
        var confirm = deapprove ? "1" : "0";
        const string sql = """
            SELECT TOP 1 M.ACCEPT_TYPE, M.ACCEPT_NO
            FROM dbo.MOU_ACCEPT_M M
            WHERE ISNULL(M.CONFIRM_TAG,0)=@confirm
            ORDER BY ISNULL(M.CONFIRM_DATE, M.ACCEPT_DATE) DESC;
            """;
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@confirm", confirm);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
            throw new InvalidOperationException(deapprove
                ? "未找到可解批对拍的已批核模具承认单（自动选单无结果）。"
                : "未找到可对拍的未批核模具承认单（自动选单无结果）。");
        return new[] { reader.GetString(0).Trim(), reader.GetString(1).Trim() };
    }

    /// <summary>
    /// Master-scoped inventory-log spec for 2xxx outbound documents: unlike the
    /// row-level BuildLogSpec (which narrows by detail serials/products/depots),
    /// this filters by document date + key pair only. Both paths execute the same
    /// document inside symmetric rolled-back transactions, so the wider scope is
    /// comparison-safe and needs no detail context rows.
    /// </summary>
    private static TableSpec BuildLogSpecByMaster(
        string moduleNumber, MasterContext master)
    {
        var date = master.ReceiveDate!.Value.ToString(SqlDateFormat);
        var parameters = new List<SqlParameter>
        {
            new("@rdate", date),
            new("@rt", master.ReceiveType),
            new("@rn", master.ReceiveNo),
        };
        var builder = new StringBuilder("CONVERT(nvarchar(19),MUTUALITY_DATE,120)=@rdate")
            .Append($" AND ((MUTUALITY_TYPE='{moduleNumber}' AND MUTUALITY_NO=@rt) OR (MUTUALITY_TYPE=@rt AND MUTUALITY_NO=@rn))");
        return new TableSpec(
            "INV_DEPOT_LOG",
            new[] { "PRO_NO", "MUTUALITY_DATE", "IN_OUT", "MUTUALITY_SERIAL_NO", "DEPOT_ID", "BATCH_NO" },
            builder.ToString(),
            parameters);
    }

    private static IReadOnlyList<TableSpec> BuildTableSpecs2705(MasterContext master)
    {
        var wt = new SqlParameter("@wt", master.ReceiveType);
        var wn = new SqlParameter("@wn", master.ReceiveNo);
        var specs = new List<TableSpec>
        {
            new("MOC_WORK_M", new[] { "WORK_TYPE", "WORK_NO" },
                "@wt=WORK_TYPE AND @wn=WORK_NO", new[] { wt, wn }),
            new("MOC_WORK_D", new[] { "WORK_TYPE", "WORK_NO", "SERIAL_NO" },
                "@wt=WORK_TYPE AND @wn=WORK_NO", new[] { wt, wn }),
        };
        // field-accumulate targets reached through the work detail produce refs.
        specs.Add(new("MOC_PRODUCE_M", new[] { "PRODUCE_TYPE", "PRODUCE_NO" },
            "EXISTS (SELECT 1 FROM dbo.MOC_WORK_D R WHERE R.WORK_TYPE=@wt AND R.WORK_NO=@wn "
            + "AND MOC_PRODUCE_M.PRODUCE_TYPE=R.PRODUCE_TYPE AND MOC_PRODUCE_M.PRODUCE_NO=R.PRODUCE_NO)",
            new[] { wt, wn }));
        specs.Add(new("MOC_PRODUCE_PROCESS_D", new[] { "PRODUCE_TYPE", "PRODUCE_NO", "SERIAL_NO" },
            "EXISTS (SELECT 1 FROM dbo.MOC_WORK_D R WHERE R.WORK_TYPE=@wt AND R.WORK_NO=@wn "
            + "AND MOC_PRODUCE_PROCESS_D.PRODUCE_TYPE=R.PRODUCE_TYPE AND MOC_PRODUCE_PROCESS_D.PRODUCE_NO=R.PRODUCE_NO)",
            new[] { wt, wn }));
        return specs;
    }

    private static IReadOnlyList<TableSpec> BuildTableSpecs2706(MasterContext master)
    {
        var wt = new SqlParameter("@wt", master.ReceiveType);
        var wn = new SqlParameter("@wn", master.ReceiveNo);
        var specs = new List<TableSpec>
        {
            new("MOC_WORK_IN_M", new[] { "WORK_IN_TYPE", "WORK_IN_NO" },
                "@wt=WORK_IN_TYPE AND @wn=WORK_IN_NO", new[] { wt, wn }),
            new("MOC_WORK_IN_D", new[] { "WORK_IN_TYPE", "WORK_IN_NO", "SERIAL_NO" },
                "@wt=WORK_IN_TYPE AND @wn=WORK_IN_NO", new[] { wt, wn }),
        };
        // Referenced work-order rows (completion targets of the inbound).
        specs.Add(new("MOC_WORK_M", new[] { "WORK_TYPE", "WORK_NO" },
            "EXISTS (SELECT 1 FROM dbo.MOC_WORK_IN_D R WHERE R.WORK_IN_TYPE=@wt AND R.WORK_IN_NO=@wn "
            + "AND MOC_WORK_M.WORK_TYPE=R.WORK_TYPE AND MOC_WORK_M.WORK_NO=R.WORK_NO)",
            new[] { wt, wn }));
        specs.Add(new("MOC_WORK_D", new[] { "WORK_TYPE", "WORK_NO", "SERIAL_NO" },
            "EXISTS (SELECT 1 FROM dbo.MOC_WORK_IN_D R WHERE R.WORK_IN_TYPE=@wt AND R.WORK_IN_NO=@wn "
            + "AND MOC_WORK_D.WORK_TYPE=R.WORK_TYPE AND MOC_WORK_D.WORK_NO=R.WORK_NO "
            + "AND MOC_WORK_D.SERIAL_NO=R.WORK_SERIAL_NO)",
            new[] { wt, wn }));
        specs.Add(new("MOC_PRODUCE_M", new[] { "PRODUCE_TYPE", "PRODUCE_NO" },
            "EXISTS (SELECT 1 FROM dbo.MOC_WORK_IN_D R WHERE R.WORK_IN_TYPE=@wt AND R.WORK_IN_NO=@wn "
            + "AND MOC_PRODUCE_M.PRODUCE_TYPE=R.PRODUCE_TYPE AND MOC_PRODUCE_M.PRODUCE_NO=R.PRODUCE_NO)",
            new[] { wt, wn }));
        specs.Add(new("MOC_PRODUCE_PROCESS_D", new[] { "PRODUCE_TYPE", "PRODUCE_NO", "SERIAL_NO" },
            "EXISTS (SELECT 1 FROM dbo.MOC_WORK_IN_D R WHERE R.WORK_IN_TYPE=@wt AND R.WORK_IN_NO=@wn "
            + "AND MOC_PRODUCE_PROCESS_D.PRODUCE_TYPE=R.PRODUCE_TYPE AND MOC_PRODUCE_PROCESS_D.PRODUCE_NO=R.PRODUCE_NO)",
            new[] { wt, wn }));
        return specs;
    }

    private static IReadOnlyList<TableSpec> BuildTableSpecs2707(MasterContext master)
    {
        var wt = new SqlParameter("@wt", master.ReceiveType);
        var wn = new SqlParameter("@wn", master.ReceiveNo);
        var specs = new List<TableSpec>
        {
            new("MOC_WORK_OUT_M", new[] { "WORK_OUT_TYPE", "WORK_OUT_NO" },
                "@wt=WORK_OUT_TYPE AND @wn=WORK_OUT_NO", new[] { wt, wn }),
            new("MOC_WORK_OUT_D", new[] { "WORK_OUT_TYPE", "WORK_OUT_NO", "SERIAL_NO" },
                "@wt=WORK_OUT_TYPE AND @wn=WORK_OUT_NO", new[] { wt, wn }),
        };
        // completion-close targets (MOC_WORK_D/M) reached through the issue refs.
        specs.Add(new("MOC_WORK_M", new[] { "WORK_TYPE", "WORK_NO" },
            "EXISTS (SELECT 1 FROM dbo.MOC_WORK_OUT_D R WHERE R.WORK_OUT_TYPE=@wt AND R.WORK_OUT_NO=@wn "
            + "AND MOC_WORK_M.WORK_TYPE=R.WORK_TYPE AND MOC_WORK_M.WORK_NO=R.WORK_NO)",
            new[] { wt, wn }));
        specs.Add(new("MOC_WORK_D", new[] { "WORK_TYPE", "WORK_NO", "SERIAL_NO" },
            "EXISTS (SELECT 1 FROM dbo.MOC_WORK_OUT_D R WHERE R.WORK_OUT_TYPE=@wt AND R.WORK_OUT_NO=@wn "
            + "AND MOC_WORK_D.WORK_TYPE=R.WORK_TYPE AND MOC_WORK_D.WORK_NO=R.WORK_NO)",
            new[] { wt, wn }));
        specs.Add(new("MOC_PRODUCE_M", new[] { "PRODUCE_TYPE", "PRODUCE_NO" },
            "EXISTS (SELECT 1 FROM dbo.MOC_WORK_OUT_D R WHERE R.WORK_OUT_TYPE=@wt AND R.WORK_OUT_NO=@wn "
            + "AND MOC_PRODUCE_M.PRODUCE_TYPE=R.PRODUCE_TYPE AND MOC_PRODUCE_M.PRODUCE_NO=R.PRODUCE_NO)",
            new[] { wt, wn }));
        specs.Add(new("MOC_PRODUCE_PROCESS_D", new[] { "PRODUCE_TYPE", "PRODUCE_NO", "SERIAL_NO" },
            "EXISTS (SELECT 1 FROM dbo.MOC_WORK_OUT_D R WHERE R.WORK_OUT_TYPE=@wt AND R.WORK_OUT_NO=@wn "
            + "AND MOC_PRODUCE_PROCESS_D.PRODUCE_TYPE=R.PRODUCE_TYPE AND MOC_PRODUCE_PROCESS_D.PRODUCE_NO=R.PRODUCE_NO)",
            new[] { wt, wn }));
        return specs;
    }

    private static IReadOnlyList<TableSpec> BuildTableSpecs2708(MasterContext master)
    {
        var pt = new SqlParameter("@pt", master.ReceiveType);
        var pn = new SqlParameter("@pn", master.ReceiveNo);
        var specs = new List<TableSpec>
        {
            new("SFC_PLAN_M", new[] { "PLAN_TYPE", "PLAN_NO" },
                "@pt=PLAN_TYPE AND @pn=PLAN_NO", new[] { pt, pn }),
            new("SFC_PLAN_D", new[] { "PLAN_TYPE", "PLAN_NO", "SERIAL_NO" },
                "@pt=PLAN_TYPE AND @pn=PLAN_NO", new[] { pt, pn }),
            new("SFC_PLAN_MORE", new[] { "PLAN_TYPE", "PLAN_NO", "SERIAL_NO" },
                "@pt=PLAN_TYPE AND @pn=PLAN_NO", new[] { pt, pn }),
        };
        // field-accumulate target reached through the plan detail produce refs.
        specs.Add(new("MOC_PRODUCE_M", new[] { "PRODUCE_TYPE", "PRODUCE_NO" },
            "EXISTS (SELECT 1 FROM dbo.SFC_PLAN_D R WHERE R.PLAN_TYPE=@pt AND R.PLAN_NO=@pn "
            + "AND MOC_PRODUCE_M.PRODUCE_TYPE=R.PRODUCE_TYPE AND MOC_PRODUCE_M.PRODUCE_NO=R.PRODUCE_NO)",
            new[] { pt, pn }));
        return specs;
    }

    private static IReadOnlyList<TableSpec> BuildTableSpecs2815(MasterContext master)
    {
        var ot = new SqlParameter("@ot", master.ReceiveType);
        var on = new SqlParameter("@on", master.ReceiveNo);
        var specs = new List<TableSpec>
        {
            new("MOC_PRODUCT_OUT_M", new[] { "PRODUCT_OUT_TYPE", "PRODUCT_OUT_NO" },
                "@ot=PRODUCT_OUT_TYPE AND @on=PRODUCT_OUT_NO", new[] { ot, on }),
            new("MOC_PRODUCT_OUT_D", new[] { "PRODUCT_OUT_TYPE", "PRODUCT_OUT_NO", "SERIAL_NO" },
                "@ot=PRODUCT_OUT_TYPE AND @on=PRODUCT_OUT_NO", new[] { ot, on }),
        };
        // Produce write-back (field-accumulate + completion-close pair).
        specs.Add(new("MOC_PRODUCE_M", new[] { "PRODUCE_TYPE", "PRODUCE_NO" },
            "EXISTS (SELECT 1 FROM dbo.MOC_PRODUCT_OUT_D R WHERE R.PRODUCT_OUT_TYPE=@ot AND R.PRODUCT_OUT_NO=@on "
            + "AND MOC_PRODUCE_M.PRODUCE_TYPE=R.PRODUCE_TYPE AND MOC_PRODUCE_M.PRODUCE_NO=R.PRODUCE_NO)",
            new[] { ot, on }));
        specs.Add(new("MOC_PRODUCE_D", new[] { "PRODUCE_TYPE", "PRODUCE_NO", "SERIAL_NO" },
            "EXISTS (SELECT 1 FROM dbo.MOC_PRODUCT_OUT_D R WHERE R.PRODUCT_OUT_TYPE=@ot AND R.PRODUCT_OUT_NO=@on "
            + "AND MOC_PRODUCE_D.PRODUCE_TYPE=R.PRODUCE_TYPE AND MOC_PRODUCE_D.PRODUCE_NO=R.PRODUCE_NO)",
            new[] { ot, on }));
        // Order write-back (adjust-projection target).
        specs.Add(new("COP_ORDER_D", new[] { "ORDER_TYPE", "ORDER_NO", "SERIAL_NO" },
            "EXISTS (SELECT 1 FROM dbo.MOC_PRODUCT_OUT_D R WHERE R.PRODUCT_OUT_TYPE=@ot AND R.PRODUCT_OUT_NO=@on "
            + "AND COP_ORDER_D.ORDER_TYPE=R.ORDER_TYPE AND COP_ORDER_D.ORDER_NO=R.ORDER_NO "
            + "AND COP_ORDER_D.SERIAL_NO=R.ORDER_SERIAL_NO)",
            new[] { ot, on }));
        // Stock footprint of the outbound move.
        specs.Add(new("PRODUCT", new[] { "PRO_NO" },
            "EXISTS (SELECT 1 FROM dbo.MOC_PRODUCT_OUT_D R WHERE R.PRODUCT_OUT_TYPE=@ot AND R.PRODUCT_OUT_NO=@on "
            + "AND PRODUCT.PRO_NO=R.PRO_NO)",
            new[] { ot, on }));
        specs.Add(new("INV_PRO_DEPOT", new[] { "PRO_NO", "DEPOT_ID" },
            "EXISTS (SELECT 1 FROM dbo.MOC_PRODUCT_OUT_D R WHERE R.PRODUCT_OUT_TYPE=@ot AND R.PRODUCT_OUT_NO=@on "
            + "AND INV_PRO_DEPOT.PRO_NO=R.PRO_NO AND INV_PRO_DEPOT.DEPOT_ID=R.DEPOT_ID)",
            new[] { ot, on }));
        if (master.ReceiveDate is not null)
        {
            specs.Add(BuildLogSpecByMaster("2815", master));
        }
        return specs;
    }

    /// <summary>
    /// Material issue family (1503/1514/1517/2805/2806, legacy P_WF_MOC_GET): the document
    /// consumes the produce (used quantity on the produce line), the customer order MORE
    /// row, the product's expected-get projection and the outbound stock move. Replaces the
    /// master+detail-only spec whose PASS could not see any of those tables.
    /// </summary>
    private static IReadOnlyList<TableSpec> BuildTableSpecsMaterialIssue(MasterContext master)
    {
        var gt = new SqlParameter("@gt", master.ReceiveType);
        var gn = new SqlParameter("@gn", master.ReceiveNo);
        var doc = "R.GET_TYPE=@gt AND R.GET_NO=@gn";
        var specs = new List<TableSpec>
        {
            new("MOC_GET_M", new[] { "GET_TYPE", "GET_NO" }, "@gt=GET_TYPE AND @gn=GET_NO", new[] { gt, gn }),
            new("MOC_GET_D", new[] { "GET_TYPE", "GET_NO", "SERIAL_NO" }, "@gt=GET_TYPE AND @gn=GET_NO", new[] { gt, gn }),
        };
        // Produce write-back: USED_QTY per line, start tags on the produce master.
        specs.Add(new("MOC_PRODUCE_M", new[] { "PRODUCE_TYPE", "PRODUCE_NO" },
            $"EXISTS (SELECT 1 FROM dbo.MOC_GET_D R WHERE {doc} "
            + "AND MOC_PRODUCE_M.PRODUCE_TYPE=R.PRODUCE_TYPE AND MOC_PRODUCE_M.PRODUCE_NO=R.PRODUCE_NO)",
            new[] { gt, gn }));
        specs.Add(new("MOC_PRODUCE_D", new[] { "PRODUCE_TYPE", "PRODUCE_NO", "SERIAL_NO" },
            $"EXISTS (SELECT 1 FROM dbo.MOC_GET_D R WHERE {doc} "
            + "AND MOC_PRODUCE_D.PRODUCE_TYPE=R.PRODUCE_TYPE AND MOC_PRODUCE_D.PRODUCE_NO=R.PRODUCE_NO)",
            new[] { gt, gn }));
        // Customer order MORE row (no serial column: located by order keys + product).
        specs.Add(new("COP_ORDER_MORE", new[] { "ORDER_TYPE", "ORDER_NO", "PRO_NO" },
            $"EXISTS (SELECT 1 FROM dbo.MOC_GET_D R WHERE {doc} "
            + "AND COP_ORDER_MORE.ORDER_TYPE=R.ORDER_TYPE AND COP_ORDER_MORE.ORDER_NO=R.ORDER_NO "
            + "AND COP_ORDER_MORE.PRO_NO=R.PRO_NO)",
            new[] { gt, gn }));
        // Product projection + stock footprint of the outbound move.
        specs.Add(new("PRODUCT", new[] { "PRO_NO" },
            $"EXISTS (SELECT 1 FROM dbo.MOC_GET_D R WHERE {doc} AND PRODUCT.PRO_NO=R.PRO_NO)",
            new[] { gt, gn }));
        specs.Add(new("INV_PRO_DEPOT", new[] { "PRO_NO", "DEPOT_ID" },
            $"EXISTS (SELECT 1 FROM dbo.MOC_GET_D R WHERE {doc} "
            + "AND INV_PRO_DEPOT.PRO_NO=R.PRO_NO AND INV_PRO_DEPOT.DEPOT_ID=R.DEPOT_ID)",
            new[] { gt, gn }));
        if (master.ReceiveDate is not null)
        {
            specs.Add(BuildLogSpecByMaster("1503", master));
        }
        return specs;
    }

    /// <summary>
    /// Purchase apply documents (1615 finished-goods request / 1616 material request):
    /// the approval writes apply quantities back to the produce line and the plan row,
    /// stamps the order line with the apply reference, and accumulates the order MORE row.
    /// </summary>
    private static IReadOnlyList<TableSpec> BuildTableSpecs1615(MasterContext master)
    {
        var at = new SqlParameter("@at", master.ReceiveType);
        var an = new SqlParameter("@an", master.ReceiveNo);
        var docMore = "R.APPLY_TYPE=@at AND R.APPLY_NO=@an";
        var docDetail = "D.APPLY_TYPE=@at AND D.APPLY_NO=@an";
        var specs = new List<TableSpec>
        {
            new("PUR_APPLY_M", new[] { "APPLY_TYPE", "APPLY_NO" }, "@at=APPLY_TYPE AND @an=APPLY_NO", new[] { at, an }),
            new("PUR_APPLY_D", new[] { "APPLY_TYPE", "APPLY_NO", "SERIAL_NO" }, "@at=APPLY_TYPE AND @an=APPLY_NO", new[] { at, an }),
            new("PUR_APPLY_MORE", new[] { "APPLY_TYPE", "APPLY_NO", "SERIAL_NO" }, "@at=APPLY_TYPE AND @an=APPLY_NO", new[] { at, an }),
        };
        // Produce line: apply quantity accumulation fed by PUR_APPLY_MORE.
        specs.Add(new("MOC_PRODUCE_D", new[] { "PRODUCE_TYPE", "PRODUCE_NO", "PRO_NO" },
            $"EXISTS (SELECT 1 FROM dbo.PUR_APPLY_MORE R WHERE {docMore} "
            + "AND MOC_PRODUCE_D.PRODUCE_TYPE=R.PRODUCE_TYPE AND MOC_PRODUCE_D.PRODUCE_NO=R.PRODUCE_NO)",
            new[] { at, an }));
        // Plan row: apply quantity + reference stamp fed by PUR_APPLY_D.
        specs.Add(new("MOC_PLAN_PUR", new[] { "PLAN_TYPE", "PLAN_NO", "SERIAL_NO" },
            $"EXISTS (SELECT 1 FROM dbo.PUR_APPLY_D D WHERE {docDetail} "
            + "AND MOC_PLAN_PUR.PLAN_TYPE=D.PLAN_TYPE AND MOC_PLAN_PUR.PLAN_NO=D.PLAN_NO "
            + "AND MOC_PLAN_PUR.SERIAL_NO=D.PLAN_SERIAL_NO)",
            new[] { at, an }));
        // Order line: apply reference write-back fed by PUR_APPLY_D.
        specs.Add(new("COP_ORDER_D", new[] { "ORDER_TYPE", "ORDER_NO", "SERIAL_NO" },
            $"EXISTS (SELECT 1 FROM dbo.PUR_APPLY_D D WHERE {docDetail} "
            + "AND COP_ORDER_D.ORDER_TYPE=D.ORDER_TYPE AND COP_ORDER_D.ORDER_NO=D.ORDER_NO "
            + "AND COP_ORDER_D.SERIAL_NO=D.ORDER_SERIAL_NO)",
            new[] { at, an }));
        // Order MORE row: apply quantity accumulation fed by PUR_APPLY_D.
        specs.Add(new("COP_ORDER_MORE", new[] { "ORDER_TYPE", "ORDER_NO", "PRO_NO" },
            $"EXISTS (SELECT 1 FROM dbo.PUR_APPLY_D D WHERE {docDetail} "
            + "AND COP_ORDER_MORE.ORDER_TYPE=D.ORDER_TYPE AND COP_ORDER_MORE.ORDER_NO=D.ORDER_NO "
            + "AND COP_ORDER_MORE.PRO_NO=D.PRO_NO)",
            new[] { at, an }));
        return specs;
    }

    /// <summary>
    /// Stock check document (130101): the approval stamps the last check date onto the
    /// product and the depot balance rows (max-merge; deapprove is a no-op on both paths).
    /// </summary>
    private static IReadOnlyList<TableSpec> BuildTableSpecs130101(MasterContext master)
    {
        var ct = new SqlParameter("@ct", master.ReceiveType);
        var cn = new SqlParameter("@cn", master.ReceiveNo);
        var doc = "R.CHECK_STOCK_TYPE=@ct AND R.CHECK_STOCK_NO=@cn";
        var specs = new List<TableSpec>
        {
            new("INV_CHECK_STOCK_M", new[] { "CHECK_STOCK_TYPE", "CHECK_STOCK_NO" }, "@ct=CHECK_STOCK_TYPE AND @cn=CHECK_STOCK_NO", new[] { ct, cn }),
            new("INV_CHECK_STOCK_D", new[] { "CHECK_STOCK_TYPE", "CHECK_STOCK_NO", "SERIAL_NO" }, "@ct=CHECK_STOCK_TYPE AND @cn=CHECK_STOCK_NO", new[] { ct, cn }),
        };
        specs.Add(new("PRODUCT", new[] { "PRO_NO" },
            $"EXISTS (SELECT 1 FROM dbo.INV_CHECK_STOCK_D R WHERE {doc} AND PRODUCT.PRO_NO=R.PRO_NO)", new[] { ct, cn }));
        specs.Add(new("INV_PRO_DEPOT", new[] { "PRO_NO", "DEPOT_ID" },
            $"EXISTS (SELECT 1 FROM dbo.INV_CHECK_STOCK_D R WHERE {doc} "
            + "AND INV_PRO_DEPOT.PRO_NO=R.PRO_NO AND INV_PRO_DEPOT.DEPOT_ID=R.DEPOT_ID)", new[] { ct, cn }));
        return specs;
    }

    /// <summary>
    /// Warehouse transfer document (130105): a single line moves stock out of DEPOT_ID and
    /// into IN_DEPOT_ID, so the compared footprint is both depot balances, the product row
    /// and the inventory log.
    /// </summary>
    private static IReadOnlyList<TableSpec> BuildTableSpecs130105(MasterContext master)
    {
        var ot = new SqlParameter("@ot", master.ReceiveType);
        var on = new SqlParameter("@on", master.ReceiveNo);
        var doc = "R.OCCUR_TYPE=@ot AND R.OCCUR_NO=@on";
        var specs = new List<TableSpec>
        {
            new("INV_OCCUR_TRANSFER_M", new[] { "OCCUR_TYPE", "OCCUR_NO" }, "@ot=OCCUR_TYPE AND @on=OCCUR_NO", new[] { ot, on }),
            new("INV_OCCUR_TRANSFER_D", new[] { "OCCUR_TYPE", "OCCUR_NO", "SERIAL_NO" }, "@ot=OCCUR_TYPE AND @on=OCCUR_NO", new[] { ot, on }),
            new("PRODUCT", new[] { "PRO_NO" },
                $"EXISTS (SELECT 1 FROM dbo.INV_OCCUR_TRANSFER_D R WHERE {doc} AND PRODUCT.PRO_NO=R.PRO_NO)", new[] { ot, on }),
            new("INV_PRO_DEPOT", new[] { "PRO_NO", "DEPOT_ID" },
                $"EXISTS (SELECT 1 FROM dbo.INV_OCCUR_TRANSFER_D R WHERE {doc} "
                + "AND INV_PRO_DEPOT.PRO_NO=R.PRO_NO "
                + "AND (INV_PRO_DEPOT.DEPOT_ID=R.DEPOT_ID OR INV_PRO_DEPOT.DEPOT_ID=R.IN_DEPOT_ID))",
                new[] { ot, on }),
        };
        if (master.ReceiveDate is not null)
        {
            specs.Add(BuildLogSpecByMaster("130105", master));
        }
        return specs;
    }

    /// <summary>
    /// Miscellaneous stock in/out documents sharing the INV_OCCUR tables (130103/130104/
    /// 130110/2817/2818/3901): a single stock move, so the compared footprint is the
    /// document, the product row and the depot balance plus the inventory log.
    /// </summary>
    private static IReadOnlyList<TableSpec> BuildTableSpecsOccurInventory(
        MasterContext master, string masterTable, string detailTable)
    {
        var ot = new SqlParameter("@ot", master.ReceiveType);
        var on = new SqlParameter("@on", master.ReceiveNo);
        var doc = "R.OCCUR_TYPE=@ot AND R.OCCUR_NO=@on";
        var specs = new List<TableSpec>
        {
            new(masterTable, new[] { "OCCUR_TYPE", "OCCUR_NO" }, "@ot=OCCUR_TYPE AND @on=OCCUR_NO", new[] { ot, on }),
            new(detailTable, new[] { "OCCUR_TYPE", "OCCUR_NO", "SERIAL_NO" }, "@ot=OCCUR_TYPE AND @on=OCCUR_NO", new[] { ot, on }),
            new("PRODUCT", new[] { "PRO_NO" },
                $"EXISTS (SELECT 1 FROM dbo.{detailTable} R WHERE {doc} AND PRODUCT.PRO_NO=R.PRO_NO)",
                new[] { ot, on }),
            new("INV_PRO_DEPOT", new[] { "PRO_NO", "DEPOT_ID" },
                $"EXISTS (SELECT 1 FROM dbo.{detailTable} R WHERE {doc} "
                + "AND INV_PRO_DEPOT.PRO_NO=R.PRO_NO AND INV_PRO_DEPOT.DEPOT_ID=R.DEPOT_ID)",
                new[] { ot, on }),
        };
        if (master.ReceiveDate is not null)
        {
            specs.Add(BuildLogSpecByMaster(masterTable.StartsWith("INV_OCCUR_IN", StringComparison.OrdinalIgnoreCase)
                ? "130103"
                : "130104", master));
        }
        return specs;
    }

    /// <summary>
    /// Production-defect material return (1423): order-line write-back (BACK_MATERIAL /
    /// BACK_BAD chosen by BACK_CODE), the order completion recompute, the outbound stock
    /// leg and the inventory log. The stock row-set supplies the amount slots as
    /// configuration constants, so no amount column is read from this document.
    /// </summary>
    private static IReadOnlyList<TableSpec> BuildTableSpecs1423(MasterContext master)
    {
        var bt = new SqlParameter("@bt", master.ReceiveType);
        var bn = new SqlParameter("@bn", master.ReceiveNo);
        var specs = new List<TableSpec>
        {
            new("COP_BACK_M", new[] { "BACK_TYPE", "BACK_NO" },
                "@bt=BACK_TYPE AND @bn=BACK_NO", new[] { bt, bn }),
            new("COP_BACK_D", new[] { "BACK_TYPE", "BACK_NO", "SERIAL_NO" },
                "@bt=BACK_TYPE AND @bn=BACK_NO", new[] { bt, bn }),
        };
        // Order line write-back (field-accumulate) and order completion (completion-close).
        specs.Add(new("COP_ORDER_D", new[] { "ORDER_TYPE", "ORDER_NO", "SERIAL_NO" },
            "EXISTS (SELECT 1 FROM dbo.COP_BACK_D R WHERE R.BACK_TYPE=@bt AND R.BACK_NO=@bn "
            + "AND COP_ORDER_D.ORDER_TYPE=R.ORDER_TYPE AND COP_ORDER_D.ORDER_NO=R.ORDER_NO "
            + "AND COP_ORDER_D.SERIAL_NO=R.ORDER_SERIAL_NO)",
            new[] { bt, bn }));
        specs.Add(new("COP_ORDER_M", new[] { "ORDER_TYPE", "ORDER_NO" },
            "EXISTS (SELECT 1 FROM dbo.COP_BACK_D R WHERE R.BACK_TYPE=@bt AND R.BACK_NO=@bn "
            + "AND COP_ORDER_M.ORDER_TYPE=R.ORDER_TYPE AND COP_ORDER_M.ORDER_NO=R.ORDER_NO)",
            new[] { bt, bn }));
        // Stock footprint of the outbound move.
        specs.Add(new("PRODUCT", new[] { "PRO_NO" },
            "EXISTS (SELECT 1 FROM dbo.COP_BACK_D R WHERE R.BACK_TYPE=@bt AND R.BACK_NO=@bn "
            + "AND PRODUCT.PRO_NO=R.PRO_NO)",
            new[] { bt, bn }));
        specs.Add(new("INV_PRO_DEPOT", new[] { "PRO_NO", "DEPOT_ID" },
            "EXISTS (SELECT 1 FROM dbo.COP_BACK_D R WHERE R.BACK_TYPE=@bt AND R.BACK_NO=@bn "
            + "AND INV_PRO_DEPOT.PRO_NO=R.PRO_NO AND INV_PRO_DEPOT.DEPOT_ID=R.DEPOT_ID)",
            new[] { bt, bn }));
        if (master.ReceiveDate is not null)
        {
            specs.Add(BuildLogSpecByMaster("1423", master));
        }
        return specs;
    }

    private static IReadOnlyList<TableSpec> BuildTableSpecs2903(MasterContext master)
    {
        var at = new SqlParameter("@at", master.ReceiveType);
        var an = new SqlParameter("@an", master.ReceiveNo);
        var specs = new List<TableSpec>
        {
            new("MOU_APPLY_M", new[] { "APPLY_TYPE", "APPLY_NO" },
                "@at=APPLY_TYPE AND @an=APPLY_NO", new[] { at, an }),
            new("MOU_APPLY_D", new[] { "APPLY_TYPE", "APPLY_NO", "SERIAL_NO" },
                "@at=APPLY_TYPE AND @an=APPLY_NO", new[] { at, an }),
        };
        // link-stamp target: the assessment referencing this application.
        specs.Add(new("MOU_ASSESS_M", new[] { "ASSESS_TYPE", "ASSESS_NO" },
            "EXISTS (SELECT 1 FROM dbo.MOU_APPLY_M R WHERE R.APPLY_TYPE=@at AND R.APPLY_NO=@an "
            + "AND MOU_ASSESS_M.ASSESS_TYPE=R.ASSESS_TYPE AND MOU_ASSESS_M.ASSESS_NO=R.ASSESS_NO)",
            new[] { at, an }));
        return specs;
    }

    private static IReadOnlyList<TableSpec> BuildTableSpecs2904(MasterContext master)
    {
        var at = new SqlParameter("@at", master.ReceiveType);
        var an = new SqlParameter("@an", master.ReceiveNo);
        var specs = new List<TableSpec>
        {
            new("MOU_ACCEPT_M", new[] { "ACCEPT_TYPE", "ACCEPT_NO" },
                "@at=ACCEPT_TYPE AND @an=ACCEPT_NO", new[] { at, an }),
        };
        // link-stamp target: the application referenced by the accept master.
        specs.Add(new("MOU_APPLY_M", new[] { "APPLY_TYPE", "APPLY_NO" },
            "EXISTS (SELECT 1 FROM dbo.MOU_ACCEPT_M R WHERE R.ACCEPT_TYPE=@at AND R.ACCEPT_NO=@an "
            + "AND MOU_APPLY_M.APPLY_TYPE=R.APPLY_TYPE AND MOU_APPLY_M.APPLY_NO=R.APPLY_NO)",
            new[] { at, an }));
        specs.Add(new("MOU_APPLY_D", new[] { "APPLY_TYPE", "APPLY_NO", "SERIAL_NO" },
            "EXISTS (SELECT 1 FROM dbo.MOU_ACCEPT_M R WHERE R.ACCEPT_TYPE=@at AND R.ACCEPT_NO=@an "
            + "AND MOU_APPLY_D.APPLY_TYPE=R.APPLY_TYPE AND MOU_APPLY_D.APPLY_NO=R.APPLY_NO)",
            new[] { at, an }));
        specs.Add(new("PRODUCT", new[] { "PRO_NO" },
            "EXISTS (SELECT 1 FROM dbo.MOU_ACCEPT_M R WHERE R.ACCEPT_TYPE=@at AND R.ACCEPT_NO=@an "
            + "AND PRODUCT.PRO_NO=R.PRO_NO)",
            new[] { at, an }));
        return specs;
    }

    private static IReadOnlyList<TableSpec> BuildTableSpecs2907(MasterContext master)
    {
        var gt = new SqlParameter("@gt", master.ReceiveType);
        var gn = new SqlParameter("@gn", master.ReceiveNo);
        var specs = new List<TableSpec>
        {
            new("MOU_GET_M", new[] { "GET_TYPE", "GET_NO" },
                "@gt=GET_TYPE AND @gn=GET_NO", new[] { gt, gn }),
            new("MOU_GET_D", new[] { "GET_TYPE", "GET_NO", "SERIAL_NO" },
                "@gt=GET_TYPE AND @gn=GET_NO", new[] { gt, gn }),
        };
        // Dual-leg move touches both the issue depot and the receipt depot.
        specs.Add(new("PRODUCT", new[] { "PRO_NO" },
            "EXISTS (SELECT 1 FROM dbo.MOU_GET_D R WHERE R.GET_TYPE=@gt AND R.GET_NO=@gn "
            + "AND PRODUCT.PRO_NO=R.PRO_NO)",
            new[] { gt, gn }));
        specs.Add(new("INV_PRO_DEPOT", new[] { "PRO_NO", "DEPOT_ID" },
            "EXISTS (SELECT 1 FROM dbo.MOU_GET_D R WHERE R.GET_TYPE=@gt AND R.GET_NO=@gn "
            + "AND ((INV_PRO_DEPOT.PRO_NO=R.PRO_NO AND INV_PRO_DEPOT.DEPOT_ID=R.DEPOT_ID) "
            + "OR (INV_PRO_DEPOT.PRO_NO=R.PRO_NO AND INV_PRO_DEPOT.DEPOT_ID=R.IN_DEPOT_ID)))",
            new[] { gt, gn }));
        if (master.ReceiveDate is not null)
        {
            specs.Add(BuildLogSpecByMaster("2907", master));
        }
        return specs;
    }

    private static IReadOnlyList<TableSpec> BuildTableSpecs2913(MasterContext master)
    {
        var gt = new SqlParameter("@gt", master.ReceiveType);
        var gn = new SqlParameter("@gn", master.ReceiveNo);
        var specs = new List<TableSpec>
        {
            new("MOU_GET2_M", new[] { "GET_TYPE", "GET_NO" },
                "@gt=GET_TYPE AND @gn=GET_NO", new[] { gt, gn }),
            new("MOU_GET2_D", new[] { "GET_TYPE", "GET_NO", "SERIAL_NO" },
                "@gt=GET_TYPE AND @gn=GET_NO", new[] { gt, gn }),
        };
        // set-state targets reached through the apply refs on the consume lines,
        // then the accept refs on the application (apply -> accept -> batch chain).
        specs.Add(new("MOU_APPLY_M", new[] { "APPLY_TYPE", "APPLY_NO" },
            "EXISTS (SELECT 1 FROM dbo.MOU_GET2_D R WHERE R.GET_TYPE=@gt AND R.GET_NO=@gn "
            + "AND MOU_APPLY_M.APPLY_TYPE=R.APPLY_TYPE AND MOU_APPLY_M.APPLY_NO=R.APPLY_NO)",
            new[] { gt, gn }));
        specs.Add(new("MOU_APPLY_D", new[] { "APPLY_TYPE", "APPLY_NO", "SERIAL_NO" },
            "EXISTS (SELECT 1 FROM dbo.MOU_GET2_D R WHERE R.GET_TYPE=@gt AND R.GET_NO=@gn "
            + "AND MOU_APPLY_D.APPLY_TYPE=R.APPLY_TYPE AND MOU_APPLY_D.APPLY_NO=R.APPLY_NO)",
            new[] { gt, gn }));
        specs.Add(new("MOU_BATCH_M", new[] { "BATCH_TYPE", "BATCH_NO" },
            "EXISTS (SELECT 1 FROM dbo.MOU_GET2_D R JOIN dbo.MOU_APPLY_M A "
            + "ON A.APPLY_TYPE=R.APPLY_TYPE AND A.APPLY_NO=R.APPLY_NO "
            + "WHERE R.GET_TYPE=@gt AND R.GET_NO=@gn "
            + "AND MOU_BATCH_M.ACCEPT_TYPE=A.ACCEPT_TYPE AND MOU_BATCH_M.ACCEPT_NO=A.ACCEPT_NO)",
            new[] { gt, gn }));
        specs.Add(new("PRODUCT", new[] { "PRO_NO" },
            "EXISTS (SELECT 1 FROM dbo.MOU_GET2_D R WHERE R.GET_TYPE=@gt AND R.GET_NO=@gn "
            + "AND PRODUCT.PRO_NO=R.PRO_NO)",
            new[] { gt, gn }));
        specs.Add(new("INV_PRO_DEPOT", new[] { "PRO_NO", "DEPOT_ID" },
            "EXISTS (SELECT 1 FROM dbo.MOU_GET2_D R WHERE R.GET_TYPE=@gt AND R.GET_NO=@gn "
            + "AND INV_PRO_DEPOT.PRO_NO=R.PRO_NO AND INV_PRO_DEPOT.DEPOT_ID=R.DEPOT_ID)",
            new[] { gt, gn }));
        if (master.ReceiveDate is not null)
        {
            specs.Add(BuildLogSpecByMaster("2913", master));
        }
        return specs;
    }

    private static IReadOnlyList<TableSpec> BuildTableSpecs2906(MasterContext master)
    {
        var bt = new SqlParameter("@bt", master.ReceiveType);
        var bn = new SqlParameter("@bn", master.ReceiveNo);
        var specs = new List<TableSpec>
        {
            new("MOU_BATCH_M", new[] { "BATCH_TYPE", "BATCH_NO" },
                "@bt=BATCH_TYPE AND @bn=BATCH_NO", new[] { bt, bn }),
            new("MOU_BATCH_D", new[] { "BATCH_TYPE", "BATCH_NO", "SERIAL_NO" },
                "@bt=BATCH_TYPE AND @bn=BATCH_NO", new[] { bt, bn }),
        };
        specs.Add(new("MOU_ACCEPT_M", new[] { "ACCEPT_TYPE", "ACCEPT_NO" },
            "EXISTS (SELECT 1 FROM dbo.MOU_BATCH_M R WHERE R.BATCH_TYPE=@bt AND R.BATCH_NO=@bn "
            + "AND MOU_ACCEPT_M.ACCEPT_TYPE=R.ACCEPT_TYPE AND MOU_ACCEPT_M.ACCEPT_NO=R.ACCEPT_NO)",
            new[] { bt, bn }));
        specs.Add(new("MOU_SCRAP_D", new[] { "SCRAP_TYPE", "SCRAP_NO", "SERIAL_NO" },
            "EXISTS (SELECT 1 FROM dbo.MOU_BATCH_M R WHERE R.BATCH_TYPE=@bt AND R.BATCH_NO=@bn "
            + "AND MOU_SCRAP_D.SCRAP_TYPE=R.SCRAP_TYPE AND MOU_SCRAP_D.SCRAP_NO=R.SCRAP_NO "
            + "AND MOU_SCRAP_D.SERIAL_NO=R.SCRAP_SERIAL_NO)",
            new[] { bt, bn }));
        specs.Add(new("PRODUCT", new[] { "PRO_NO" },
            "EXISTS (SELECT 1 FROM dbo.MOU_BATCH_M R WHERE R.BATCH_TYPE=@bt AND R.BATCH_NO=@bn "
            + "AND PRODUCT.PRO_NO=R.PRO_NO)",
            new[] { bt, bn }));
        return specs;
    }

    private static IReadOnlyList<TableSpec> BuildTableSpecs180106(MasterContext master)
    {
        var ct = new SqlParameter("@ct", master.ReceiveType);
        var cn = new SqlParameter("@cn", master.ReceiveNo);
        var specs = new List<TableSpec>
        {
            new("HR_CONTRACT_M", new[] { "CONT_TYPE", "CONT_NO" },
                "@ct=CONT_TYPE AND @cn=CONT_NO", new[] { ct, cn }),
            new("HR_CONTRACT_D", new[] { "CONT_TYPE", "CONT_NO", "SERIAL_NO" },
                "@ct=CONT_TYPE AND @cn=CONT_NO", new[] { ct, cn }),
        };
        specs.Add(new("HR_EMPLOYEE", new[] { "EMP_ID" },
            "EXISTS (SELECT 1 FROM dbo.HR_CONTRACT_D R WHERE R.CONT_TYPE=@ct AND R.CONT_NO=@cn "
            + "AND HR_EMPLOYEE.EMP_ID=R.EMP_ID)",
            new[] { ct, cn }));
        return specs;
    }

    private static IReadOnlyList<TableSpec> BuildTableSpecs180206(MasterContext master)
    {
        var at = new SqlParameter("@at", master.ReceiveType);
        var an = new SqlParameter("@an", master.ReceiveNo);
        var specs = new List<TableSpec>
        {
            new("HR_APPLY_M", new[] { "APPLY_TYPE", "APPLY_NO" },
                "@at=APPLY_TYPE AND @an=APPLY_NO", new[] { at, an }),
            new("HR_APPLY_D", new[] { "APPLY_TYPE", "APPLY_NO", "SERIAL_NO" },
                "@at=APPLY_TYPE AND @an=APPLY_NO", new[] { at, an }),
        };
        // 180206 hr-usage targets HR_ENACTMENT_D (monthly row per employee); the
        // employee lives on the target detail, the month on the target master.
        specs.Add(new("HR_ENACTMENT_M", new[] { "ENACTMENT_TYPE", "ENACTMENT_NO" },
            "EXISTS (SELECT 1 FROM dbo.HR_APPLY_M A JOIN dbo.HR_APPLY_D AD "
            + "ON AD.APPLY_TYPE=A.APPLY_TYPE AND AD.APPLY_NO=A.APPLY_NO "
            + "JOIN dbo.HR_ENACTMENT_D ED ON ED.EMP_ID=AD.EMP_ID "
            + "WHERE A.APPLY_TYPE=@at AND A.APPLY_NO=@an "
            + "AND HR_ENACTMENT_M.COUNT_MONTH=CONVERT(varchar(6), A.COUNT_DATE, 112) "
            + "AND HR_ENACTMENT_M.ENACTMENT_TYPE=ED.ENACTMENT_TYPE "
            + "AND HR_ENACTMENT_M.ENACTMENT_NO=ED.ENACTMENT_NO)",
            new[] { at, an }));
        specs.Add(new("HR_ENACTMENT_D", new[] { "ENACTMENT_TYPE", "ENACTMENT_NO", "EMP_ID", "SERIAL_NO" },
            "EXISTS (SELECT 1 FROM dbo.HR_APPLY_M A JOIN dbo.HR_APPLY_D AD "
            + "ON AD.APPLY_TYPE=A.APPLY_TYPE AND AD.APPLY_NO=A.APPLY_NO "
            + "WHERE A.APPLY_TYPE=@at AND A.APPLY_NO=@an AND AD.EMP_ID=HR_ENACTMENT_D.EMP_ID "
            + "AND EXISTS (SELECT 1 FROM dbo.HR_ENACTMENT_M EM "
            + "WHERE EM.ENACTMENT_TYPE=HR_ENACTMENT_D.ENACTMENT_TYPE "
            + "AND EM.ENACTMENT_NO=HR_ENACTMENT_D.ENACTMENT_NO "
            + "AND EM.COUNT_MONTH=CONVERT(varchar(6), A.COUNT_DATE, 112)))",
            new[] { at, an }));
        return specs;
    }

    private static IReadOnlyList<TableSpec> BuildTableSpecs180207(MasterContext master)
    {
        var wt = new SqlParameter("@wt", master.ReceiveType);
        var wn = new SqlParameter("@wn", master.ReceiveNo);
        var specs = new List<TableSpec>
        {
            new("HR_WORKTIME_M", new[] { "WORKTIME_TYPE", "WORKTIME_NO" },
                "@wt=WORKTIME_TYPE AND @wn=WORKTIME_NO", new[] { wt, wn }),
            new("HR_WORKTIME_D", new[] { "WORKTIME_TYPE", "WORKTIME_NO", "SERIAL_NO" },
                "@wt=WORKTIME_TYPE AND @wn=WORKTIME_NO", new[] { wt, wn }),
        };
        // 180207 hr-usage targets HR_APPLY_D (daily row per employee).
        specs.Add(new("HR_APPLY_M", new[] { "APPLY_TYPE", "APPLY_NO" },
            "EXISTS (SELECT 1 FROM dbo.HR_WORKTIME_M W JOIN dbo.HR_WORKTIME_D WD "
            + "ON WD.WORKTIME_TYPE=W.WORKTIME_TYPE AND WD.WORKTIME_NO=W.WORKTIME_NO "
            + "JOIN dbo.HR_APPLY_D AD ON AD.EMP_ID=WD.EMP_ID "
            + "WHERE W.WORKTIME_TYPE=@wt AND W.WORKTIME_NO=@wn "
            + "AND HR_APPLY_M.COUNT_DATE=W.COUNT_DATE "
            + "AND HR_APPLY_M.APPLY_TYPE=AD.APPLY_TYPE AND HR_APPLY_M.APPLY_NO=AD.APPLY_NO)",
            new[] { wt, wn }));
        specs.Add(new("HR_APPLY_D", new[] { "APPLY_TYPE", "APPLY_NO", "SERIAL_NO" },
            "EXISTS (SELECT 1 FROM dbo.HR_WORKTIME_M W JOIN dbo.HR_WORKTIME_D WD "
            + "ON WD.WORKTIME_TYPE=W.WORKTIME_TYPE AND WD.WORKTIME_NO=W.WORKTIME_NO "
            + "WHERE W.WORKTIME_TYPE=@wt AND W.WORKTIME_NO=@wn AND WD.EMP_ID=HR_APPLY_D.EMP_ID "
            + "AND EXISTS (SELECT 1 FROM dbo.HR_APPLY_M AM "
            + "WHERE AM.APPLY_TYPE=HR_APPLY_D.APPLY_TYPE AND AM.APPLY_NO=HR_APPLY_D.APPLY_NO "
            + "AND AM.COUNT_DATE=W.COUNT_DATE))",
            new[] { wt, wn }));
        return specs;
    }

    private static IReadOnlyList<TableSpec> BuildTableSpecs180310(MasterContext master)
    {
        var wt = new SqlParameter("@wt", master.ReceiveType);
        var wn = new SqlParameter("@wn", master.ReceiveNo);
        var specs = new List<TableSpec>
        {
            new("HR_WAGE_M", new[] { "WAGE_TYPE", "WAGE_NO" },
                "@wt=WAGE_TYPE AND @wn=WAGE_NO", new[] { wt, wn }),
            new("HR_WAGE_D", new[] { "WAGE_TYPE", "WAGE_NO", "SERIAL_NO" },
                "@wt=WAGE_TYPE AND @wn=WAGE_NO", new[] { wt, wn }),
        };
        // 180310/1803101 employee-dimission-sync: approve marks the document's wage
        // detail employees dimitted (STATE=5, DIMISSION_DATE from HR_DIMISSION_M), and
        // deapprove restores the same employee set to active — the effect lives on
        // HR_EMPLOYEE, correlated through HR_WAGE_D.
        specs.Add(new("HR_EMPLOYEE", new[] { "EMP_ID" },
            "EXISTS (SELECT 1 FROM dbo.HR_WAGE_D R WHERE R.WAGE_TYPE=@wt AND R.WAGE_NO=@wn "
            + "AND HR_EMPLOYEE.EMP_ID=R.EMP_ID)",
            new[] { wt, wn }));
        return specs;
    }

    /// <summary>
    /// Over-produce predicate for 1505 selection (group-level SUM, old-SP semantics):
    /// document quantity plus finished plus scrap exceeds the produce order quantity.
    /// </summary>
    private const string ProductInOverProduce = """
        EXISTS(
          SELECT 1 FROM (SELECT S.PRODUCE_TYPE, S.PRODUCE_NO,
                           SUM(COALESCE(S.QTY,0)) QTY, SUM(COALESCE(S.SPARE_QTY,0)) SPARE_QTY
                         FROM dbo.MOC_PRODUCT_IN_D S
                         WHERE S.PRODUCT_IN_TYPE=M.PRODUCT_IN_TYPE AND S.PRODUCT_IN_NO=M.PRODUCT_IN_NO
                         GROUP BY S.PRODUCE_TYPE, S.PRODUCE_NO) G
          JOIN dbo.MOC_PRODUCE_M T ON T.PRODUCE_TYPE=G.PRODUCE_TYPE AND T.PRODUCE_NO=G.PRODUCE_NO
         WHERE G.QTY+COALESCE(T.FINISHED_QTY,0)+COALESCE(T.SCRAP_IN_QTY,0) > COALESCE(T.QTY,0)
            OR G.SPARE_QTY+COALESCE(T.FINISHED_SPARE_QTY,0)+COALESCE(T.SCRAP_IN_SPARE_QTY,0) > COALESCE(T.SPARE_QTY,0))
        """;

    private static async Task<IReadOnlyList<string>> ResolveRecordKeys1505Async(
        SqlConnection connection, bool deapprove, bool failure)
    {
        const string noLogHistory = """
            NOT EXISTS (SELECT 1 FROM dbo.INV_DEPOT_LOG L
                        WHERE LTRIM(RTRIM(L.MUTUALITY_TYPE))=LTRIM(RTRIM(M.PRODUCT_IN_TYPE))
                          AND LTRIM(RTRIM(L.MUTUALITY_NO))=LTRIM(RTRIM(M.PRODUCT_IN_NO)))
            """;
        string sql;
        string emptyMessage;
        if (failure)
        {
            sql = "SELECT TOP 1 M.PRODUCT_IN_TYPE, M.PRODUCT_IN_NO FROM dbo.MOC_PRODUCT_IN_M M "
                + "WHERE ISNULL(M.CONFIRM_TAG,0)=0 AND " + noLogHistory
                + " AND " + ProductInOverProduce
                + " ORDER BY M.PRODUCT_IN_DATE DESC, M.PRODUCT_IN_NO DESC;";
            emptyMessage = "未找到可触发失败分支的超收生产入库单。";
        }
        else if (deapprove)
        {
            // Inbound reversal shrinks stock: only docs whose lines still fit in stock.
            sql = """
                SELECT TOP 1 M.PRODUCT_IN_TYPE, M.PRODUCT_IN_NO
                FROM dbo.MOC_PRODUCT_IN_M M
                WHERE ISNULL(M.CONFIRM_TAG,0)=1
                  AND EXISTS (SELECT 1 FROM dbo.INV_DEPOT_LOG L
                              WHERE LTRIM(RTRIM(L.MUTUALITY_TYPE))=LTRIM(RTRIM(M.PRODUCT_IN_TYPE))
                                AND LTRIM(RTRIM(L.MUTUALITY_NO))=LTRIM(RTRIM(M.PRODUCT_IN_NO)))
                  AND NOT EXISTS (
                      SELECT 1
                      FROM (SELECT D.PRO_NO, D.DEPOT_ID,
                                   SUM(ISNULL(D.QTY,0) + ISNULL(D.SPARE_QTY,0)) QTY
                            FROM dbo.MOC_PRODUCT_IN_D D
                            WHERE D.PRODUCT_IN_TYPE=M.PRODUCT_IN_TYPE AND D.PRODUCT_IN_NO=M.PRODUCT_IN_NO
                            GROUP BY D.PRO_NO, D.DEPOT_ID) X
                      JOIN dbo.INV_PRO_DEPOT S
                        ON S.PRO_NO=X.PRO_NO AND S.DEPOT_ID=X.DEPOT_ID
                      WHERE ISNULL(S.QTY,0) + 0.001 < X.QTY)
                ORDER BY ISNULL(M.CONFIRM_DATE, M.PRODUCT_IN_DATE) DESC, M.PRODUCT_IN_NO DESC;
                """;
            emptyMessage = "未找到可解批对拍的已批核生产入库单（自动选单无结果）。";
        }
        else
        {
            sql = "SELECT TOP 1 M.PRODUCT_IN_TYPE, M.PRODUCT_IN_NO FROM dbo.MOC_PRODUCT_IN_M M "
                + "WHERE ISNULL(M.CONFIRM_TAG,0)=0 AND " + noLogHistory
                + " AND EXISTS (SELECT 1 FROM dbo.MOC_PRODUCT_IN_D D"
                + " WHERE D.PRODUCT_IN_TYPE=M.PRODUCT_IN_TYPE AND D.PRODUCT_IN_NO=M.PRODUCT_IN_NO)"
                + " AND NOT " + ProductInOverProduce
                + " ORDER BY M.PRODUCT_IN_DATE DESC, M.PRODUCT_IN_NO DESC;";
            emptyMessage = "未找到可对拍的未批核生产入库单（自动选单无结果）。";
        }
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            throw new InvalidOperationException(emptyMessage);
        }
        return new[] { reader.GetString(0).Trim(), reader.GetString(1).Trim() };
    }

    private static IReadOnlyList<TableSpec> BuildTableSpecs1406(MasterContext master, IReadOnlyList<DetailRow> details)
    {
        var specs = new List<TableSpec>
        {
            new("COP_SEND_M", new[] { "SEND_TYPE", "SEND_NO" },
                "@st=SEND_TYPE AND @sn=SEND_NO", new[] { new SqlParameter("@st", master.ReceiveType), new SqlParameter("@sn", master.ReceiveNo) }),
            new("COP_SEND_D", new[] { "SEND_TYPE", "SEND_NO", "SERIAL_NO" },
                "@st=SEND_TYPE AND @sn=SEND_NO", new[] { new SqlParameter("@st", master.ReceiveType), new SqlParameter("@sn", master.ReceiveNo) }),
        };

        var orderLineKeys = details
            .Where(row => row.OrderType.Length > 0 && row.OrderNo.Length > 0 && row.OrderSerialNo is not null)
            .Select(row => (row.OrderType, row.OrderNo, row.OrderSerialNo!.Value))
            .Distinct()
            .ToArray();
        if (orderLineKeys.Length > 0)
        {
            specs.Add(BuildValuesSpec("COP_ORDER_D",
                new[] { "ORDER_TYPE", "ORDER_NO", "SERIAL_NO" },
                orderLineKeys, "ORDER_TYPE", "ORDER_NO", "SERIAL_NO"));
        }

        var shipmentLineKeys = details
            .Where(row => row.ShipmentType.Length > 0 && row.ShipmentNo.Length > 0 && row.ShipmentSerialNo is not null)
            .Select(row => (row.ShipmentType, row.ShipmentNo, row.ShipmentSerialNo!.Value))
            .Distinct()
            .ToArray();
        if (shipmentLineKeys.Length > 0)
        {
            specs.Add(BuildValuesSpec("COP_SHIPMENT_D",
                new[] { "SHIPMENT_TYPE", "SHIPMENT_NO", "SERIAL_NO" },
                shipmentLineKeys, "SHIPMENT_TYPE", "SHIPMENT_NO", "SERIAL_NO"));
        }

        var produceKeys = details
            .Where(row => row.ProduceType.Length > 0 && row.ProduceNo.Length > 0)
            .Select(row => (row.ProduceType, row.ProduceNo))
            .Distinct()
            .ToArray();
        if (produceKeys.Length > 0)
        {
            specs.Add(BuildValuesSpec("MOC_PRODUCE_M", new[] { "PRODUCE_TYPE", "PRODUCE_NO" },
                produceKeys, "PRODUCE_TYPE", "PRODUCE_NO"));
        }

        var fitoutLineKeys = details
            .Where(row => row.FitoutType.Length > 0 && row.FitoutNo.Length > 0 && row.FitoutSerialNo is not null)
            .Select(row => (row.FitoutType, row.FitoutNo, row.FitoutSerialNo!.Value))
            .Distinct()
            .ToArray();
        if (fitoutLineKeys.Length > 0)
        {
            specs.Add(BuildValuesSpec("COP_FITOUT_D",
                new[] { "FITOUT_TYPE", "FITOUT_NO", "SERIAL_NO" },
                fitoutLineKeys, "FITOUT_TYPE", "FITOUT_NO", "SERIAL_NO"));
        }

        var productKeys = details.Select(row => row.ProNo).Where(pro => pro.Length > 0).Distinct().ToArray();
        if (productKeys.Length > 0)
        {
            specs.Add(new("PRODUCT", new[] { "PRO_NO" }, InClause("PRO_NO", productKeys), productKeys.Select((value, index) => new SqlParameter("@p" + index, value)).ToArray()));
        }

        if (master.SupplierId is not null)
        {
            specs.Add(new("CLIENT", new[] { "CLIENT_ID" }, "CLIENT_ID=@cid", new[] { new SqlParameter("@cid", master.SupplierId) }));
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
                specs.Add(BuildBatchDetailSpec("1406", master.ReceiveDate.Value, master.ReceiveType, master.ReceiveNo, batchKeys));
            }
        }

        if (master.ReceiveDate is not null
            && details.Any(row => row.SerialNo.HasValue)
            && details.Any(row => row.ProNo.Length > 0)
            && details.Any(row => row.DepotId.Length > 0))
        {
            specs.Add(BuildLogSpec("1406", master, details));
        }
        return specs;
    }

    private static TableSpec BuildLogSpec(string moduleNumber, MasterContext master, IReadOnlyList<DetailRow> details)
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
            .Append($" AND ((MUTUALITY_TYPE='{moduleNumber}' AND MUTUALITY_NO=@rt) OR (MUTUALITY_TYPE=@rt AND MUTUALITY_NO=@rn))")
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
        string moduleNumber,
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
            .Append($" AND ((BATCH_ORDER_TYPE='{moduleNumber}' AND BATCH_ORDER_NO=@rt) OR (BATCH_ORDER_TYPE=@rt AND BATCH_ORDER_NO=@rn))")
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
            // Null and empty string are the same "unspecified" value for text columns
            // (empty-value semantics, decision #61): a null-vs-empty diff is normalised.
            var oldText = NormalizeValue(oldValue) as string;
            var newText = NormalizeValue(newValue) as string;
            if ((oldText is { Length: 0 }) || (newText is { Length: 0 }))
            {
                return (true, true);
            }
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
        bool Failure = false,
        bool EngineOnly = false);

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
        int? SerialNo,
        int? OrderSerialNo = null,
        string ShipmentType = "",
        string ShipmentNo = "",
        int? ShipmentSerialNo = null,
        string ProduceType = "",
        string ProduceNo = "",
        string FitoutType = "",
        string FitoutNo = "",
        int? FitoutSerialNo = null,
        string BadDepotId = "");

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
