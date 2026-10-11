using Microsoft.Data.SqlClient;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 库存能力台帐的 **S2 / S3** 两段（原由脚本门禁 `check-capability-ledger.ps1` 守着，该门禁已退役，
/// 见 `scripts/README.md` 退役清单；它的 S1「能力档位」已由离线用例
/// <see cref="DepotStockPolicyCapabilityLedgerTests"/> 承接）。
///
/// · **S2｜库存域库对象**：声明"已生效"的库对象必须指出消费方。与 S1 的差别只有一处，但很关键：
///   消费方**可能在配置面**（通用表单的目标表 `MODULES.MASTER_TABLE` / 效果链动作行
///   `MODULE_BUSINESS_ACTION_OP`），此时 C# 里一个字都没有。所以命中数是"代码侧 + 配置侧"之和——
///   只给一侧会让结论反转（实测：`INV_LOAN_M` 代码侧 0、配置侧 1）。
///   配置侧刻意**不计入** `FIELDS` 行登记与已发布快照（那是登记/展示，不是消费，否则
///   "有元数据、没入口"的表会被误判成活体），也不做基于 LIKE 的 `PARAM_STRUCT` 子串匹配
///   （SQL 没有正则，`%EFFECT_DATE%` 会把 `IN_EFFECT_DATE` 算成命中）。
///
/// · **S3｜配置面库存写入旁路（棘轮，只减不增）**：效果链写在配置面，一个效果动作就能直接写库存四表，
///   而代码侧一个字都不变。故这一段不看代码，只看配置里到底有哪些写入点；出现未登记的新行即失败。
///
/// 无连接串即失败（**无连接跳过 ≠ 已验证**）。
/// </summary>
[Trait("Category", "Integration")]
[Collection("live-database")]
public sealed class InventoryCapabilityLedgerLiveTests
{
    /// <summary>库存四表：直接写它们即绕过库存移动引擎。</summary>
    private const string InventoryTables = "'INV_PRO_DEPOT','INV_DEPOT_LOG','INV_BATCH_M','INV_BATCH_D'";

    /// <summary>正路的写入者：效果键 `inventory-move` 不算旁路。</summary>
    private const string LegitEffectKey = "inventory-move";

    /// <summary>
    /// S3 登在册的旁路（棘轮：**只减不增**）。现仅一条，作为"配置面能绕过引擎"的对照项保留——
    /// 它只写"最近盘点日"标记列。
    /// </summary>
    private static readonly HashSet<string> RegisteredInventoryWrites = new(StringComparer.Ordinal)
    {
        "stamp-last-activity|130101|APPROVE_EFFECT|INV_PRO_DEPOT|LAST_CHECK_DATE",
    };

    /// <summary>
    /// S2 台帐（人工分类；判定消费它）。`Kind`：`Consumer` 声明已生效（两侧之和须 &gt;= Min）；
    /// `KnownGap` 已登记缺口（须写 Reason；两侧之和不为 0 时说明缺口可能已补）。
    /// </summary>
    private static readonly Dictionary<string, ObjectExpectation> ObjectLedger = new(StringComparer.Ordinal)
    {
        ["INV_DEPOT_LOG.LOCATION_PATH"] = new("Consumer", "LOCATION_PATH", "LOCATION_PATH", 1,
            "位置路径快照：移动引擎写入、盘点与查询读取"),
        ["INV_PRO_DEPOT.LAST_CHECK_DATE"] = new("Consumer", "LAST_CHECK_DATE", "LAST_CHECK_DATE", 1,
            "最近盘点日：由 130101 APPROVE_EFFECT / stamp-last-activity 写入（配置面消费方），查询侧经 InventoryQueryService 读取"),
        ["INV_PRO_DEPOT.USEABLE_QTY"] = new("Consumer", "USEABLE_QTY", "USEABLE_QTY", 1,
            "可用量：口径唯一出口是 InventoryAvailabilityService（SyncSlotsAsync 是唯一落列点），出库校验与读取侧三处同源"),
        ["INV_BATCH_M.EFFECT_DATE"] = new("Consumer", "EFFECT_DATE", "EFFECT_DATE", 1,
            "有效日期：由批次主档受控写入口写入（master-field-write）——它在字段白名单内，且服务端拒写引擎维护列"),
        ["INV_BATCH_M.AGAIN_CHECK_DATE"] = new("Consumer", "AGAIN_CHECK_DATE", "AGAIN_CHECK_DATE", 1,
            "复检日期：同 EFFECT_DATE，由受控写入口写入"),
        ["DEPOT_PRODUCT_LOCATION"] = new("Consumer", "DEPOT_PRODUCT_LOCATION", "DEPOT_PRODUCT_LOCATION", 1,
            "物料默认货位：代码侧 DepotLocationService 读主货位（FIXED 存放方式据此落位），配置侧模块 110311 以它为主表"),
        ["INV_LOAN_M"] = new("Consumer", "INV_LOAN_M", "INV_LOAN_M", 1,
            "借出单主表：**配置面驱动的对照项**（代码侧 0、配置侧 1，模块 130108），用于自证「两侧求和」这条口径有效"),
    };

