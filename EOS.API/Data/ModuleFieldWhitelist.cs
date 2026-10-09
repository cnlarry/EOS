using System.Data;
using EOS.API.Models;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data;

/// <summary>
/// 模块主表的字段白名单：**分组表达式**与**模块 FILTER** 共用的一处判据。
/// 口径 = `FIELDS` 已登记 + 物理存在（`sys.columns`，表或视图）+ 非虚拟（`IS_VIRTUAL=0`），
/// 标识符再经 <see cref="WorkbenchSql.Identifier"/> 收敛（含隐藏字段，与用户列选择无关）。
///
/// 三处消费同一个实现：发布校验（系统口径）、分组配置写侧（系统口径）、分组读侧（按调用者权限收敛）。
/// 各写一遍的代价不是重复代码，而是"写的时候放行、读的时候拒绝"——那类表达式保存成功却永远点不开。
/// </summary>
internal static class ModuleFieldWhitelist
{
    /// <param name="rights">
    /// 非空时按调用者在该模块上的权限收敛（禁止字段 / 成本 / 保密）。配置写与发布校验传 <c>null</c>：
    /// 那两处回答的是"这个表达式合法吗"，不是"这个人能不能按这个字段分组"——后者的差异由读侧的
    /// `available` 标记表达（看不到成本列的用户在分组下拉里看到该项置灰），不在这里拦。
    /// </param>
    /// <param name="transaction">
    /// 连接上已开的本地事务（没有就传 <c>null</c>）。**调用方在事务里时必传**：命令不带上事务，
    /// 执行时会抛 `BeginExecuteReader 要求命令拥有事务`——写入路径的校验正好在事务内，
    /// 这条参数就是把那个坑堵死。
    /// </param>
    public static async Task<IReadOnlySet<string>> ReadAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        string table,
        ModuleRights? rights,
        CancellationToken token)
    {
        const string sql = """
            SELECT LTRIM(RTRIM(f.F_ID)),CAST(COALESCE(f.IS_COST,0) AS bit),CAST(COALESCE(f.IS_SECRECY,0) AS bit)
            FROM dbo.FIELDS f WITH (NOLOCK)
            WHERE f.T_ID=@Table AND COALESCE(f.IS_VIRTUAL,0)=0
              AND EXISTS (SELECT 1 FROM sys.columns c
                          JOIN sys.objects o ON c.object_id=o.object_id AND o.type IN ('U','V')
                          JOIN sys.schemas s ON o.schema_id=s.schema_id
                          WHERE s.name=N'dbo' AND o.name=@Table AND c.name=f.F_ID);
            """;
        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@Table", SqlDbType.NVarChar, 100).Value = table;
        await using var reader = await command.ExecuteReaderAsync(token);
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (await reader.ReadAsync(token))
        {
            var key = reader.GetString(0).Trim();
            if (!WorkbenchSql.Identifier.IsMatch(key)) continue;
            if (rights is not null)
            {
                if (rights.DeniedMasterFields.Contains(key)) continue;
                if (!rights.CanViewCost && reader.GetBoolean(1)) continue;
                if (!rights.CanViewSecrecy && reader.GetBoolean(2)) continue;
            }
            result.Add(key);
        }
        return result;
    }
}
