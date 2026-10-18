using System.Data;
using System.Text.Json;
using EOS.API.Data;
using EOS.API.Data.Effects;
using EOS.API.Models;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 校验规则口径核对：<c>MODULE_VALIDATION_RULE</c> 的每一行都必须被运行时真正接受并解释。
/// 诊断黄金集（每个模块校验规则天然是一个"应当能被诊断出来"的样本）建立在这批语料之上，
/// 若配置与执行不一致，黄金集会建在纸面规则上——本类就是这道地基。
///
/// 三条断言，全部读真库、走真实代码，不重写判定口径：
///   ① 配置表 ↔ 当前快照逐条一致（`(STAGE, SEQ, VALIDATION_KEY, ENABLED)` 集合双向相等）——
///      "配了但引擎读不到"的行必须为 0。**只对有当前快照的模块成立**：没有快照的模块
///      （定制承载页，`M_URL` 是精确路径、不参与发布）每请求实时装配，配置表就是运行期真值，
///      对它们改为断言"必须有单据面"——没有单据面的模块实时装配不出定义，规则一样读不到；
///   ② 每条规则喂进真实 <see cref="EffectPlanLoader"/>（其内部用 <c>ValidationRuleRegistry</c> 校验
///      键闭集 / 阶段闭集 / 参数结构）不抛错。快照模块喂快照段，无快照模块走真实
///      <see cref="WorkbenchDefinitionBuilder"/> 装配出的定义；
///   ③ 语料基线冻结（总条数 / SAVE 条数 / 覆盖模块数 / `键|阶段` 分布）——漂移必须显式暴露，
///      因为它同时改变黄金集的样本面。
///
/// 需要 MSSQL_ERP_CONN（与本仓库其它真库用例一致）。
/// </summary>
[Trait("Category", "Integration")]
[Collection("live-database")]
public sealed class ValidationRuleParityLiveTests
{
    private static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("MSSQL_ERP_CONN")
        ?? throw new InvalidOperationException(
            "真库集成测试需要 MSSQL_ERP_CONN；未配置即失败（无连接跳过≠已验证）。");

    /// <summary>配置事实：`(M_IDX, STAGE, SEQ, VALIDATION_KEY, ENABLED)`。</summary>
    private sealed record RuleKey(int ModuleId, string Stage, int Seq, string Key, bool Enabled)
    {
        public override string ToString() => $"{ModuleId}/{Stage}/{Seq}/{Key}/{(Enabled ? 1 : 0)}";
    }

    /// <summary>模块形态：判断它有没有单据面（能不能装配出定义）。</summary>
    private sealed record ModuleShape(string MasterTable, string NodeKind, bool FormEnabled)
    {
        public bool HasDocumentSurface =>
            MasterTable.Length > 0
            && (NodeKind.Equals("WORKBENCH", StringComparison.OrdinalIgnoreCase) || FormEnabled);
    }

    private static async Task<(List<RuleKey> Rules, Dictionary<int, ModuleShape> Modules)> ReadTableAsync(
        SqlConnection connection, CancellationToken token)
    {
        var rules = new List<RuleKey>();
        await using (var command = new SqlCommand("""
            SELECT M_IDX, STAGE, SEQ, VALIDATION_KEY, ENABLED
            FROM dbo.MODULE_VALIDATION_RULE WITH (NOLOCK)
            ORDER BY M_IDX, STAGE, SEQ;
            """, connection))
        await using (var reader = await command.ExecuteReaderAsync(token))
        {
            while (await reader.ReadAsync(token))
            {
                rules.Add(new RuleKey(
                    reader.GetInt32(0),
                    reader.GetString(1).Trim(),
                    reader.GetInt32(2),
                    reader.GetString(3).Trim(),
                    reader.GetBoolean(4)));
            }
        }

        var modules = new Dictionary<int, ModuleShape>();
        await using (var command = new SqlCommand("""
            SELECT m.M_IDX, ISNULL(m.MASTER_TABLE,''), ISNULL(n.NODE_KIND,''), ISNULL(m.UNIFIED_FORM_ENABLED,0)
            FROM dbo.MODULES m WITH (NOLOCK)
            LEFT JOIN dbo.V_MODULE_NODE n WITH (NOLOCK) ON n.M_IDX=m.M_IDX;
            """, connection))
        await using (var reader = await command.ExecuteReaderAsync(token))
        {
            while (await reader.ReadAsync(token))
            {
                modules[reader.GetInt32(0)] = new ModuleShape(
                    reader.GetString(1).Trim(), reader.GetString(2).Trim(), reader.GetBoolean(3));
            }
        }

        return (rules, modules);
    }

