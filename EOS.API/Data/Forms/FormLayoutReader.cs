using System.Data;
using EOS.API.Models;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data.Forms;

/// <summary>
/// 读取模块级表单版式：表里有行即「已定制」（该表在表单上的字段集完全由版式行决定），
/// 无行则按字段级既有配置（FIELDS.FORM_*）推导默认版式。
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
        var cols = columns > 0 ? columns : 2;
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
            // 主表表单行不含虚拟列（与表单构建同口径），明细含（明细表单行查询带 includeVirtual）
            masterRows = await DeriveRows(connection, masterTable, masterTable, includeVirtual: false, cols, token);
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
                var derived = await DeriveRows(connection, masterTable, detailTable, includeVirtual: true, cols, token);
                detailRows = derived.Select(row => new FormDetailLayoutRow(row.Key, row.OrderNo, row.Hidden)).ToList();
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

    private static async Task<IReadOnlyList<FormLayoutRow>> DeriveRows(
        SqlConnection connection, string masterTable, string targetTable, bool includeVirtual, int columns, CancellationToken token)
    {
        var fields = await WorkbenchDefinitionBuilder.ReadFormFieldRows(connection, masterTable, targetTable, token, includeVirtual);
        var inputs = fields
            .Select(row => new FormLayoutFieldInput(
                row.Key, row.DataType, row.IsVisible, row.IsRequired, row.TabNo, row.FormOrder,
                row.Span, row.NewLine, row.CellGroup, row.CellRole))
            .ToList();
        return FormLayoutDerivation.DeriveDefault(inputs, [], columns).Master;
    }
}
