using EOS.API.Data.Effects;
using EOS.API.Models;
using Microsoft.Data.SqlClient;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 效果参数物理引用的**发布门**校验（<see cref="EffectParamPhysicalGate"/>）在真库上的两条断言：
///
/// ① **库内全量**：现有每个模块的每条效果动作，其参数点名的表/列都真实存在、且能被运行期
///    同一个解析器解析。这条既是常态不变量，也是发布门新校验的**影响面实测**——它若不通过，
///    说明库内确实存在"发布放过、运行期才炸"的配置（2708 就是这样躺了很久）。
/// ② **判别力自检**：人为构造一条引用不存在目标表的动作，校验必须报出来；否则①的通过没有意义。
///
/// 真库用例，需 <c>EOS_ERP_TEST_CONNECTION</c>；只读，不改任何数据。
/// </summary>
[Trait("Category", "live-database")]
[Collection("live-database")]
public sealed class EffectParamPhysicalGateLiveTests
{
    private static string RequireConnection()
    {
        var value = Environment.GetEnvironmentVariable("EOS_ERP_TEST_CONNECTION");
        Assert.False(string.IsNullOrWhiteSpace(value),
            "真库集成测试需要 EOS_ERP_TEST_CONNECTION；未配置即失败（无连接跳过≠已验证）。");
        return value!;
    }

    private sealed record ActionRow(int ModuleId, string? MasterTable, string? DetailTable, BusinessActionDto Action);

    private static async Task<List<ActionRow>> ReadActionsAsync(SqlConnection connection, CancellationToken token)
    {
        const string sql = """
            SELECT a.M_IDX, LTRIM(RTRIM(ISNULL(m.MASTER_TABLE,''))), LTRIM(RTRIM(ISNULL(m.DETAIL_TABLE,''))),
                   a.SEQ, LTRIM(RTRIM(ISNULL(a.EVENT_CODE,''))), LTRIM(RTRIM(ISNULL(a.EFFECT_KEY,''))),
                   LTRIM(RTRIM(ISNULL(a.PARAM_STRUCT,'')))
            FROM dbo.MODULE_BUSINESS_ACTION a
            JOIN dbo.MODULES m ON m.M_IDX = a.M_IDX
            ORDER BY a.M_IDX, a.EVENT_CODE, a.SEQ;
            """;
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(token);
        var rows = new List<ActionRow>();
        while (await reader.ReadAsync(token))
        {
            var action = new BusinessActionDto(
                reader.GetInt32(3),
                reader.GetString(4),
                reader.GetString(5),
                Params: reader.GetString(6));
            rows.Add(new ActionRow(
                reader.GetInt32(0),
                reader.GetString(1) is { Length: > 0 } master ? master : null,
                reader.GetString(2) is { Length: > 0 } detail ? detail : null,
                action));
        }
        return rows;
    }

    [Fact]
    public async Task 库内所有模块的效果参数物理引用均存在()
    {
        var connectionString = RequireConnection();
        var token = CancellationToken.None;
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(token);

        var rows = await ReadActionsAsync(connection, token);
        Assert.NotEmpty(rows);

        var issues = new List<string>();
        var modules = 0;
        foreach (var group in rows.GroupBy(row => (row.ModuleId, row.MasterTable, row.DetailTable)))
        {
            modules++;
            var found = await EffectParamPhysicalGate.RunAsync(
                connection, group.Key.ModuleId, group.Key.MasterTable, group.Key.DetailTable,
                group.Select(row => row.Action).ToList(), token);
            issues.AddRange(found.Select(issue => $"模块 {group.Key.ModuleId}：{issue}"));
        }

        Assert.True(issues.Count == 0,
            $"共 {modules} 个模块、{rows.Count} 条效果动作，其中以下参数引用了不存在的表/列"
            + "（发布门会在发布该模块时报错，需先修正配置或退役对应对象）：\n"
            + string.Join("\n", issues));
    }

    [Fact]
    public async Task 引用不存在目标表的动作必须被报出_判别力自检()
    {
        var connectionString = RequireConnection();
        var token = CancellationToken.None;
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(token);

        var seed = (await ReadActionsAsync(connection, token)).FirstOrDefault();
        Assert.NotNull(seed);

        // set-state 的目标表来自参数，改成不存在的表名后必须报错。
        var bogus = new BusinessActionDto(
            1, "SAVE", "set-state", null, true, "BLOCK", null,
            """{"targets":["NO_SUCH_TABLE_ADR14"],"state":{"NO_SUCH_COLUMN":1}}""");

        var issues = await EffectParamPhysicalGate.RunAsync(
            connection, seed!.ModuleId, seed.MasterTable, seed.DetailTable, new[] { bogus }, token);

        Assert.NotEmpty(issues);
    }
}