    /// <summary>运行时输入：每个模块当前快照的 `validationRules` 段（引擎真正读到的就是它）。</summary>
    private static async Task<Dictionary<int, JsonElement>> ReadSnapshotRulesAsync(
        SqlConnection connection, CancellationToken token)
    {
        var snapshots = new Dictionary<int, JsonElement>();
        await using var command = new SqlCommand("""
            SELECT M_IDX, DEFINITION_JSON
            FROM dbo.WORKBENCH_DEFINITION_SNAPSHOT WITH (NOLOCK)
            WHERE IS_CURRENT=1;
            """, connection);
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            var moduleId = reader.GetInt32(0);
            using var document = JsonDocument.Parse(reader.GetString(1));
            if (document.RootElement.TryGetProperty("validationRules", out var rules)
                && rules.ValueKind == JsonValueKind.Array)
            {
                snapshots[moduleId] = rules.Clone();
            }
        }

        return snapshots;
    }

    private static List<RuleKey> SnapshotKeys(int moduleId, JsonElement rules)
    {
        var keys = new List<RuleKey>();
        foreach (var rule in rules.EnumerateArray())
        {
            // 缺失的 stage/validationKey/seq 由运行时按"必填缺失"抛错，这里如实保留原值以便报出定位。
            keys.Add(new RuleKey(
                moduleId,
                rule.TryGetProperty("stage", out var stage) && stage.ValueKind == JsonValueKind.String
                    ? stage.GetString()!.Trim()
                    : "<missing>",
                rule.TryGetProperty("seq", out var seq) && seq.ValueKind == JsonValueKind.Number
                    ? seq.GetInt32()
                    : -1,
                rule.TryGetProperty("validationKey", out var key) && key.ValueKind == JsonValueKind.String
                    ? key.GetString()!.Trim()
                    : "<missing>",
                !rule.TryGetProperty("enabled", out var enabled) || enabled.ValueKind != JsonValueKind.False));
        }

        return keys;
    }

    private static DbConnectionFactory CreateConnections() =>
        new(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:ErpDatabase"] = ConnectionString,
            })
            .Build());

    private static WorkbenchDefinitionBuilder CreateBuilder(DbConnectionFactory connections)
    {
        var formCapability = LiveFormCapability.FromEnvironment();
        return new WorkbenchDefinitionBuilder(
            connections,
            new WorkbenchDefinitionProvider(connections, NullLogger<WorkbenchDefinitionProvider>.Instance),
            formCapability,
            TestBusinessConfig.Reader(connections),
            NullLogger<WorkbenchDefinitionBuilder>.Instance);
    }

    [Fact]
    public async Task 配置表与当前快照逐条一致_不存在配了却读不到的规则()
    {
        var token = CancellationToken.None;
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(token);

        var (rules, modules) = await ReadTableAsync(connection, token);
        var snapshots = await ReadSnapshotRulesAsync(connection, token);

        var snapshotModules = snapshots.Keys.ToHashSet();
        var table = rules.ToHashSet();
        var snapshot = new HashSet<RuleKey>();
        foreach (var (moduleId, element) in snapshots)
            foreach (var key in SnapshotKeys(moduleId, element))
                snapshot.Add(key);

        // 有快照的模块按快照执行：配置表里的每一行都必须出现在快照里。
        var missingInSnapshot = table
            .Where(rule => snapshotModules.Contains(rule.ModuleId) && !snapshot.Contains(rule))
            .ToList();
        Assert.True(missingInSnapshot.Count == 0,
            $"以下规则已配置但不在当前快照里（引擎不会执行）：{string.Join("; ", missingInSnapshot.Take(20))}");

        var missingInTable = snapshot.Except(table).ToList();
        Assert.True(missingInTable.Count == 0,
            $"以下快照规则在配置表中已不存在（快照落后于配置）：{string.Join("; ", missingInTable.Take(20))}");

        // 没有快照的模块走实时装配：装配不出来就没有任何运行时读者，配置同样等于纸面规则。
        var liveModules = rules.Select(rule => rule.ModuleId).Distinct().Where(id => !snapshotModules.Contains(id));
        var surfaceLess = liveModules
            .Where(id => !modules.TryGetValue(id, out var shape) || !shape.HasDocumentSurface)
            .ToList();
        Assert.True(surfaceLess.Count == 0,
            "以下模块没有当前快照、也装配不出单据面，配置的校验规则不会被任何运行时读者读取："
            + string.Join("; ", surfaceLess.Select(id =>
                modules.TryGetValue(id, out var shape) ? $"module={id} kind={shape.NodeKind} master='{shape.MasterTable}' form={shape.FormEnabled}" : $"module={id} 不存在")));
    }

    [Fact]
    public async Task 每条规则都被运行时口径接受()
    {
        var token = CancellationToken.None;
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(token);

        var (rules, modules) = await ReadTableAsync(connection, token);
        var snapshots = await ReadSnapshotRulesAsync(connection, token);
        var loader = new EffectPlanLoader();
        var failures = new List<string>();
        var accepted = 0;

        foreach (var (moduleId, element) in snapshots.OrderBy(item => item.Key))
        {
            var expected = rules.Count(rule => rule.ModuleId == moduleId);
            // 只喂 validationRules 段：本次核对的正是校验规则，动作链的配置问题由门禁另有覆盖。
            var definition = new WorkbenchDefinition(
                moduleId,
                $"module-{moduleId}",
                modules.TryGetValue(moduleId, out var shape) ? shape.MasterTable : string.Empty,
                null,
                Array.Empty<WorkbenchField>(),
                Array.Empty<WorkbenchField>(),
                null,
                false,
                false,
                false,
                Array.Empty<string>(),
                string.Empty,
                false,
                ValidationRules: element);
            try
            {
                var plan = loader.Load(definition);
                accepted += plan.Rules.Count;
                if (plan.Rules.Count != expected)
                {
                    failures.Add($"module={moduleId} 引擎解析 {plan.Rules.Count} 条 ≠ 配置 {expected} 条");
                }
            }
            catch (Exception exception)
            {
                failures.Add($"module={moduleId} 运行时拒绝该模块校验规则：{exception.Message}");
            }
        }

        // 无快照模块：喂**真实装配**出的定义（不是另拼一份 JSON），它才是运行时读到的东西。
        var liveModules = rules.Select(rule => rule.ModuleId).Distinct()
            .Where(id => !snapshots.ContainsKey(id)).OrderBy(id => id).ToList();
        if (liveModules.Count > 0)
        {
            var builder = CreateBuilder(CreateConnections());
            var denied = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var moduleId in liveModules)
            {
                var expected = rules.Count(rule => rule.ModuleId == moduleId);
                try
                {
                    var definition = await builder.GetDefinitionAsync(
                        moduleId, "validation-rule-parity", "Z", true, true, denied, denied, token);
                    if (definition is null)
                    {
                        failures.Add($"module={moduleId} 没有单据面，实时装配不出定义（规则不会被读取）");
                        continue;
                    }
                    var plan = loader.Load(definition);
                    accepted += plan.Rules.Count;
                    if (plan.Rules.Count != expected)
                    {
                        failures.Add($"module={moduleId} 实时定义的规则数 {plan.Rules.Count} ≠ 配置 {expected} 条");
                    }
                }
                catch (Exception exception)
                {
                    failures.Add($"module={moduleId} 运行时拒绝该模块校验规则：{exception.Message}");
                }
            }
        }

        Assert.True(failures.Count == 0, string.Join("；", failures.Take(10)));
        Assert.Equal(rules.Count, accepted);
    }

    [Fact]
    public async Task 语料基线与登记值一致()
    {
        var token = CancellationToken.None;
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(token);

        var (rules, _) = await ReadTableAsync(connection, token);
        var distribution = rules
            .GroupBy(rule => $"{rule.Key}|{rule.Stage}")
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => $"{group.Key}={group.Count()}")
            .ToList();

        // 冻结的是"黄金集建在哪批语料上"这一事实，不是校验规则本身：语料变化即样本面变化，
        // 必须重走口径核对（改这里等于重新登记黄金集地基）。
        // 2026-10-07（迁移 331）：给月结单（1304）挂了一条 APPROVE 阶段的 custom-validation
        // ——"该期快照明细为空不许关账"。于是总数 187→188、有规则的模块数 96→97、分布多一项。
        // 2026-10-18（迁移 408 / 416）：产品 BOM（1204）补损耗率区间校验（line-require seq=5）
        // 与子件版本校验（require-when-referenced seq=6）。于是总数 188→190、SAVE 167→169，
        // 分布多一项；有规则的模块数不变（1204 本来就在）。
        Assert.Equal(190, rules.Count);
        Assert.Equal(169, rules.Count(rule => rule.Stage == "SAVE"));
        Assert.Equal(97, rules.Select(rule => rule.ModuleId).Distinct().Count());
        Assert.Equal(
            new[]
            {
                "custom-validation|APPROVE=1",
                "custom-validation|DELETE=1",
                "custom-validation|SAVE=6",
                "duplicate-check|SAVE=19",
                "line-require|APPROVE=2",
                "line-require|SAVE=39",
                "no-cycle|SAVE=1",
                "period-overlap|SAVE=3",
                "qty-not-exceed|APPROVE=8",
                "qty-not-exceed|DEAPPROVE=9",
                "qty-not-exceed|SAVE=32",
                "reference-exists|SAVE=68",
                "require-when-referenced|SAVE=1",
            },
            distribution);
    }
}
