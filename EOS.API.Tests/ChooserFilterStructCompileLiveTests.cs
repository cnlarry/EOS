using System.Data;
using EOS.API.Data;
using Microsoft.Data.SqlClient;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 存量选择器过滤结构（`FIELD_DATASOURCE.FILTER_STRUCT`）必须**逐条可编译**。
///
/// 为什么值得一条真库用例：运行期是 **fail-closed**——结构解析不出或编译不过，
/// 选择器就退化成"没有过滤"，页面上只是候选变多，不会报错、也不会进日志错误码。
/// 也就是说一条坏结构可以长期存在而无人发现，直到有人比对候选项。
///
/// 判据取 <see cref="ChooserFilterValidator.ValidateAsync"/>（保存路径用的同一个校验器，
/// 含源表物理列、算子白名单、跨表引用 JOIN 目录、子查询表存在），与"保存即校验"同口径。
/// 空结构（`{"logic":"AND","items":[]}` 或 NULL）表示"无过滤"，是合法状态，不参与断言。
/// </summary>
[Trait("Category", "Integration")]
[Collection("live-database")]
public sealed class ChooserFilterStructCompileLiveTests
{
    private static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("MSSQL_ERP_CONN")
        ?? throw new InvalidOperationException(
            "真库集成测试需要 MSSQL_ERP_CONN；未配置即失败（无连接跳过≠已验证）。");

    [Fact]
    public async Task 存量过滤结构逐条可编译()
    {
        var token = CancellationToken.None;
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(token);

        var rows = new List<(int Id, string Table, string Field, string SourceTable, string Struct)>();
        await using (var command = new SqlCommand("""
            SELECT ID, T_ID, F_ID, SOURCE_T_ID, FILTER_STRUCT
            FROM dbo.FIELD_DATASOURCE WITH (NOLOCK)
            WHERE ISNULL(FILTER_STRUCT, '') <> ''
            ORDER BY ID;
            """, connection))
        await using (var reader = await command.ExecuteReaderAsync(token))
        {
            while (await reader.ReadAsync(token))
            {
                rows.Add((reader.GetInt32(0), reader.GetString(1).Trim(), reader.GetString(2).Trim(),
                    reader.GetString(3).Trim(), reader.GetString(4)));
            }
        }

        Assert.NotEmpty(rows);

        var unparsable = new List<string>();
        var uncompilable = new List<string>();
        var checkedCount = 0;
        foreach (var row in rows)
        {
            if (!ChooserFilterStruct.TryParse(row.Struct, out var filter) || filter is null)
            {
                unparsable.Add($"{row.Id} {row.Table}.{row.Field}（源 {row.SourceTable}）");
                continue;
            }
            // 空 items 是"无过滤"，合法
            if (filter.Items.Count == 0) continue;

            checkedCount++;
            var result = await ChooserFilterValidator.ValidateAsync(connection, filter, row.SourceTable, token);
            if (!result.Ok)
            {
                uncompilable.Add($"{row.Id} {row.Table}.{row.Field}（源 {row.SourceTable}）：" +
                    string.Join("；", result.Messages));
            }
        }

        Assert.True(unparsable.Count == 0,
            $"这些 FILTER_STRUCT 解析不出（运行期会 fail-closed 成无过滤）：\n  {string.Join("\n  ", unparsable)}");
        Assert.True(uncompilable.Count == 0,
            $"这些 FILTER_STRUCT 编译不过（运行期会 fail-closed 成无过滤）：\n  {string.Join("\n  ", uncompilable)}");
        Assert.True(checkedCount > 0, "没有一条非空过滤结构被校验到——判据形同虚设，先查 SQL。");
    }
}
