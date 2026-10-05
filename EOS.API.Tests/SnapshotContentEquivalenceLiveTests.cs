using System.Text.Json.Nodes;
using EOS.API.Data.Definitions;
using Microsoft.Data.SqlClient;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 定义内容比较器在**已发布快照真数据**上的判据：取库内真实定义，按"整理配置"的方式改动它，
/// 断言该变的判等价、不该变的判不等价。
///
/// 为什么必须用真数据：合成样本只能证明"我按我理解的规则写了代码"，证明不了"库里的配置长这样、
/// 改动确实落在我以为的那条规则上"。真库里空串与 NULL 两种写法同时大量存在（结构列合计上千格），
/// 而 CONSTANT 来源的空串是**真实取值**（清空该列，实测 25 行）——不拿真数据测，
/// 最容易被漏掉的恰恰是这条例外。
///
/// 需要 MSSQL_ERP_CONN。
/// </summary>
[Trait("Category", "Integration")]
[Collection("live-database")]
public sealed class SnapshotContentEquivalenceLiveTests
{
    private static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("MSSQL_ERP_CONN")
        ?? throw new InvalidOperationException("真库集成测试需要 MSSQL_ERP_CONN；未配置即失败（无连接跳过≠已验证）。");

    private sealed record Sample(int ModuleId, int Version, string DefinitionJson);

