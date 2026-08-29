using System.Data;
using EOS.API.Models;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data;

/// <summary>
/// 报表过滤条件设置（2205，旧 RPT/SysqrDft.aspx 的受控等价）：
/// SYSQR_DA 主档（每模块一条）+ SYSQR_DEFAULT 条件行 CRUD。
/// 语义与报表运行时对齐（ReportRepository.ReadConditionsAsync）：
/// 条件行按模块（M_IDX）维护，报表查看器条件面板按此渲染；
/// 条件行写路径结束后清空 SYSQR_USER 用户条件记忆（等价旧 SP
/// P_SYSQR_DEFAULT_After_Save，与 DomainRuleService.SysqrDefaultAfterSaveAsync 同规则）。
/// 表/列名仅来自常量，值全部参数化。
/// </summary>
public sealed class ReportConditionsRepository(DbConnectionFactory connections)
{
    public async Task<List<ReportConditionDraft>> ListConditionsAsync(int moduleId, CancellationToken token)
    {
        const string sql = """
            SELECT CONVERT(INT,SERIAL_NO),COALESCE(TRY_CONVERT(INT,F_TYPE),0),
                   LTRIM(RTRIM(ISNULL(F_ID,''))),LTRIM(RTRIM(ISNULL(F_EXPR,''))),
                   LTRIM(RTRIM(ISNULL(F_DESC,''))),LTRIM(RTRIM(ISNULL(F_VALUE,''))),
                   LTRIM(RTRIM(ISNULL(PARA_NAME,''))),LTRIM(RTRIM(ISNULL(REMARK,'')))
            FROM dbo.SYSQR_DEFAULT WITH (NOLOCK)
            WHERE M_IDX=@ModuleId ORDER BY SERIAL_NO;
            """;
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@ModuleId", SqlDbType.Int).Value = moduleId;
        await using var reader = await command.ExecuteReaderAsync(token);
        var result = new List<ReportConditionDraft>();
        while (await reader.ReadAsync(token))
        {
            result.Add(new ReportConditionDraft(
                reader.GetInt32(0), reader.GetInt32(1),
                EmptyToNull(reader.GetString(2)), EmptyToNull(reader.GetString(3)),
                EmptyToNull(reader.GetString(4)), EmptyToNull(reader.GetString(5)),
                EmptyToNull(reader.GetString(6)), EmptyToNull(reader.GetString(7))));
        }
        return result;
    }

    /// <summary>全部条件行（含模块编号/名称；LEFT JOIN MODULES——模块已不在册的孤儿行保留展示，2026-08-29 单表改版）。</summary>
    public async Task<List<ReportConditionListRow>> ListAllConditionsAsync(CancellationToken token)
    {
        const string sql = """
            SELECT d.M_IDX, LTRIM(RTRIM(ISNULL(m.M_DESC,''))),
                   CONVERT(INT,d.SERIAL_NO), COALESCE(TRY_CONVERT(INT,d.F_TYPE),0),
                   LTRIM(RTRIM(ISNULL(d.F_ID,''))),LTRIM(RTRIM(ISNULL(d.F_EXPR,''))),
                   LTRIM(RTRIM(ISNULL(d.F_DESC,''))),LTRIM(RTRIM(ISNULL(d.F_VALUE,''))),
                   LTRIM(RTRIM(ISNULL(d.PARA_NAME,''))),LTRIM(RTRIM(ISNULL(d.REMARK,'')))
            FROM dbo.SYSQR_DEFAULT d WITH (NOLOCK)
            LEFT JOIN dbo.MODULES m WITH (NOLOCK) ON m.M_IDX=d.M_IDX
            ORDER BY d.M_IDX, d.SERIAL_NO;
            """;
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(token);
        var result = new List<ReportConditionListRow>();
        while (await reader.ReadAsync(token))
        {
            result.Add(new ReportConditionListRow(
                reader.GetInt32(0), reader.GetString(1),
                reader.GetInt32(2), reader.GetInt32(3),
                EmptyToNull(reader.GetString(4)), EmptyToNull(reader.GetString(5)),
                EmptyToNull(reader.GetString(6)), EmptyToNull(reader.GetString(7)),
                EmptyToNull(reader.GetString(8)), EmptyToNull(reader.GetString(9))));
        }
        return result;
    }