    private static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("MSSQL_ERP_CONN")
        ?? throw new InvalidOperationException("真库集成测试需要 MSSQL_ERP_CONN；未配置即失败（无连接跳过≠已验证）。");

    [Fact]
    public void 库存域库对象必须指出消费方_代码侧或配置侧()
    {
        var apiRoot = ApiSourceProbe.ApiRoot();
        var failures = new List<string>();

        foreach (var (name, expectation) in ObjectLedger.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var code = ApiSourceProbe.CountConsumerHits(apiRoot, expectation.CodePattern);
            Assert.True(code >= 0, $"库对象「{name}」的代码侧探针未执行（源码目录缺失？）");

            var config = CountConfigHits(expectation.ConfigObject);
            Assert.True(config >= 0, $"库对象「{name}」的配置侧探针未执行（连库失败）——不能在看不到配置的情况下宣称通过");

            var total = code + config;
            var detail = $"代码侧 {code} + 配置侧 {config}";

            if (expectation.Kind == "Consumer" && total < expectation.Min)
            {
                failures.Add($"库对象「{name}」声明为已生效，但两侧探针合计仅命中 {total} 次（{detail}，要求 >= {expectation.Min}）——声称与实现不符");
            }
        }

        Assert.True(failures.Count == 0, string.Join("；", failures));
    }

    [Fact]
    public void 两侧求和口径_对照项必须靠配置侧撑着()
    {
        // 判别性对照：`INV_LOAN_M` 是"代码侧一个字都没有、只有配置面在用"的对照项。
        // 它的不变式今天**只由配置侧那一条命中撑着**——若有人摘掉配置侧探针，本用例立刻变红，
        // 而不是等到某天发现一整族配置驱动的库存单据表被误判成空壳。
        var code = ApiSourceProbe.CountConsumerHits(ApiSourceProbe.ApiRoot(), ObjectLedger["INV_LOAN_M"].CodePattern);
        Assert.True(code >= 0, "代码侧探针未执行（源码目录缺失？）");

        var config = CountConfigHits(ObjectLedger["INV_LOAN_M"].ConfigObject);
        Assert.True(config >= 0, "配置侧探针未执行（连库失败）");

        Assert.True(config >= 1,
            $"对照项 INV_LOAN_M 的配置侧命中数为 {config}——配置面探针可能失效（口径：MODULES 的主/明细表 + 效果链动作行的源/目标表与目标字段）。" +
            "若它真的已无配置面消费方，请换一个仍是单侧的对照项，而不是直接删掉这条对照。");

        Assert.True(code == 0,
            $"对照项 INV_LOAN_M 的代码侧命中数变成了 {code}（原为 0）。这不一定是坏事——说明它从纯配置驱动变成了代码也读。" +
            "但「两侧求和」这条口径的对照价值随之消失，请把对照项换成另一个仍是单侧的条目（并同步本文件的 Reason）。");
    }

