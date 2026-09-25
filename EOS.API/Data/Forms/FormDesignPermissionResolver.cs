using System.Data;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data.Forms;

/// <summary>
/// 版式设计权判定结果。微调档（<c>FORM_ADJUST_TAG</c>）目前只被报表版式设计器的"微调模式"消费，
/// 表单版式只用完整设计档；两档由本记录一并返回，避免各自另写一份判定。
/// </summary>
public sealed record FormDesignMode(bool CanDesign, bool CanAdjust);

/// <summary>
/// 版式设计权限的**唯一**判定入口：个人权限（SYSDD）覆盖组权限——某模块只要有个人权限记录，
/// 就完全采用个人记录，不合并组记录；无个人记录时取该用户所属各组该位的布尔 OR。
///
/// 表单版式与报表版式共用本实现：同一安全语义写成两份，两侧的兜底分支迟早会漂移
/// （历史上就有过"一处 fail-closed、另一处静默放行"的教训）。
/// </summary>
public static class FormDesignPermissionResolver
{
    public static async Task<FormDesignMode> ResolveAsync(
        SqlConnection connection, string userId, int moduleId, CancellationToken token)
    {
        const string personalSql = """
            SELECT ISNULL(FORM_DESIGN_TAG, 0) AS CAN_DESIGN, ISNULL(FORM_ADJUST_TAG, 0) AS CAN_ADJUST
            FROM dbo.SYSDD WITH (NOLOCK)
            WHERE USER_ID = @UserId AND M_IDX = @ModuleId;
            """;
        await using (var command = new SqlCommand(personalSql, connection))
        {
            command.Parameters.Add("@UserId", SqlDbType.NChar, 10).Value = userId.Trim();
            command.Parameters.Add("@ModuleId", SqlDbType.Int).Value = moduleId;
            await using var reader = await command.ExecuteReaderAsync(token);
            if (await reader.ReadAsync(token))
            {
                return new FormDesignMode(reader.GetBoolean("CAN_DESIGN"), reader.GetBoolean("CAN_ADJUST"));
            }
        }

        const string groupSql = """
            SELECT ISNULL(MAX(CAST(FORM_DESIGN_TAG AS INT)), 0) AS CAN_DESIGN,
                   ISNULL(MAX(CAST(FORM_ADJUST_TAG AS INT)), 0) AS CAN_ADJUST
            FROM dbo.SYSDH h WITH (NOLOCK)
            INNER JOIN dbo.SYSDG_USER gu WITH (NOLOCK) ON gu.G_IDX = h.G_IDX
            WHERE gu.USER_ID = @UserId AND h.M_IDX = @ModuleId;
            """;
        await using var groupCommand = new SqlCommand(groupSql, connection);
        groupCommand.Parameters.Add("@UserId", SqlDbType.NChar, 10).Value = userId.Trim();
        groupCommand.Parameters.Add("@ModuleId", SqlDbType.Int).Value = moduleId;
        await using var groupReader = await groupCommand.ExecuteReaderAsync(token);
        if (await groupReader.ReadAsync(token))
        {
            return new FormDesignMode(
                groupReader.GetInt32("CAN_DESIGN") != 0, groupReader.GetInt32("CAN_ADJUST") != 0);
        }
        return new FormDesignMode(false, false);
    }

    /// <summary>是否存在任一模块的完整设计权限（全局资产维护入口用）。</summary>
    public static async Task<bool> HasAnyAsync(SqlConnection connection, string userId, CancellationToken token)
    {
        const string personalSql = """
            SELECT TOP 1 1 FROM dbo.SYSDD WITH (NOLOCK)
            WHERE USER_ID = @UserId AND ISNULL(FORM_DESIGN_TAG, 0) = 1;
            """;
        await using (var personalCommand = new SqlCommand(personalSql, connection))
        {
            personalCommand.Parameters.Add("@UserId", SqlDbType.NChar, 10).Value = userId.Trim();
            if (await personalCommand.ExecuteScalarAsync(token) is not null) return true;
        }

        const string groupSql = """
            SELECT TOP 1 1
            FROM dbo.SYSDH h WITH (NOLOCK)
            INNER JOIN dbo.SYSDG_USER gu WITH (NOLOCK) ON gu.G_IDX = h.G_IDX
            WHERE gu.USER_ID = @UserId AND ISNULL(h.FORM_DESIGN_TAG, 0) = 1;
            """;
        await using var groupCommand = new SqlCommand(groupSql, connection);
        groupCommand.Parameters.Add("@UserId", SqlDbType.NChar, 10).Value = userId.Trim();
        return await groupCommand.ExecuteScalarAsync(token) is not null;
    }
}