    public async Task<string?> GetMasterTableAsync(int moduleId, CancellationToken token)
    {
        const string sql = "SELECT LTRIM(RTRIM(ISNULL(MASTER_TABLE,''))) FROM dbo.MODULES WITH (NOLOCK) WHERE M_IDX=@ModuleId;";
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@ModuleId", SqlDbType.Int).Value = moduleId;
        var value = await command.ExecuteScalarAsync(token);
        var masterTable = Convert.ToString(value)?.Trim();
        return string.IsNullOrEmpty(masterTable) ? null : masterTable;
    }

    /// <summary>新增条件行：确保 SYSQR_DA 主档存在 → 插入条件行 → 清用户条件记忆（同批事务执行）。</summary>
    public async Task CreateConditionAsync(int moduleId, ReportConditionDraft draft, string user, CancellationToken token)
    {
        const string sql = """
            SET XACT_ABORT ON;
            BEGIN TRANSACTION;
            IF NOT EXISTS (SELECT 1 FROM dbo.SYSQR_DA WITH (UPDLOCK,HOLDLOCK) WHERE M_IDX=@ModuleId)
                INSERT INTO dbo.SYSQR_DA (M_IDX,CREATE_PERSON,CREATE_DATE) VALUES (@ModuleId,@User,GETDATE());
            INSERT INTO dbo.SYSQR_DEFAULT (M_IDX,SERIAL_NO,F_ID,F_TYPE,F_EXPR,F_DESC,F_VALUE,PARA_NAME,REMARK)
            VALUES (@ModuleId,@SerialNo,@FieldId,@FType,@Expr,@FDesc,@FValue,@ParaName,@Remark);
            DELETE FROM dbo.SYSQR_USER WHERE M_IDX=@ModuleId;
            COMMIT TRANSACTION;
            """;
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        await using var command = new SqlCommand(sql, connection);
        AddConditionParameters(command, moduleId, draft.SerialNo, draft, user);
        await command.ExecuteNonQueryAsync(token);
    }

    /// <summary>更新条件行：更新条件行 + 主档审计列 → 清用户条件记忆（同批事务执行，返回条件行是否命中）。</summary>
    public async Task<bool> UpdateConditionAsync(int moduleId, int serialNo, ReportConditionDraft draft, string user, CancellationToken token)
    {
        const string sql = """
            SET XACT_ABORT ON;
            BEGIN TRANSACTION;
            UPDATE dbo.SYSQR_DEFAULT
            SET F_ID=@FieldId,F_TYPE=@FType,F_EXPR=@Expr,F_DESC=@FDesc,F_VALUE=@FValue,PARA_NAME=@ParaName,REMARK=@Remark
            WHERE M_IDX=@ModuleId AND SERIAL_NO=@RowSerialNo;
            SET @UpdatedOut = @@ROWCOUNT;
            IF @UpdatedOut > 0
                UPDATE dbo.SYSQR_DA SET LAST_UPDATE_BY=@User,LAST_UPDATE_DATE=GETDATE() WHERE M_IDX=@ModuleId;
            DELETE FROM dbo.SYSQR_USER WHERE M_IDX=@ModuleId;
            COMMIT TRANSACTION;
            """;
        return await ExecuteConditionWriteAsync(sql, moduleId, draft.SerialNo, draft, user, serialNo, token);
    }

    /// <summary>删除条件行 + 主档审计列更新 → 清用户条件记忆（同批事务执行，返回条件行是否命中）。</summary>
    public async Task<bool> DeleteConditionAsync(int moduleId, int serialNo, string user, CancellationToken token)
    {
        const string sql = """
            SET XACT_ABORT ON;
            BEGIN TRANSACTION;
            DELETE FROM dbo.SYSQR_DEFAULT WHERE M_IDX=@ModuleId AND SERIAL_NO=@SerialNo;
            SET @UpdatedOut = @@ROWCOUNT;
            IF @UpdatedOut > 0
                UPDATE dbo.SYSQR_DA SET LAST_UPDATE_BY=@User,LAST_UPDATE_DATE=GETDATE() WHERE M_IDX=@ModuleId;
            DELETE FROM dbo.SYSQR_USER WHERE M_IDX=@ModuleId;
            COMMIT TRANSACTION;
            """;
        return await ExecuteConditionWriteAsync(sql, moduleId, serialNo, null, user, null, token);
    }

