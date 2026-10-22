using System.Data;

using Microsoft.Data.SqlClient;

namespace EOS.API.Data.Workbench;

/// <summary>
/// 主表金额汇总的唯一实现。规则是**可发现**的，不需要逐模块配置：
/// 主表列 C 与明细同名列 C 之间，只要名称相同、数据类型相同、且两侧在 <c>FIELDS</c>
/// 都登记为金额角色（<c>NUMERIC_ROLE='AMOUNT'</c>），就认定「主表 C = SUM(明细 C)」。
///
/// 三条判据缺一不可：只认名字相同会让 <c>CURR_RATE</c> / <c>TAX_RATE</c> / <c>PRICE</c> / <c>QTY</c>
/// 这类同名列也被加总；只认金额角色又覆盖不到「主表叫别名、明细没有同名列」的写法。
///
/// 主表金额的权威是明细行：这里只做加法，不参与任何汇率折算，也不做取整
/// （显示精度归 ADR-037 的格式化层，落库值保持明细之和）。
///
/// 表名/列名只来自服务端元数据（<c>FIELDS</c> 与 <c>sys.columns</c>），并经标识符校验后加方括号；
/// 键值一律参数化。
/// </summary>
internal static class MasterAmountRollup
{
    /// <summary>
    /// 发现该主从表对上的汇总列。三项判据全在 SQL 里，与运行期执行共用同一处定义，
    /// 保证「发现得到的列」与「真正回写的列」不可能不一致。
    /// </summary>
    internal static async Task<IReadOnlyList<string>> DiscoverColumnsAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        string masterTable,
        string detailTable,
        CancellationToken token)
    {
        if (!WorkbenchSql.Identifier.IsMatch(masterTable) || !WorkbenchSql.Identifier.IsMatch(detailTable))
        {
            return [];
        }
        const string sql = """
            SELECT master.F_ID
            FROM dbo.FIELDS master
            JOIN dbo.FIELDS detail ON detail.T_ID = @detail AND detail.F_ID = master.F_ID
            WHERE master.T_ID = @master
              AND master.NUMERIC_ROLE = N'AMOUNT'
              AND detail.NUMERIC_ROLE = N'AMOUNT'
              AND COL_LENGTH('dbo.' + QUOTENAME(@master), master.F_ID) IS NOT NULL
              AND COL_LENGTH('dbo.' + QUOTENAME(@detail), master.F_ID) IS NOT NULL
              AND (SELECT TYPE_NAME(c.user_type_id) FROM sys.columns c
                    WHERE c.object_id = OBJECT_ID('dbo.' + QUOTENAME(@master)) AND c.name = master.F_ID)
                = (SELECT TYPE_NAME(c.user_type_id) FROM sys.columns c
                    WHERE c.object_id = OBJECT_ID('dbo.' + QUOTENAME(@detail)) AND c.name = master.F_ID)
            ORDER BY master.F_ID;
            """;
        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@master", SqlDbType.NVarChar, 128).Value = masterTable;
        command.Parameters.Add("@detail", SqlDbType.NVarChar, 128).Value = detailTable;
        var columns = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            var column = reader.GetString(0).Trim();
            // 元数据里的列名同样是外部输入：不过标识符校验就整列丢弃，绝不拼进 SQL。
            if (WorkbenchSql.Identifier.IsMatch(column)) columns.Add(column);
        }
        return columns;
    }

    /// <summary>
    /// 按发现结果回写主表金额。<paramref name="keyValues"/> 为空表示整表重算
    /// （保存路径的口径：一次分组更新覆盖所有单据）；非空只重算这一张单据
    /// （动作、批核/解批、效果链的收尾路径，避免每次点一个动作就扫全表）。
    /// </summary>
    /// <returns>实际执行的回写语句条数（每列一条）。没有可汇总的列时为 0。</returns>
    internal static async Task<int> ApplyAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        string masterTable,
        string detailTable,
        IReadOnlyList<string> masterPkOrder,
        IReadOnlyList<string>? keyValues,
        CancellationToken token)
    {
        if (masterPkOrder.Count < 2) return 0;
        var typeColumn = masterPkOrder[0];
        var noColumn = masterPkOrder[1];
        if (!WorkbenchSql.Identifier.IsMatch(typeColumn) || !WorkbenchSql.Identifier.IsMatch(noColumn)) return 0;
        var columns = await DiscoverColumnsAsync(connection, transaction, masterTable, detailTable, token);
        if (columns.Count == 0) return 0;

        var scoped = keyValues is { Count: >= 2 };
        var filter = scoped ? $"WHERE [{typeColumn}]=@Key0 AND [{noColumn}]=@Key1" : string.Empty;
        var executed = 0;
        foreach (var column in columns)
        {
            var sql = $"""
                UPDATE m SET m.[{column}]=d.[{column}]
                FROM dbo.[{masterTable}] m
                INNER JOIN (SELECT [{typeColumn}],[{noColumn}],SUM([{column}]) AS [{column}]
                            FROM dbo.[{detailTable}] {filter}
                            GROUP BY [{typeColumn}],[{noColumn}]) d
                  ON m.[{typeColumn}]=d.[{typeColumn}] AND m.[{noColumn}]=d.[{noColumn}]
                {(scoped ? $"WHERE m.[{typeColumn}]=@Key0 AND m.[{noColumn}]=@Key1" : "")};
                """;
            await using var command = new SqlCommand(sql, connection, transaction);
            if (scoped)
            {
                command.Parameters.Add("@Key0", SqlDbType.NVarChar, 40).Value = keyValues![0]?.Trim() ?? string.Empty;
                command.Parameters.Add("@Key1", SqlDbType.NVarChar, 40).Value = keyValues[1]?.Trim() ?? string.Empty;
            }
            await command.ExecuteNonQueryAsync(token);
            executed++;
        }
        return executed;
    }
}
