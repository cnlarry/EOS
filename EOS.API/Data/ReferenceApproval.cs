using System.Collections.Concurrent;
using System.Data;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data;

/// <summary>
/// 引用数据的批核门槛：**任何被引用的外部数据，只要来源表有 APPROVED 列，就只能是已批核的行**。
///
/// 这条规则不看模块有没有启用批核、有没有开自动批核——批核状态是数据自己的属性，
/// 与"这个模块今天有没有批核入口"无关。历史数据里出现过"模块没开批核、却带着 APPROVED=1"的形状
/// （迁移过来的旧库数据），所以不能靠模块标志位推断，只能看列在不在。
///
/// 落点是**查询层而不是配置层**：`FIELD_DATASOURCE` 有一千多条、以后还会新增，
/// 靠逐条配置保证"新写的选择器也记得加"是不现实的；这里是所有选择器读数经过的那一处，
/// 加一次就全覆盖，且新配置天然继承。
///
/// 覆盖范围（业务与配置同一口径，2026-11-01 用户拍板）：字段/明细选择器、统一选择器的业务单据源、
/// 以及统一选择器的**配置面源**（账号 / 用户组 / 库位 / 批次…）。
///
/// 缓存：列存在性是 DDL 事实，而 DDL 只随迁移在启动时变化，故按表名缓存到进程结束。
/// 迁移改了列集合需要重启——与其余"启动时应用迁移"的口径一致。
/// </summary>
internal static class ReferenceApproval
{
    /// <summary>批核状态列名（生命周期列命名收敛后的唯一写法）。</summary>
    internal const string Column = "APPROVED";

    private static readonly ConcurrentDictionary<string, bool> HasApprovedColumnCache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 引用该表时必须附加的谓词（不含前导 AND）；表没有批核列时返回空串。
    /// <paramref name="alias"/> 是 FROM 里用的表别名（与基表同名时传表名）。
    /// </summary>
    /// <remarks>
    /// 用连接工厂而不是现成连接：调用方的连接往往还没打开（SQL 是拼好了才开），
    /// 而"检查列存在性"不该逼着它们改连接的打开时机。命中缓存时不开连接。
    /// </remarks>
    internal static async Task<string> PredicateAsync(
        DbConnectionFactory connections, string alias, string table, CancellationToken token)
    {
        var key = table.Trim();
        if (!WorkbenchSql.Identifier.IsMatch(alias) || !WorkbenchSql.Identifier.IsMatch(key))
        {
            // 标识符不合规时不拼进 SQL：调用方本来也不该走到这里（表名来自注册表/元数据）。
            return string.Empty;
        }
        if (!HasApprovedColumnCache.TryGetValue(key, out var hasApproved))
        {
            await using var connection = connections.Create();
            await connection.OpenAsync(token);
            hasApproved = await ReadHasApprovedColumnAsync(connection, key, token);
            HasApprovedColumnCache[key] = hasApproved;
        }
        return hasApproved ? $"[{alias}].[{Column}] = 1" : string.Empty;
    }

    /// <summary>表（或视图）是否有批核状态列。</summary>
    internal static async Task<bool> HasApprovedColumnAsync(
        SqlConnection connection, string table, CancellationToken token)
    {
        var key = table.Trim();
        if (HasApprovedColumnCache.TryGetValue(key, out var cached))
        {
            return cached;
        }
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(token);
        }
        var exists = await ReadHasApprovedColumnAsync(connection, key, token);
        HasApprovedColumnCache[key] = exists;
        return exists;
    }

    private static async Task<bool> ReadHasApprovedColumnAsync(
        SqlConnection connection, string table, CancellationToken token)
    {
        const string sql = """
            SELECT CASE WHEN EXISTS (
                       SELECT 1 FROM sys.columns c
                       JOIN sys.objects o ON o.object_id = c.object_id
                       JOIN sys.schemas s ON s.schema_id = o.schema_id
                       WHERE s.name = N'dbo' AND o.name = @Table AND o.type IN ('U', 'V')
                         AND c.name = N'APPROVED')
                   THEN 1 ELSE 0 END;
            """;
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@Table", SqlDbType.NVarChar, 200).Value = table;
        return Convert.ToInt32(await command.ExecuteScalarAsync(token) ?? 0) == 1;
    }
}
