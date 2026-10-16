using System.Text.Json;
using Microsoft.Data.SqlClient;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 库存**配置面**收口（连库）：代码收口之后，配置仍能让运行期拼出直读库存表的 SQL，
/// 而这类引用在 C# 里一个字都看不到——只扫代码会漏掉这半边。
///
/// 原先由脚本门禁 `check-inventory-read-hosts.ps1`（配置侧）与
/// `check-stock-depot-level-consistency.ps1`（① ② ③ ⑤配置侧 ⑥）守着，两者都已退役
/// （见 `scripts/README.md` 退役清单），本文件承接它们：
///
///  ① 配置面引用库存表必须落在白名单内（双向：白名单外的引用即失败；白名单条目消失也失败）；
///  ② 引擎维护的库存列必须只读（可编辑即等于绕过移动引擎改库存口径，零命中类，不得进白名单）；
///  ③ 盘点单明细的判重键必须含库位与批次（零命中类）；
///  ④ 主表 / 明细表是库存表的模块不得进统一表单**写**名单（"能看"与"能改"分开管；只读名单不是豁免）；
///  ⑤ 策略表 `DEPOT_STOCK_POLICY` 不得被声明进配置面（求值口径必须唯一）；
///  ⑥ 库别级字段同键内不得取值分裂、策略档位取值必须已实现、位置号不得带尾随空格、
///     半成品账的键必须齐全且不与主账重叠。
///
/// 只读，不建夹具；无连接串即失败（无连接跳过≠已验证）。
/// 夹具前缀（`E2E%` / `ZZ%`）在两处逐键扫描里排除——那是开发库卫生，不是库存问题。
/// </summary>
[Trait("Category", "Integration")]
[Collection("live-database")]
public sealed class InventoryConfigFaceLiveTests
{
    /// <summary>
    /// 配置白名单（FAIL 类）。写的是**当前库里实际存在**的那几条，双向断言：
    /// 出现清单之外的引用即失败；清单里的条目在库里消失也会失败（棘轮，只减不增）。
    /// 每条都要能说出"为什么可以留着"。
    /// </summary>
    private static readonly string[] ConfigAllowed =
    {
        "FIELDS:VIRTUAL_EXP:INV_PRO_DEPOT.SUB_QTY",      // 行级虚拟列：逐行求值语义正确，且无聚合消费方
        "FIELDS:VIRTUAL_EXP:INV_PRO_DEPOT.LONGTH",       // 同上（另一列的表达式）
        "MODULES:MASTER_TABLE:1302",                     // 料件批号资料：以批次账为主表的只读查询模块（写路径已封）
        "MODULES:MASTER_TABLE:1303",                     // 料件库存资料：以余额表为主表的只读查询模块（写路径已封）
        "MODULES:MASTER_TABLE:1305",                     // 库存日志：以流水表为主表的查询模块（本就不在写名单内）
        "VALIDATION:1406:custom-validation",             // 库存来源声明；处理器有 fail-closed 守卫：配的表必须就是余额表本身
        "ACTION:130101:stocktake-scope-generate",        // 同上（库存来源声明 + 同一道守卫）
    };

    private static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("MSSQL_ERP_CONN")
        ?? throw new InvalidOperationException("真库集成测试需要 MSSQL_ERP_CONN；未配置即失败（无连接跳过≠已验证）。");

    /// <summary>逐键扫描里排除的自造夹具前缀（E2E 跑批残留 + 真库用例的 ZZ…）。</summary>
    private const string NotFixture = "PRO_NO NOT LIKE N'E2E%' AND PRO_NO NOT LIKE N'ZZ%'";

