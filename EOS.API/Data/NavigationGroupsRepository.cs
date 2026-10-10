using System.Data;
using EOS.API.Data.Workbench;
using EOS.API.Errors;
using EOS.API.Models;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data;

public sealed record NavigationGroupDefinition(int GroupId, string Description, bool Available);

/// <summary>
/// 菜单分组（第 4 级）数据：读取 MODULE_GROUPS 里该模块的分组（GROUP_ID / GROUP_DESC / GROUP_EXP），
/// 用 GroupExpressionParser 受控编译后查询主表分组值。
///
/// 分组身份是 GROUP_ID，不是"第几组"：SORT_IDX 只决定下拉里的先后，删除或调整顺序
/// 不该让既有的分组筛选链接指到另一个表达式上去。
/// 有行即生效——行存在、表达式与名称都非空即对外提供该分组，没有单独的启用位。
///
/// 安全边界：表达式字段白名单 = 主表物理存在、非虚拟的 FIELDS 列（含隐藏字段）
/// （受禁止字段/成本/保密权限约束）；不可解析一律抛 GroupExpressionUnsupportedException。
/// </summary>
public sealed class NavigationGroupsRepository(DbConnectionFactory connections, ILogger<NavigationGroupsRepository> logger)
{
    private const int MaxGroupValues = 500;

    /// <summary>模块的分组行（已过滤空名称/空表达式的行，按 SORT_IDX、GROUP_ID 排序）。</summary>
    private sealed record ModuleGroup(int GroupId, int SortIdx, string Description, string Expression);

    public async Task<IReadOnlyList<NavigationGroupDefinition>> GetGroupsAsync(
        int moduleId,
        ModuleRights rights,
        CancellationToken token)
    {
        var (masterTable, groups) = await ReadGroupsAsync(moduleId, token);
        if (string.IsNullOrWhiteSpace(masterTable)) return [];
        var allowed = await ReadAllowedFieldsAsync(masterTable, rights, token);
        var result = new List<NavigationGroupDefinition>(groups.Count);
        foreach (var group in groups)
        {
            var available = GroupExpressionParser.TryCompile(group.Expression, masterTable, allowed, out _);
            result.Add(new NavigationGroupDefinition(group.GroupId, group.Description, available));
        }
        return result;
    }

    public async Task<IReadOnlyList<string>> GetGroupValuesAsync(
        int moduleId,
        int groupId,
        ModuleRights rights,
        CancellationToken token)
    {
        var (masterTable, groups) = await ReadGroupsAsync(moduleId, token);
        if (string.IsNullOrWhiteSpace(masterTable))
            throw new GroupExpressionUnsupportedException("该模块没有分组定义，已拒绝查询。");
        var group = groups.FirstOrDefault(item => item.GroupId == groupId);
        if (group is null)
            throw new GroupExpressionUnsupportedException("该模块没有这个分组，已拒绝查询。");
        var allowed = await ReadAllowedFieldsAsync(masterTable, rights, token);
        if (!GroupExpressionParser.TryCompile(group.Expression, masterTable, allowed, out var compiled))
            throw new GroupExpressionUnsupportedException("分组表达式超出受控子集，已拒绝查询。");

        logger.LogDebug("分组值查询 module={ModuleId} group={GroupId} master={Master}", moduleId, groupId, masterTable);
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

    /// <summary>读模块主表与其分组行；模块不存在时主表为空串。</summary>
    private async Task<(string MasterTable, IReadOnlyList<ModuleGroup> Groups)> ReadGroupsAsync(
        int moduleId,
        CancellationToken token)
    {
        const string sql = """
            SELECT ISNULL(LTRIM(RTRIM(m.MASTER_TABLE)),''),
                   g.GROUP_ID,g.SORT_IDX,g.GROUP_DESC,g.GROUP_EXP
            FROM dbo.MODULES m WITH (NOLOCK)
            LEFT JOIN dbo.MODULE_GROUPS g WITH (NOLOCK) ON g.M_IDX=m.M_IDX
            WHERE m.M_IDX=@ModuleId
            ORDER BY g.SORT_IDX,g.GROUP_ID;
            """;
        await using var connection = connections.Create();
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@ModuleId", SqlDbType.Int).Value = moduleId;
        await connection.OpenAsync(token);
        await using var reader = await command.ExecuteReaderAsync(token);
        var master = string.Empty;
        var groups = new List<ModuleGroup>();
        var first = true;
        while (await reader.ReadAsync(token))
        {
            if (first)
            {
                master = reader.GetString(0).Trim();
                first = false;
            }
            if (await reader.IsDBNullAsync(1, token)) continue;
            var description = reader.IsDBNull(3) ? string.Empty : reader.GetString(3).Trim();
            var expression = reader.IsDBNull(4) ? string.Empty : reader.GetString(4).Trim();
            if (description.Length == 0 || expression.Length == 0) continue;
            groups.Add(new ModuleGroup(reader.GetInt32(1), reader.IsDBNull(2) ? 0 : reader.GetInt32(2), description, expression));
        }
        return (master, groups);
    }

    /// <summary>
    /// 分组表达式字段白名单（读侧口径）：物理存在、非虚拟字段再按调用者权限收敛
    /// （禁止字段 / 成本 / 保密）。实现与配置写侧、发布校验共用
    /// <see cref="ModuleFieldWhitelist"/>——两侧各写一遍时，写侧放行的表达式可能在这里被拒，
    /// 那种分组"存得下、点不开"。
    /// </summary>
    private async Task<IReadOnlySet<string>> ReadAllowedFieldsAsync(
        string masterTable,
        ModuleRights rights,
        CancellationToken token)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        return await ModuleFieldWhitelist.ReadAsync(connection, null, masterTable, rights, token);
    }
}
