using System.Data;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data.Reports;

/// <summary>
/// 模块级报表可见性的**唯一实现**：报表的可见范围只由归属模块的 <c>REPORT_TAG</c> 决定
/// ——个人行 <c>SYS_USER_MODULE_PERMISSION</c> 优先（有个人行就只看个人），没有个人行才看
/// <c>SYS_GROUP_MODULE_PERMISSION</c>（多组取 OR）。
///
/// <para>
/// 这段判据此前有三份：报表权读取、打印面板的报表清单、报表中心目录。三处各写一遍时，
/// "目录里看得见、打印面板里没有"这类不一致只能靠人眼发现，改一处也不等于改齐。
/// 现在集合查询用 <see cref="ForUser"/> 拼进 WHERE，单模块查询用 <see cref="IsVisibleAsync"/>
/// ——后者就是把同一段谓词套进 <c>CASE WHEN</c>，两条路径不可能各判一套。
/// </para>
/// </summary>
internal static class ModuleReportVisibility
{
    /// <summary>
    /// 可见性谓词。带 <see cref="UserIdParameter"/> 与模块号两个参数。
    /// </summary>
    /// <param name="moduleIdExpression">
    /// 模块号所在的 SQL 表达式：单模块查模块号列时传 <c>@ModuleId</c>，按报表行逐行判定时传
    /// 行里的模块列（如 <c>r.M_IDX</c>）。**只由本类调用方传常量**，不接受任何外部输入。
    /// </param>
    internal static string ForUser(string moduleIdExpression) => $$"""
        (
            EXISTS (SELECT 1 FROM dbo.SYS_USER_MODULE_PERMISSION d WITH (NOLOCK)
                    WHERE d.USER_ID = @UserId AND d.M_IDX = {{moduleIdExpression}} AND ISNULL(d.REPORT_TAG, 0) = 1)
            OR (
                NOT EXISTS (SELECT 1 FROM dbo.SYS_USER_MODULE_PERMISSION d WITH (NOLOCK)
                            WHERE d.USER_ID = @UserId AND d.M_IDX = {{moduleIdExpression}})
                AND EXISTS (SELECT 1 FROM dbo.SYS_GROUP_MODULE_PERMISSION h WITH (NOLOCK)
                            INNER JOIN dbo.SYS_USER_GROUP_MEMBER gu WITH (NOLOCK) ON gu.G_IDX = h.G_IDX
                            WHERE gu.USER_ID = @UserId AND h.M_IDX = {{moduleIdExpression}} AND ISNULL(h.REPORT_TAG, 0) = 1)
            )
        )
        """;

    /// <summary>用户参数名：两处查询都用它，参数化连接由调用方负责。</summary>
    internal const string UserIdParameter = "@UserId";

    /// <summary>单个模块的报表可见性。与集合查询共用 <see cref="ForUser"/>，语义不可能分叉。</summary>
    internal static async Task<bool> IsVisibleAsync(
        SqlConnection connection, string userId, int moduleId, CancellationToken token)
    {
        var sql = $"SELECT CASE WHEN {ForUser("@ModuleId")} THEN 1 ELSE 0 END;";
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add(UserIdParameter, SqlDbType.NChar, 10).Value = userId.Trim();
        command.Parameters.Add("@ModuleId", SqlDbType.Int).Value = moduleId;
        var result = await command.ExecuteScalarAsync(token);
        return result is not null and not DBNull && Convert.ToBoolean(result);
    }
}