    /// <summary>
    /// 条件行写批次执行：命中行数经输出参数返回（批次内 SYSQR_USER 清理会额外影响行数，
    /// 不能用 ExecuteNonQuery 汇总行数判定条件行是否命中）。
    /// </summary>
    private async Task<bool> ExecuteConditionWriteAsync(
        string sql, int moduleId, int serialNo, ReportConditionDraft? draft,
        string user, int? rowSerialNo, CancellationToken token)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        await using var command = new SqlCommand(sql, connection);
        if (draft is not null) AddConditionParameters(command, moduleId, serialNo, draft, user, rowSerialNo);
        else
        {
            command.Parameters.Add("@ModuleId", SqlDbType.Int).Value = moduleId;
            command.Parameters.Add("@SerialNo", SqlDbType.SmallInt).Value = (short)serialNo;
            command.Parameters.Add("@User", SqlDbType.NChar, 40).Value = user.Trim();
        }
        var updated = command.Parameters.Add("@UpdatedOut", SqlDbType.Int);
        updated.Direction = ParameterDirection.Output;
        await command.ExecuteNonQueryAsync(token);
        return Convert.ToInt32(updated.Value ?? 0) > 0;
    }

    /// <summary>
    /// F_TYPE 3/5 数据源表/列物理存在校验（sys.* 目录视图，仅 dbo 表/视图），
    /// 与运行时 ReportRepository 的白名单校验同规则，fail-closed。
    /// </summary>
    public async Task<bool> SelectSourceExistsAsync(string table, string idColumn, string valueColumn, CancellationToken token)
    {
        const string sql = """
            SELECT COUNT(DISTINCT c.name) FROM sys.columns c
            JOIN sys.objects o ON c.object_id=o.object_id AND o.type IN ('U','V')
            JOIN sys.schemas s ON o.schema_id=s.schema_id
            WHERE s.name=N'dbo' AND o.name=@Table AND c.name IN (@IdColumn,@ValueColumn);
            """;
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@Table", SqlDbType.NVarChar, 128).Value = table;
        command.Parameters.Add("@IdColumn", SqlDbType.NVarChar, 128).Value = idColumn;
        command.Parameters.Add("@ValueColumn", SqlDbType.NVarChar, 128).Value = valueColumn;
        var count = Convert.ToInt32(await command.ExecuteScalarAsync(token));
        return count >= 2;
    }

    private static void AddConditionParameters(
        SqlCommand command, int moduleId, int serialNo, ReportConditionDraft draft,
        string user, int? rowSerialNo = null)
    {
        command.Parameters.Add("@ModuleId", SqlDbType.Int).Value = moduleId;
        command.Parameters.Add("@SerialNo", SqlDbType.SmallInt).Value = (short)serialNo;
        if (rowSerialNo is not null)
            command.Parameters.Add("@RowSerialNo", SqlDbType.SmallInt).Value = (short)rowSerialNo.Value;
        command.Parameters.AddWithNullable("FieldId", SqlDbType.NVarChar, 50, draft.Field);
        command.Parameters.Add("@FType", SqlDbType.NChar, 1).Value = draft.Type.ToString();
        command.Parameters.AddWithNullable("Expr", SqlDbType.NVarChar, 2000, draft.Expression);
        command.Parameters.AddWithNullable("FDesc", SqlDbType.NVarChar, 50, draft.Description);
        command.Parameters.AddWithNullable("FValue", SqlDbType.NVarChar, 500, draft.DefaultValue);
        command.Parameters.AddWithNullable("ParaName", SqlDbType.NVarChar, 50, draft.ParameterName);
        command.Parameters.AddWithNullable("Remark", SqlDbType.NVarChar, 500, draft.Remark);
        command.Parameters.Add("@User", SqlDbType.NChar, 40).Value = user.Trim();
    }

    private static string? EmptyToNull(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