    [Fact]
    public async Task 配置面引用库存表必须落在白名单内()
    {
        await using var connection = await OpenAsync();

        // 与退役门禁同口径：SQL 文本列用大小写不敏感匹配；主表 / 明细表按表名精确取。
        var hits = await RowsAsync(connection, """
            SET NOCOUNT ON;
            SELECT HIT FROM (
                SELECT N'FIELDS:VIRTUAL_EXP:' + T_ID + '.' + F_ID AS HIT FROM dbo.FIELDS
                 WHERE ISNULL(VIRTUAL_EXP,'') LIKE '%INV_PRO_DEPOT%' OR ISNULL(VIRTUAL_EXP,'') LIKE '%INV_DEPOT_LOG%'
                UNION ALL
                SELECT N'FIELDS:CONVERT_FUNCTION:' + T_ID + '.' + F_ID FROM dbo.FIELDS
                 WHERE ISNULL(CONVERT_FUNCTION,'') LIKE '%INV_PRO_DEPOT%' OR ISNULL(CONVERT_FUNCTION,'') LIKE '%INV_DEPOT_LOG%'
                UNION ALL
                SELECT N'MODULES:FILTER:' + CONVERT(nvarchar(10), M_IDX) FROM dbo.MODULES
                 WHERE ISNULL(FILTER,'') LIKE '%INV_PRO_DEPOT%' OR ISNULL(FILTER,'') LIKE '%INV_DEPOT_LOG%'
                UNION ALL
                SELECT N'MODULES:MASTER_TABLE:' + CONVERT(nvarchar(10), M_IDX) FROM dbo.MODULES
                 WHERE MASTER_TABLE IN ('INV_PRO_DEPOT','INV_DEPOT_LOG','INV_BATCH_M')
                UNION ALL
                SELECT N'MODULES:DETAIL_TABLE:' + CONVERT(nvarchar(10), M_IDX) FROM dbo.MODULES
                 WHERE DETAIL_TABLE IN ('INV_PRO_DEPOT','INV_DEPOT_LOG','INV_BATCH_M')
                UNION ALL
                SELECT N'VALIDATION:' + CONVERT(nvarchar(10), M_IDX) + ':' + VALIDATION_KEY FROM dbo.MODULE_VALIDATION_RULE
                 WHERE ISNULL(PARAM_STRUCT,'') LIKE '%INV_PRO_DEPOT%' OR ISNULL(PARAM_STRUCT,'') LIKE '%INV_DEPOT_LOG%'
                UNION ALL
                SELECT N'ACTION:' + CONVERT(nvarchar(10), M_IDX) + ':' + EFFECT_KEY FROM dbo.MODULE_BUSINESS_ACTION
                 WHERE ISNULL(PARAM_STRUCT,'') LIKE '%INV_PRO_DEPOT%' OR ISNULL(PARAM_STRUCT,'') LIKE '%INV_DEPOT_LOG%'
            ) y;
            """);

        var actual = hits.OrderBy(x => x, StringComparer.Ordinal).ToList();
        var unregistered = actual.Except(ConfigAllowed, StringComparer.Ordinal).ToList();
        var stale = ConfigAllowed.Except(actual, StringComparer.Ordinal).ToList();

        Assert.True(unregistered.Count == 0,
            $"这些配置引用直接碰库存表（须改走 InventoryQueryService 或进白名单并写明理由）：{string.Join("、", unregistered)}");
        Assert.True(stale.Count == 0,
            $"白名单条目在库里已不存在，应当移除：{string.Join("、", stale)}");
    }

    [Fact]
    public async Task 引擎维护的库存列必须只读()
    {
        await using var connection = await OpenAsync();

        // 余额/流水/批次账由移动引擎写，产品主档的库存与 MRP 派生列由 MRP 重算回写；
        // 可编辑即等于绕过引擎改库存口径（批次账的 IN_SUM - OUT_SUM 正是引擎判定批号是否足够的依据）。
        // 本类**不得进白名单**：期望命中 0 条。
        var editable = await RowsAsync(connection, """
            SET NOCOUNT ON;
            SELECT CONCAT(T_ID, '.', F_ID) FROM dbo.FIELDS
             WHERE ISNULL(IS_READONLY,0) = 0 AND (
                   (T_ID = 'PRODUCT' AND F_ID IN ('QTY','MRP_QTY','NOT_SEND_QTY','NOT_IN_QTY','NOT_GET_QTY','IN_BUY_QTY',
                                                  'MRP_QTY_ABS','SAFETY_MRP_QTY','AMOUNT'))
                OR (T_ID = 'INV_BATCH_M' AND F_ID IN ('IN_SUM','OUT_SUM','LATELY_IN_DATE','LATELY_OUT_DATE','LATELY_CHECK_DATE')));
            """);
        Assert.True(editable.Count == 0,
            $"引擎维护的库存列被放开可编辑（绕过引擎改口径）：{string.Join("、", editable)}");
    }

    [Fact]
    public async Task 盘点单明细的判重键必须含库位与批次()
    {
        await using var connection = await OpenAsync();

        // 两键判重会把同料号同库别下的多个库位/批次误判成明细重复，而按库区生成明细恰恰会写出这样的多行。
        var bad = await ScalarAsync<int>(connection, """
            SET NOCOUNT ON;
            SELECT COUNT(*) FROM dbo.TABLES
             WHERE T_ID = 'INV_CHECK_STOCK_D'
               AND (ISNULL(DF_VERIFY,'') NOT LIKE '%LOCATION_NO%' OR ISNULL(DF_VERIFY,'') NOT LIKE '%BATCH_NO%');
            """);
        Assert.Equal(0, bad);
    }