    /// <summary>取含指定文本的当前快照（按模块号升序，最多若干份）。</summary>
    private static async Task<IReadOnlyList<Sample>> ReadSnapshotsContainingAsync(
        string pattern, int limit, CancellationToken token)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(token);
        await using var command = new SqlCommand(
            $"""
            SELECT TOP ({limit}) s.M_IDX, s.VERSION, s.DEFINITION_JSON
            FROM dbo.WORKBENCH_DEFINITION_SNAPSHOT s WITH (NOLOCK)
            WHERE s.IS_CURRENT = 1 AND s.DEFINITION_JSON LIKE '%' + @pattern + '%'
            ORDER BY s.M_IDX;
            """, connection);
        command.Parameters.AddWithValue("@pattern", pattern);
        var samples = new List<Sample>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            samples.Add(new Sample(reader.GetInt32(0), reader.GetInt32(1), reader.GetString(2)));
        }
        Assert.True(samples.Count > 0, $"库里没有含 {pattern} 的当前快照，本用例失去取样对象。");
        return samples;
    }

    /// <summary>取一份含指定文本的当前快照（模块号最小者）。</summary>
    private static async Task<Sample> ReadSnapshotContainingAsync(string pattern, CancellationToken token) =>
        (await ReadSnapshotsContainingAsync(pattern, 1, token))[0];

    private static bool IsBlank(JsonObject target, string name) =>
        target[name] is not JsonValue value
        || value.GetValueKind() != System.Text.Json.JsonValueKind.String
        || string.IsNullOrWhiteSpace(value.GetValue<string>());

    /// <summary>独立实现（不调用被测比较器的内部逻辑）：删掉全空的占位公式行。</summary>
    private static string StripPlaceholderOps(string definitionJson)
    {
        var definition = JsonNode.Parse(definitionJson)!.AsObject();
        if (definition["businessActions"] is not JsonArray actions)
        {
            return definition.ToJsonString();
        }
        foreach (var element in actions)
        {
            if (element is not JsonObject action || action["ops"] is not JsonArray ops)
            {
                continue;
            }
            for (var index = ops.Count - 1; index >= 0; index--)
            {
                if (ops[index] is JsonObject op
                    && IsBlank(op, "opCode") && IsBlank(op, "targetTable") && IsBlank(op, "targetField"))
                {
                    ops.RemoveAt(index);
                }
            }
        }
        return definition.ToJsonString();
    }

    [Fact]
    public async Task 真数据_删掉全空占位公式行判等价()
    {
        var token = CancellationToken.None;
        var sample = await ReadSnapshotContainingAsync("\"opCode\":\"\"", token);

        var stripped = StripPlaceholderOps(sample.DefinitionJson);

        Assert.NotEqual(sample.DefinitionJson, stripped);
        Assert.True(WorkbenchDefinitionContentComparer.AreEquivalent(sample.DefinitionJson, stripped),
            $"模块 {sample.ModuleId} v{sample.Version}：删掉占位行后应判等价（否则清理仍会顶版）。");
    }

    [Fact]
    public async Task 真数据_把结构列的空串改成缺省判等价()
    {
        var token = CancellationToken.None;
        var sample = await ReadSnapshotContainingAsync("\"remark\":\"\"", token);

        var definition = JsonNode.Parse(sample.DefinitionJson)!.AsObject();
        var actions = definition["businessActions"]!.AsArray();
        var rewritten = 0;
        foreach (var element in actions)
        {
            if (element is not JsonObject action || action["ops"] is not JsonArray ops)
            {
                continue;
            }
            foreach (var opNode in ops)
            {
                if (opNode is not JsonObject op)
                {
                    continue;
                }
                foreach (var name in new[] { "remark", "condition", "match", "sourceTerms" })
                {
                    if (op[name] is JsonValue value
                        && value.GetValueKind() == System.Text.Json.JsonValueKind.String
                        && value.GetValue<string>().Length == 0)
                    {
                        op[name] = null;
                        rewritten++;
                    }
                }
            }
        }

        Assert.True(rewritten > 0, $"模块 {sample.ModuleId} v{sample.Version}：快照里没有可改写的空串字段。");
        var after = definition.ToJsonString();

        Assert.NotEqual(sample.DefinitionJson, after);
        Assert.True(WorkbenchDefinitionContentComparer.AreEquivalent(sample.DefinitionJson, after),
            $"模块 {sample.ModuleId} v{sample.Version}：把 {rewritten} 处空串改成缺省后应判等价。");
    }

    /// <summary>找第一条 CONSTANT 来源且常量为空串的公式行。</summary>
    private static JsonObject? FindConstantOpWithEmptyConstant(JsonObject definition)
    {
        if (definition["businessActions"] is not JsonArray actions)
        {
            return null;
        }
        foreach (var action in actions)
        {
            if (action is not JsonObject actionObject || actionObject["ops"] is not JsonArray ops)
            {
                continue;
            }
            foreach (var opNode in ops)
            {
                if (opNode is JsonObject op
                    && op["sourceScope"] is JsonValue scope
                    && scope.GetValueKind() == System.Text.Json.JsonValueKind.String
                    && string.Equals(scope.GetValue<string>(), "CONSTANT", StringComparison.OrdinalIgnoreCase)
                    && op["sourceConstant"] is JsonValue constant
                    && constant.GetValueKind() == System.Text.Json.JsonValueKind.String
                    && constant.GetValue<string>().Length == 0)
                {
                    return op;
                }
            }
        }
        return null;
    }

    [Fact]
    public async Task 真数据_CONSTANT来源的空常量改成缺省判不等价()
    {
        var token = CancellationToken.None;
        // "sourceScope" 与 "sourceConstant" 在同一个公式行对象内按固定键序出现，库侧先粗筛，
        // 再在内存里确认两者属于**同一条**公式行（粗筛可能跨行命中）。
        var candidates = await ReadSnapshotsContainingAsync(
            "%\"sourceScope\":\"CONSTANT\"%sourceConstant\":\"\"%", 50, token);

        foreach (var sample in candidates)
        {
            var definition = JsonNode.Parse(sample.DefinitionJson)!.AsObject();
            if (FindConstantOpWithEmptyConstant(definition) is not { } op)
            {
                continue;
            }
            // 只改这一处：空串（清空该列）改成缺省（运行期直接抛"缺少 sourceConstant"）
            op["sourceConstant"] = null;
            var after = definition.ToJsonString();

            Assert.False(WorkbenchDefinitionContentComparer.AreEquivalent(sample.DefinitionJson, after),
                $"模块 {sample.ModuleId} v{sample.Version}：CONSTANT 的空常量与缺省语义不同，不得判等价。");
            return;
        }

        Assert.Fail("库里没有 CONSTANT 来源且常量为空串的公式行，本用例失去取样对象。");
    }

    [Fact]
    public async Task 真数据_改掉目标列判不等价()
    {
        var token = CancellationToken.None;
        var sample = await ReadSnapshotContainingAsync("\"opCode\":\"ACCUM\"", token);

        var definition = JsonNode.Parse(sample.DefinitionJson)!.AsObject();
        var actions = definition["businessActions"]!.AsArray();
        var rewritten = false;
        foreach (var element in actions)
        {
            if (element is not JsonObject action || action["ops"] is not JsonArray ops)
            {
                continue;
            }
            foreach (var opNode in ops)
            {
                if (opNode is not JsonObject op || op["targetField"] is not JsonValue field)
                {
                    continue;
                }
                // 占位行不能当样本：给它改名会把它变成"非占位行"，两侧的差异就变成"占位行是否被剔除"
                // （R1 的效果）而不是目标列被改——那样的反例测不到目标列这条规则上。
                if (IsBlank(op, "opCode") && IsBlank(op, "targetTable") && IsBlank(op, "targetField"))
                {
                    continue;
                }
                op["targetField"] = field.GetValue<string>() + "_CHANGED";
                rewritten = true;
                break;
            }
            if (rewritten)
            {
                break;
            }
        }

        Assert.True(rewritten, $"模块 {sample.ModuleId} v{sample.Version}：快照里没有可改写的非占位公式行。");
        Assert.False(WorkbenchDefinitionContentComparer.AreEquivalent(sample.DefinitionJson, definition.ToJsonString()));
    }
}
