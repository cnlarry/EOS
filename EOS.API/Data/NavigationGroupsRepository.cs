using System.Data;
using EOS.API.Errors;
using EOS.API.Models;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data;

public sealed record NavigationGroupDefinition(int Index, string? Description, bool Enabled, bool Available);

/// <summary>
/// 菜单分组（第 4 级）数据：读取 MODULES.GROUP1..5 / GROUP_EXP1..5 / GROUP_DESC1..5，
/// 用 GroupExpressionParser 受控编译后查询主表分组值。
/// 安全边界：表达式字段白名单 = 主表可见、非虚拟、物理存在的 FIELDS 列
/// （受禁止字段/成本/保密权限约束）；不可解析一律抛 GroupExpressionUnsupportedException。
/// </summary>
public sealed class NavigationGroupsRepository(DbConnectionFactory connections, ILogger<NavigationGroupsRepository> logger)
{
    private const int MaxGroupValues = 500;

    public async Task<IReadOnlyList<NavigationGroupDefinition>> GetGroupsAsync(
        int moduleId,
        LegacyModuleRights rights,
        CancellationToken token)
    {
        var (masterTable, expressions, descriptions, enabledFlags) = await ReadGroupMetadataAsync(moduleId, token);
        if (string.IsNullOrWhiteSpace(masterTable)) return [];
        var allowed = await ReadFilterFieldKeysAsync(masterTable, rights, token);
        var groups = new List<NavigationGroupDefinition>();
        for (var i = 0; i < 5; i++)
        {
            if (!enabledFlags[i]) continue;
            var expression = expressions[i];
            var description = descriptions[i];
            if (string.IsNullOrWhiteSpace(expression) || string.IsNullOrWhiteSpace(description)) continue;
            var available = GroupExpressionParser.TryCompile(expression, masterTable, allowed, out _);
            groups.Add(new NavigationGroupDefinition(i + 1, description, true, available));
        }
        return groups;
    }

    public async Task<IReadOnlyList<string>> GetGroupValuesAsync(
        int moduleId,
        int index,
        LegacyModuleRights rights,
        CancellationToken token)
    {
        if (index is < 1 or > 5)
            throw new ArgumentException("group index 必须在 1~5 之间。");
        var (masterTable, expressions, descriptions, enabledFlags) = await ReadGroupMetadataAsync(moduleId, token);
        if (string.IsNullOrWhiteSpace(masterTable))
            throw new GroupExpressionUnsupportedException("该模块没有分组定义，已拒绝查询。");
        if (!enabledFlags[index - 1] || string.IsNullOrWhiteSpace(expressions[index - 1]))
            throw new GroupExpressionUnsupportedException("该模块未启用此分组表达式，已拒绝查询。");
        var allowed = await ReadFilterFieldKeysAsync(masterTable, rights, token);
        var expression = expressions[index - 1];
        if (!GroupExpressionParser.TryCompile(expression, masterTable, allowed, out var compiled))
            throw new GroupExpressionUnsupportedException("分组表达式超出受控子集，已拒绝查询。");

        logger.LogDebug("分组值查询 module={ModuleId} group={GroupIndex} master={Master}", moduleId, index, masterTable);
        await using var connection = connections.Create();
        await using var command = new SqlCommand(
            $"SELECT DISTINCT TOP ({MaxGroupValues}) {compiled} AS GroupValue FROM dbo.[{masterTable}] WITH (NOLOCK) " +
            $"WHERE {compiled} IS NOT NULL ORDER BY 1 DESC;",
            connection);
        await connection.OpenAsync(token);
        await using var reader = await command.ExecuteReaderAsync(token);
        var values = new List<string>();
        while (await reader.ReadAsync(token))
            values.Add(reader.IsDBNull(0) ? string.Empty : Convert.ToString(reader.GetValue(0), System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty);
        return values;
    }

    private async Task<(string MasterTable, string[] Expressions, string[] Descriptions, bool[] Enabled)> ReadGroupMetadataAsync(int moduleId, CancellationToken token)
    {
        const string sql = """
            SELECT MASTER_TABLE,GROUP1,GROUP_EXP1,GROUP_DESC1,GROUP2,GROUP_EXP2,GROUP_DESC2,
                   GROUP3,GROUP_EXP3,GROUP_DESC3,GROUP4,GROUP_EXP4,GROUP_DESC4,
                   GROUP5,GROUP_EXP5,GROUP_DESC5
            FROM dbo.MODULES WITH (NOLOCK) WHERE M_IDX=@ModuleId;
            """;
        await using var connection = connections.Create();
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@ModuleId", SqlDbType.Int).Value = moduleId;
        await connection.OpenAsync(token);
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token)) return (string.Empty, new string[5], new string[5], new bool[5]);
        var master = reader.IsDBNull(0) ? string.Empty : reader.GetString(0).Trim();
        var expressions = new string[5];
        var descriptions = new string[5];
        var enabled = new bool[5];
        for (var i = 0; i < 5; i++)
        {
            var offset = 1 + i * 3;
            enabled[i] = !reader.IsDBNull(offset) && reader.GetBoolean(offset);
            expressions[i] = reader.IsDBNull(offset + 1) ? string.Empty : reader.GetString(offset + 1).Trim();
            descriptions[i] = reader.IsDBNull(offset + 2) ? string.Empty : reader.GetString(offset + 2).Trim();
        }
        return (master, expressions, descriptions, enabled);
    }

    /// <summary>
    /// 分组表达式字段白名单：主表全部可见、非虚拟、物理存在字段
    /// （与工作台 MODULES.FILTER 白名单同口径，仍受成本/保密/禁止字段约束）。
    /// </summary>
    private async Task<IReadOnlySet<string>> ReadFilterFieldKeysAsync(
        string masterTable,
        LegacyModuleRights rights,
        CancellationToken token)
    {
        const string sql = """
            SELECT LTRIM(RTRIM(f.F_ID)),CAST(COALESCE(f.IS_COST,0) AS bit),CAST(COALESCE(f.IS_SECRECY,0) AS bit)
            FROM dbo.FIELDS f WITH (NOLOCK)
            WHERE f.T_ID=@MasterTable AND COALESCE(f.IS_VISIBLE,1)=1 AND COALESCE(f.IS_VIRTUAL,0)=0
              AND EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS c
                          WHERE c.TABLE_SCHEMA='dbo' AND c.TABLE_NAME=@MasterTable AND c.COLUMN_NAME=f.F_ID)
            ORDER BY f.F_ID;
            """;
        await using var connection = connections.Create();
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@MasterTable", SqlDbType.NVarChar, 100).Value = masterTable;
        await connection.OpenAsync(token);
        await using var reader = await command.ExecuteReaderAsync(token);
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (await reader.ReadAsync(token))
        {
            var key = reader.GetString(0).Trim();
            if (rights.DeniedMasterFields.Contains(key)) continue;
            if (!rights.CanViewCost && reader.GetBoolean(1)) continue;
            if (!rights.CanViewSecrecy && reader.GetBoolean(2)) continue;
            result.Add(key);
        }
        return result;
    }
}