    [Fact]
    public async Task 库存表当主表或明细表的模块不得允许通用表单写入()
    {
        await using var connection = await OpenAsync();
        // 哨兵：库里总得有模块"可写"，否则下面的断言对任何配置都会通过（判据恒真）
        var writableCount = await ScalarAsync<int>(connection, """
            SET NOCOUNT ON;
            SELECT COUNT(*) FROM dbo.MODULES
             WHERE UNIFIED_FORM_ENABLED = 1 AND UNIFIED_FORM_EDIT_ENABLED = 1;
            """);
        Assert.True(writableCount > 0, "库里没有一个可写的统一表单模块——能力位根本没读到，这条断言会恒真");

        var leaked = (await RowsAsync(connection, """
            SET NOCOUNT ON;
            SELECT CONVERT(nvarchar(20), M_IDX) + N':' + LTRIM(RTRIM(M_DESC)) FROM dbo.MODULES
             WHERE (MASTER_TABLE IN ('INV_PRO_DEPOT','INV_DEPOT_LOG','INV_BATCH_M')
                 OR DETAIL_TABLE IN ('INV_PRO_DEPOT','INV_DEPOT_LOG','INV_BATCH_M'))
               AND UNIFIED_FORM_EDIT_ENABLED = 1
             ORDER BY M_IDX;
            """)).ToList();
        Assert.True(leaked.Count == 0,
            $"主表 / 明细表是库存表的模块声明了「统一表单可写」（通用表单会绕过移动引擎直接改账）：{string.Join("、", leaked)}");
    }

    [Fact]
    public async Task 策略表不得出现在配置面()
    {
        await using var connection = await OpenAsync();

        // "库别无覆盖 → 部署级默认"两跳求值只允许出现在 DepotStockPolicyService 一处。
        var hits = await RowsAsync(connection, """
            SET NOCOUNT ON;
            SELECT HIT FROM (
                SELECT N'校验规则 ' + CONVERT(nvarchar(10), M_IDX) AS HIT FROM dbo.MODULE_VALIDATION_RULE
                 WHERE ISNULL(PARAM_STRUCT,'') LIKE '%DEPOT_STOCK_POLICY%'
                UNION ALL
                SELECT N'业务动作 ' + CONVERT(nvarchar(10), M_IDX) FROM dbo.MODULE_BUSINESS_ACTION
                 WHERE ISNULL(PARAM_STRUCT,'') LIKE '%DEPOT_STOCK_POLICY%' OR ISNULL(CONDITION_STRUCT,'') LIKE '%DEPOT_STOCK_POLICY%'
                UNION ALL
                SELECT N'动作公式行 ' + CONVERT(nvarchar(10), a.M_IDX) FROM dbo.MODULE_BUSINESS_ACTION_OP o
                 JOIN dbo.MODULE_BUSINESS_ACTION a ON a.ACTION_ID = o.ACTION_ID
                 WHERE ISNULL(o.SOURCE_TABLE,'') LIKE '%DEPOT_STOCK_POLICY%' OR ISNULL(o.TARGET_TABLE,'') LIKE '%DEPOT_STOCK_POLICY%'
                UNION ALL
                SELECT N'已发布快照 ' + CONVERT(nvarchar(10), M_IDX) FROM dbo.WORKBENCH_DEFINITION_SNAPSHOT
                 WHERE IS_CURRENT = 1 AND DEFINITION_JSON LIKE '%DEPOT_STOCK_POLICY%'
            ) y;
            """);
        Assert.True(hits.Count == 0,
            $"策略表被声明进配置面（会产生第二个求值口径）：{string.Join("、", hits)}");
    }

    [Fact]
    public async Task 库别级字段同一键内不得取值分裂()
    {
        await using var connection = await OpenAsync();

        // 库别级字段（INIT_QTY / COST_PRICE / COST_AMOUNT）每行冗余同一库别值：同一 (料号, 库别) 下
        // 出现多个取值，说明写入没有按全行同步。
        var drift = await RowsAsync(connection, $"""
            SET NOCOUNT ON;
            SELECT CONCAT(PRO_NO, '|', DEPOT_ID, '|', COUNT(DISTINCT ISNULL(INIT_QTY,-1)), '|',
                          COUNT(DISTINCT ISNULL(COST_PRICE,-1)), '|', COUNT(DISTINCT ISNULL(COST_AMOUNT,-1)))
              FROM dbo.INV_PRO_DEPOT
             WHERE {NotFixture}
             GROUP BY PRO_NO, DEPOT_ID
            HAVING COUNT(DISTINCT ISNULL(COST_PRICE,-1)) > 1
                OR COUNT(DISTINCT ISNULL(INIT_QTY,-1)) > 1
                OR COUNT(DISTINCT ISNULL(COST_AMOUNT,-1)) > 1;
            """);
        Assert.True(drift.Count == 0,
            $"库别级字段在同一键内取值分裂（写入未按全行同步）：{string.Join("；", drift.Take(10))}");
    }