    [Fact]
    public void 配置面库存写入旁路只减不增()
    {
        var actual = ListConfigInventoryWrites();
        // 连库失败返回 null（不能静默放行）；**空集合是合法状态**——旁路被撤掉时应由下面的
        // "陈旧登记"那条报出来，而不是在这里被当成探针故障。
        Assert.NotNull(actual);

        var failures = new List<string>();
        foreach (var row in actual)
        {
            if (!RegisteredInventoryWrites.Contains(row))
            {
                failures.Add($"配置面出现未登记的库存写入旁路：{row}——效果动作直接写余额/流水/批次账，绕过了库存移动引擎；请先确认安全性再登记，或改为走 {LegitEffectKey}");
            }
        }

        // 登记在册而已不存在的条目：棘轮只减不增，发现即清理（一行的事，与 NolockAllowlist 的陈旧条目同口径）。
        var stale = RegisteredInventoryWrites.Where(registered => !actual.Contains(registered)).ToList();
        foreach (var row in stale)
        {
            failures.Add($"登记在册的库存写入旁路已不存在：{row}——可从本用例的清单移除（棘轮只减不增）");
        }

        Assert.True(failures.Count == 0, string.Join("；", failures));
    }

    // ===== 探针 =====

    /// <summary>
    /// 配置侧消费方探针：对象名是否出现在**读写路径**里（通用表单的主/明细表、效果链动作行的源/目标表与目标字段）。
    /// 连不上库返回 -1，由调用方报为失败（不静默放行）。
    /// </summary>
    private static int CountConfigHits(string objectName)
    {
        const string sql = """
            SELECT COUNT(*) FROM (
                SELECT 1 AS X FROM dbo.MODULES WHERE MASTER_TABLE = @n OR DETAIL_TABLE = @n
                UNION ALL
                SELECT 1 FROM dbo.MODULE_BUSINESS_ACTION_OP
                    WHERE SOURCE_TABLE = @n OR TARGET_TABLE = @n OR TARGET_FIELD = @n
            ) z;
            """;
        try
        {
            using var connection = new SqlConnection(ConnectionString);
            connection.Open();
            using var command = new SqlCommand(sql, connection);
            command.Parameters.AddWithValue("@n", objectName);
            return Convert.ToInt32(command.ExecuteScalar());
        }
        catch (SqlException)
        {
            return -1;
        }
    }

    /// <summary>列出配置面里所有直接写库存四表的动作行（排除正路 <see cref="LegitEffectKey"/>）；连不上库返回 null。</summary>
    private static List<string>? ListConfigInventoryWrites()
    {
        var sql = $"""
            SELECT CONCAT(a.EFFECT_KEY, N'|', a.M_IDX, N'|', a.EVENT_CODE, N'|',
                          ISNULL(o.TARGET_TABLE, N'-'), N'|', ISNULL(o.TARGET_FIELD, N'-'))
            FROM dbo.MODULE_BUSINESS_ACTION_OP o
            JOIN dbo.MODULE_BUSINESS_ACTION a ON a.ACTION_ID = o.ACTION_ID
            WHERE o.TARGET_TABLE IN ({InventoryTables})
              AND a.EFFECT_KEY <> N'{LegitEffectKey}'
            ORDER BY 1;
            """;
        try
        {
            var rows = new List<string>();
            using var connection = new SqlConnection(ConnectionString);
            connection.Open();
            using var command = new SqlCommand(sql, connection);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var value = reader.IsDBNull(0) ? null : reader.GetString(0);
                if (!string.IsNullOrWhiteSpace(value)) { rows.Add(value.Trim()); }
            }
            return rows;
        }
        catch (SqlException)
        {
            return null;
        }
    }

    private sealed record ObjectExpectation(string Kind, string CodePattern, string ConfigObject, int Min, string Reason);
}
