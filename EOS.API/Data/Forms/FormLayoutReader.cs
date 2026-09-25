using System.Data;
using EOS.API.Models;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data.Forms;

/// <summary>
/// 读取模块级表单版式：表里有行即「已定制」（该表在表单上的字段集完全由版式行决定），
/// 无行则视为「未定制」——字段按元数据顺序原样渲染（字段级 `FIELDS.FORM_*` 已退役，不再据其推导）。
///
/// 定制判定按「模块 × 表」分别进行：主表有行则主表已定制、明细有行则明细已定制。
/// 不能按「该模块有没有任何行」统一判定——只有明细行的模块会把主表字段整批判成
/// 「未加入表单」而全部消失。
/// </summary>
internal static class FormLayoutReader
{
    public static async Task<FormLayoutDefinition> ReadAsync(
        SqlConnection connection,
        int moduleId,
        string masterTable,
        string? detailTable,
        int columns,
        CancellationToken token)
    {
        var cols = columns > 0 ? columns : FormLayoutDerivation.DefaultColumns;
        var tabs = await ReadTabsAsync(connection, moduleId, token);
        var rowsByTable = await ReadLayoutRowsAsync(connection, moduleId, token);

        var masterCustomized = rowsByTable.ContainsKey(masterTable);
        var detailCustomized = detailTable is not null && rowsByTable.ContainsKey(detailTable);

        IReadOnlyList<FormLayoutRow> masterRows;
        if (masterCustomized)
        {
            masterRows = rowsByTable[masterTable].OrderBy(row => row.OrderNo).ToList();
        }
        else
        {
            // 无版式行 = 未定制：返回空版式，字段按元数据顺序原样渲染（不再按字段级配置推导）。
            // 模块级版式是唯一真源，字段级 FORM_* 配置已退休。
            masterRows = [];
        }

        var detailRows = new List<FormDetailLayoutRow>();
        if (detailTable is not null)
        {
            if (detailCustomized)
            {
                detailRows = rowsByTable[detailTable]
                    .OrderBy(row => row.OrderNo)
                    .Select(row => new FormDetailLayoutRow(row.Key, row.OrderNo, row.Hidden))
                    .ToList();
            }
            else
            {
                // 同主表：无行 = 未定制，全部明细列按元数据顺序显示
                detailRows = [];
            }
        }

        return new FormLayoutDefinition(cols, tabs, masterRows, detailRows, masterCustomized, detailCustomized);
    }

    private static async Task<IReadOnlyList<FormTabDefinition>> ReadTabsAsync(
        SqlConnection connection, int moduleId, CancellationToken token)
    {
        const string sql = """
            SELECT TAB_NO, LTRIM(RTRIM(ISNULL(TAB_TITLE,'')))
            FROM dbo.MODULE_FORM_TAB WITH (NOLOCK)
            WHERE M_IDX=@ModuleId ORDER BY TAB_NO;
            """;
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@ModuleId", SqlDbType.Int).Value = moduleId;
        await using var reader = await command.ExecuteReaderAsync(token);
        var tabs = new List<FormTabDefinition>();
        while (await reader.ReadAsync(token))
        {
            var title = reader.GetString(1);
            tabs.Add(new FormTabDefinition(reader.GetInt32(0), title.Length == 0 ? "默认" : title));
        }
        return tabs;
    }

    private static async Task<Dictionary<string, List<FormLayoutRow>>> ReadLayoutRowsAsync(
        SqlConnection connection, int moduleId, CancellationToken token)
    {
        const string sql = """
            SELECT LTRIM(RTRIM(T_ID)), LTRIM(RTRIM(F_ID)), TAB_NO, ORDER_NO, SPAN, ROW_SPAN, NEW_LINE,
                   SECTION_ID, CELL_GROUP, CELL_ROLE, IS_HIDDEN
            FROM dbo.MODULE_FORM_LAYOUT WITH (NOLOCK)
            WHERE M_IDX=@ModuleId
            ORDER BY T_ID, ORDER_NO, F_ID;
            """;
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@ModuleId", SqlDbType.Int).Value = moduleId;
        await using var reader = await command.ExecuteReaderAsync(token);
        var rows = new Dictionary<string, List<FormLayoutRow>>(StringComparer.OrdinalIgnoreCase);
        while (await reader.ReadAsync(token))
        {
            var table = reader.GetString(0);
            var row = new FormLayoutRow(
                reader.GetString(1),
                reader.GetInt32(2),
                reader.GetInt32(3),
                reader.GetByte(4),
                reader.GetByte(5),
                reader.GetBoolean(6),
                reader.IsDBNull(7) ? null : reader.GetString(7).Trim(),
                reader.IsDBNull(8) ? null : reader.GetString(8).Trim(),
                reader.GetByte(9),
                reader.GetBoolean(10));
            if (!rows.TryGetValue(table, out var list))
            {
                list = [];
                rows[table] = list;
            }
            list.Add(row);
        }
        return rows;
    }

}