    [Fact]
    public async Task 策略档位取值必须已实现()
    {
        await using var connection = await OpenAsync();

        var policyRows = await ScalarAsync<int>(connection, "SELECT COUNT(*) FROM dbo.DEPOT_STOCK_POLICY;");
        Assert.True(policyRows > 0, "策略表为空——这条断言会恒真");

        var bad = await RowsAsync(connection, """
            SET NOCOUNT ON;
            SELECT CONCAT(DEPOT_ID, '|BATCH_MODE=', CONVERT(nvarchar(10), BATCH_MODE), '|CAPACITY_MODE=', CONVERT(nvarchar(10), CAPACITY_MODE))
              FROM dbo.DEPOT_STOCK_POLICY WHERE BATCH_MODE = 3 OR CAPACITY_MODE > 0;
            """);
        Assert.True(bad.Count == 0,
            $"策略表存在未实现档位的取值（只应来自直连改库或数据导入）：{string.Join("；", bad)}");
    }

    [Fact]
    public async Task 位置号不得带尾随空格()
    {
        await using var connection = await OpenAsync();

        // 变长列用 LIKE 判定（= 比较忽略尾随空格，判定无效）；定长 NCHAR 由存储引擎统一填充，无需该规则。
        var trailing = await RowsAsync(connection, $"""
            SET NOCOUNT ON;
            SELECT CONCAT(PRO_NO, '|', DEPOT_ID) FROM dbo.INV_PRO_DEPOT
             WHERE LOCATION_NO LIKE N'% ' AND {NotFixture};
            """);
        Assert.True(trailing.Count == 0,
            $"位置号带尾随空格（LIKE / 拼接比较会与去空格值不等）：{string.Join("、", trailing.Take(10))}");
    }

    [Fact]
    public async Task 半成品账的键必须齐全且不与主账重叠()
    {
        await using var connection = await OpenAsync();

        // HALF_PRO_DEPOT 按 (料号, 制程, 库别) 分账：制程落空或带尾随空格就等于这本账没了分账依据。
        var badKey = await RowsAsync(connection, """
            SET NOCOUNT ON;
            SELECT CONCAT(PRO_NO, '|', ISNULL(PROCEDURE_TYPE_ID,'<null>'), '|', DEPOT_ID) FROM dbo.HALF_PRO_DEPOT
             WHERE LTRIM(RTRIM(PRO_NO)) = N'' OR LTRIM(RTRIM(PROCEDURE_TYPE_ID)) = N'' OR LTRIM(RTRIM(DEPOT_ID)) = N''
                OR PROCEDURE_TYPE_ID <> LTRIM(RTRIM(PROCEDURE_TYPE_ID));
            """);
        Assert.True(badKey.Count == 0,
            $"半成品账存在不全的键（料号 / 制程 / 库别 落空或带尾随空格）：{string.Join("、", badKey.Take(10))}");

        // 与主账同键重叠 = 同一批货被记进两本账。
        var overlap = await RowsAsync(connection, """
            SET NOCOUNT ON;
            SELECT TOP 20 CONCAT(h.PRO_NO, '|', h.DEPOT_ID) FROM dbo.HALF_PRO_DEPOT h
             JOIN dbo.INV_PRO_DEPOT b
               ON LTRIM(RTRIM(b.PRO_NO)) = LTRIM(RTRIM(h.PRO_NO))
              AND LTRIM(RTRIM(b.DEPOT_ID)) = LTRIM(RTRIM(h.DEPOT_ID));
            """);
        Assert.True(overlap.Count == 0,
            $"半成品账与主账同键重叠（同一批货记进两本账）：{string.Join("、", overlap)}");
    }

    // ===== 装配 =====

    private static async Task<SqlConnection> OpenAsync()
    {
        var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    private static async Task<T> ScalarAsync<T>(SqlConnection connection, string sql)
    {
        await using var command = new SqlCommand(sql, connection);
        var scalar = await command.ExecuteScalarAsync();
        Assert.NotNull(scalar);
        return (T)Convert.ChangeType(scalar!, typeof(T));
    }

    private static async Task<List<string>> RowsAsync(SqlConnection connection, string sql)
    {
        var rows = new List<string>();
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(reader.IsDBNull(0) ? string.Empty : reader.GetString(0));
        }
        return rows;
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "EOS.API", "EOS.API.csproj")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Repository root not found (EOS.API/EOS.API.csproj).");
    }
}
