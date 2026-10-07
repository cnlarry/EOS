using System.Data;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data.Forms;

/// <summary>
/// 版式校验与设计态共用的字段事实：主键、单据生命周期系统列、用户可填的必填列、
/// 是否有启用中的选择器来源。
///
/// 口径取自与表单构建同一条读取路径（FIELDS + 物理结构），避免"设计态认为不可移除、
/// 保存时按另一套判定放行"这类双份实现。事实只描述字段本身，不带权限语义——
/// 权限仍是运行的边界（版式只决定排在哪儿、藏没藏）。
/// </summary>
internal static class FormLayoutFactsBuilder
{
    /// <summary>目标表的字段事实，键为字段代号（大小写不敏感）。</summary>
    public static async Task<IReadOnlyDictionary<string, FormLayoutFieldFact>> BuildAsync(
        SqlConnection connection,
        string masterTable,
        string targetTable,
        CancellationToken token)
    {
        var rows = await WorkbenchDefinitionBuilder.ReadFormFieldRows(
            connection, masterTable, targetTable, token, includeVirtual: true);
        var violatesNotNull = await ReadNotNullWithoutDefaultAsync(connection, targetTable, token);

        var facts = new Dictionary<string, FormLayoutFieldFact>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            var userFillable = IsUserFillable(row);
            facts[row.Key] = new FormLayoutFieldFact(
                row.Key,
                targetTable,
                row.IsPrimaryKey,
                IsLifecycleSystemColumn(row.Key),
                userFillable && (row.IsRequired || violatesNotNull.Contains(row.Key)),
                row.Choosers.Any(chooser => chooser.Active));
        }
        return facts;
    }

    /// <summary>单据生命周期系统列（经办人/日期/状态位）与数据归属列：界面上不提供"从表单移除"。</summary>
    public static bool IsLifecycleSystemColumn(string key)
        => LifecycleColumns.Contains(key);

    private static readonly HashSet<string> LifecycleColumns = new(
        WorkflowStates.LifecycleActorColumns
            .Concat(WorkflowStates.LifecycleTagColumns),
        StringComparer.OrdinalIgnoreCase);

    /// <summary>用户能自己填的字段：可见、非只读、非虚拟，且不由服务端独占填充。</summary>
    private static bool IsUserFillable(FormFieldRow row)
        => row.IsVisible && !row.IsReadonly && !row.IsVirtual && !IsLifecycleSystemColumn(row.Key);

    /// <summary>库里 NOT NULL 且无默认值、非自增/非计算：用户留空即撞库约束（与必填口径同源）。</summary>
    private static async Task<HashSet<string>> ReadNotNullWithoutDefaultAsync(
        SqlConnection connection, string targetTable, CancellationToken token)
    {
        const string sql = """
            SELECT c.name
            FROM sys.columns c
            JOIN sys.tables t ON t.object_id=c.object_id
            JOIN sys.schemas s ON s.schema_id=t.schema_id
            LEFT JOIN sys.default_constraints dc
                   ON dc.parent_object_id=c.object_id AND dc.parent_column_id=c.column_id
            WHERE s.name=N'dbo' AND t.name=@Table
              AND c.is_nullable=0 AND c.is_computed=0 AND c.is_identity=0 AND dc.object_id IS NULL;
            """;
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@Table", SqlDbType.VarChar, 100).Value = targetTable;
        await using var reader = await command.ExecuteReaderAsync(token);
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (await reader.ReadAsync(token))
        {
            columns.Add(reader.GetString(0).Trim());
        }
        return columns;
    }
}
