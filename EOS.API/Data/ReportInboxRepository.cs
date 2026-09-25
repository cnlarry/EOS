using System.Data;
using EOS.API.Models;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace EOS.API.Data;

/// <summary>
/// 报表中心调度订阅 + Inbox 仓储：
/// - REPORT_SUBSCRIPTION：用户订阅报表（周期 DAILY/WEEKLY/MONTHLY + 执行时刻 + 启停）；
/// - REPORT_INBOX：订阅产出记录（PDF 相对路径 + 生成时间 + 已读标记）。
/// 权限由控制器按订阅者 REPORT_TAG 校验；调度生成时以订阅者身份取数（调度服务内完成）。
/// 表结构见 Data/Migrations/026_report_schedule_inbox.sql（对象名全大写）。
/// </summary>
public sealed class ReportInboxRepository(
    DbConnectionFactory connections,
    IOptions<ReportInboxSettings> settings)
{
    public sealed record SubscriptionRow(
        int Id, string UserId, int ModuleId, string ReportId,
        string ScheduleType, int RunHour, int RunMinute, int? Weekday, int? MonthDay,
        bool Enabled, DateTime? LastRunAt, string? LastUpdateBy, DateTime? LastUpdateDate);

    public sealed record InboxRow(
        int Id, string UserId, int ModuleId, string ReportId, string Title,
        string PdfPath, DateTime GeneratedAt, bool Read);

    /// <summary>我的订阅列表。</summary>
    public async Task<IReadOnlyList<SubscriptionRow>> ListSubscriptionsAsync(string userId, CancellationToken token)
    {
        const string sql = """
            SELECT ID, LTRIM(RTRIM(USER_ID)), M_IDX, LTRIM(RTRIM(REPORT_ID)),
                   LTRIM(RTRIM(SCHEDULE_TYPE)), RUN_HOUR, RUN_MINUTE, WEEKDAY, MONTH_DAY,
                   ISNULL(ENABLED_TAG,0), LAST_RUN_AT, LTRIM(RTRIM(ISNULL(LAST_UPDATE_BY,''))), LAST_UPDATE_DATE
            FROM dbo.REPORT_SUBSCRIPTION WITH (NOLOCK)
            WHERE USER_ID=@UserId ORDER BY ID;
            """;
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@UserId", SqlDbType.NChar, 10).Value = userId.Trim();
        await using var reader = await command.ExecuteReaderAsync(token);
        var result = new List<SubscriptionRow>();
        while (await reader.ReadAsync(token))
        {
            result.Add(new SubscriptionRow(
                reader.GetInt32(0), reader.GetString(1).Trim(), reader.GetInt32(2), reader.GetString(3).Trim(),
                reader.GetString(4).Trim(), reader.GetInt32(5), reader.GetInt32(6),
                reader.IsDBNull(7) ? null : reader.GetInt32(7), reader.IsDBNull(8) ? null : reader.GetInt32(8),
                reader.GetBoolean(9),
                reader.IsDBNull(10) ? null : reader.GetDateTime(10),
                reader.IsDBNull(11) ? null : reader.GetString(11).Trim(),
                reader.IsDBNull(12) ? null : reader.GetDateTime(12)));
        }
        return result;
    }

    /// <summary>新增订阅（返回新行 ID）。</summary>
    public async Task<int> CreateSubscriptionAsync(
        string userId, int moduleId, string reportId, string scheduleType,
        int runHour, int runMinute, int? weekday, int? monthDay, bool enabled,
        string user, CancellationToken token)
    {
        const string sql = """
            INSERT INTO dbo.REPORT_SUBSCRIPTION
                (USER_ID,M_IDX,REPORT_ID,SCHEDULE_TYPE,RUN_HOUR,RUN_MINUTE,WEEKDAY,MONTH_DAY,ENABLED_TAG,CREATE_PERSON,CREATE_DATE)
            OUTPUT INSERTED.ID
            VALUES (@UserId,@ModuleId,@ReportId,@ScheduleType,@RunHour,@RunMinute,@Weekday,@MonthDay,@Enabled,@User,SYSDATETIME());
            """;
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        await using var command = new SqlCommand(sql, connection);
        AddSubscriptionParameters(command, userId, moduleId, reportId, scheduleType, runHour, runMinute, weekday, monthDay, enabled, user);
        var id = await command.ExecuteScalarAsync(token);
        return Convert.ToInt32(id);
    }

    /// <summary>更新订阅（返回是否命中）。</summary>
    public async Task<bool> UpdateSubscriptionAsync(
        int id, string userId, string reportId, string scheduleType,
        int runHour, int runMinute, int? weekday, int? monthDay, bool enabled,
        string user, CancellationToken token)
    {
        const string sql = """
            UPDATE dbo.REPORT_SUBSCRIPTION
            SET REPORT_ID=@ReportId,SCHEDULE_TYPE=@ScheduleType,RUN_HOUR=@RunHour,RUN_MINUTE=@RunMinute,
                WEEKDAY=@Weekday,MONTH_DAY=@MonthDay,ENABLED_TAG=@Enabled,LAST_UPDATE_BY=@User,LAST_UPDATE_DATE=SYSDATETIME()
            WHERE ID=@Id AND USER_ID=@UserId;
            """;
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@Id", SqlDbType.Int).Value = id;
        AddSubscriptionParameters(command, userId, 0, reportId, scheduleType, runHour, runMinute, weekday, monthDay, enabled, user);
        return await command.ExecuteNonQueryAsync(token) > 0;
    }

    /// <summary>删除订阅（返回是否命中）。</summary>
    public async Task<bool> DeleteSubscriptionAsync(int id, string userId, CancellationToken token)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        await using var command = new SqlCommand("DELETE FROM dbo.REPORT_SUBSCRIPTION WHERE ID=@Id AND USER_ID=@UserId;", connection);
        command.Parameters.Add("@Id", SqlDbType.Int).Value = id;
        command.Parameters.Add("@UserId", SqlDbType.NChar, 10).Value = userId.Trim();
        return await command.ExecuteNonQueryAsync(token) > 0;
    }

    /// <summary>我的 Inbox 列表（按生成时间倒序，限 N）。</summary>
    public async Task<IReadOnlyList<InboxRow>> ListInboxAsync(string userId, int limit, CancellationToken token)
    {
        const string sql = """
            SELECT TOP (@Limit) ID, LTRIM(RTRIM(USER_ID)), M_IDX, LTRIM(RTRIM(REPORT_ID)), TITLE,
                   LTRIM(RTRIM(PDF_PATH)), GENERATED_AT, ISNULL(READ_TAG,0)
            FROM dbo.REPORT_INBOX WITH (NOLOCK)
            WHERE USER_ID=@UserId ORDER BY GENERATED_AT DESC;
            """;
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@Limit", SqlDbType.Int).Value = Math.Clamp(limit, 1, 100);
        command.Parameters.Add("@UserId", SqlDbType.NChar, 10).Value = userId.Trim();
        await using var reader = await command.ExecuteReaderAsync(token);
        var result = new List<InboxRow>();
        while (await reader.ReadAsync(token))
        {
            result.Add(new InboxRow(
                reader.GetInt32(0), reader.GetString(1).Trim(), reader.GetInt32(2), reader.GetString(3).Trim(),
                reader.GetString(4), reader.GetString(5).Trim(), reader.GetDateTime(6), reader.GetBoolean(7)));
        }
        return result;
    }

    /// <summary>标记已读（返回是否命中）。</summary>
    public async Task<bool> MarkReadAsync(int id, string userId, CancellationToken token)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        await using var command = new SqlCommand("UPDATE dbo.REPORT_INBOX SET READ_TAG=1 WHERE ID=@Id AND USER_ID=@UserId;", connection);
        command.Parameters.Add("@Id", SqlDbType.Int).Value = id;
        command.Parameters.Add("@UserId", SqlDbType.NChar, 10).Value = userId.Trim();
        return await command.ExecuteNonQueryAsync(token) > 0;
    }

    /// <summary>调度扫描：取所有启用且到期的订阅（按周期判断是否应执行）。</summary>
    public async Task<IReadOnlyList<SubscriptionRow>> FindDueSubscriptionsAsync(DateTime now, CancellationToken token)
    {
        const string sql = """
            SELECT ID, LTRIM(RTRIM(USER_ID)), M_IDX, LTRIM(RTRIM(REPORT_ID)),
                   LTRIM(RTRIM(SCHEDULE_TYPE)), RUN_HOUR, RUN_MINUTE, WEEKDAY, MONTH_DAY,
                   ISNULL(ENABLED_TAG,0), LAST_RUN_AT, LTRIM(RTRIM(ISNULL(LAST_UPDATE_BY,''))), LAST_UPDATE_DATE
            FROM dbo.REPORT_SUBSCRIPTION WITH (NOLOCK)
            WHERE ISNULL(ENABLED_TAG,0)=1
              AND (LAST_RUN_AT IS NULL OR DATEDIFF(MINUTE, LAST_RUN_AT, @Now) >= 1)
            ORDER BY ID;
            """;
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@Now", SqlDbType.DateTime2).Value = now;
        await using var reader = await command.ExecuteReaderAsync(token);
        var result = new List<SubscriptionRow>();
        while (await reader.ReadAsync(token))
        {
            result.Add(new SubscriptionRow(
                reader.GetInt32(0), reader.GetString(1).Trim(), reader.GetInt32(2), reader.GetString(3).Trim(),
                reader.GetString(4).Trim(), reader.GetInt32(5), reader.GetInt32(6),
                reader.IsDBNull(7) ? null : reader.GetInt32(7), reader.IsDBNull(8) ? null : reader.GetInt32(8),
                reader.GetBoolean(9),
                reader.IsDBNull(10) ? null : reader.GetDateTime(10),
                reader.IsDBNull(11) ? null : reader.GetString(11).Trim(),
                reader.IsDBNull(12) ? null : reader.GetDateTime(12)));
        }
        return result;
    }

    /// <summary>记录订阅产出到 Inbox（返回新行 ID）。</summary>
    public async Task<int> RecordInboxAsync(
        string userId, int moduleId, string reportId, string title, string pdfPath,
        CancellationToken token)
    {
        const string sql = """
            INSERT INTO dbo.REPORT_INBOX (USER_ID,M_IDX,REPORT_ID,TITLE,PDF_PATH,GENERATED_AT,READ_TAG)
            OUTPUT INSERTED.ID
            VALUES (@UserId,@ModuleId,@ReportId,@Title,@PdfPath,SYSDATETIME(),0);
            """;
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@UserId", SqlDbType.NChar, 10).Value = userId.Trim();
        command.Parameters.Add("@ModuleId", SqlDbType.Int).Value = moduleId;
        command.Parameters.Add("@ReportId", SqlDbType.NChar, 50).Value = reportId.Trim();
        command.Parameters.Add("@Title", SqlDbType.NVarChar, 200).Value = title;
        command.Parameters.Add("@PdfPath", SqlDbType.NVarChar, 500).Value = pdfPath;
        return Convert.ToInt32(await command.ExecuteScalarAsync(token));
    }

    /// <summary>更新订阅上次执行时间。</summary>
    public async Task TouchLastRunAsync(int id, DateTime runAt, CancellationToken token)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        await using var command = new SqlCommand(
            "UPDATE dbo.REPORT_SUBSCRIPTION SET LAST_RUN_AT=@RunAt WHERE ID=@Id;", connection);
        command.Parameters.Add("@RunAt", SqlDbType.DateTime2).Value = runAt;
        command.Parameters.Add("@Id", SqlDbType.Int).Value = id;
        await command.ExecuteNonQueryAsync(token);
    }

    /// <summary>Inbox 根目录（相对路径解析为绝对路径）。</summary>
    public string ResolveInboxPath(string relative) =>
        Path.Combine(settings.Value.StorageRoot ?? Path.Combine(AppContext.BaseDirectory, "report-inbox"), relative);

    /// <summary>订阅行数（用于调度节流/监控）。</summary>
    public async Task<int> CountSubscriptionsAsync(CancellationToken token)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        await using var command = new SqlCommand("SELECT COUNT(*) FROM dbo.REPORT_SUBSCRIPTION WITH (NOLOCK);", connection);
        return Convert.ToInt32(await command.ExecuteScalarAsync(token));
    }

    private static void AddSubscriptionParameters(
        SqlCommand command, string userId, int moduleId, string reportId, string scheduleType,
        int runHour, int runMinute, int? weekday, int? monthDay, bool enabled, string user)
    {
        command.Parameters.Add("@UserId", SqlDbType.NChar, 10).Value = userId.Trim();
        if (moduleId > 0) command.Parameters.Add("@ModuleId", SqlDbType.Int).Value = moduleId;
        command.Parameters.Add("@ReportId", SqlDbType.NChar, 50).Value = reportId.Trim();
        command.Parameters.Add("@ScheduleType", SqlDbType.NChar, 10).Value = scheduleType.Trim().ToUpperInvariant();
        command.Parameters.Add("@RunHour", SqlDbType.Int).Value = Math.Clamp(runHour, 0, 23);
        command.Parameters.Add("@RunMinute", SqlDbType.Int).Value = Math.Clamp(runMinute, 0, 59);
        command.Parameters.Add("@Weekday", SqlDbType.Int).Value = weekday ?? (object)DBNull.Value;
        command.Parameters.Add("@MonthDay", SqlDbType.Int).Value = monthDay ?? (object)DBNull.Value;
        command.Parameters.Add("@Enabled", SqlDbType.Bit).Value = enabled;
        command.Parameters.Add("@User", SqlDbType.NChar, 40).Value = user.Trim();
    }
}

/// <summary>报表中心 Inbox 配置。</summary>
public sealed class ReportInboxSettings
{
    public string? StorageRoot { get; set; }
    /// <summary>调度扫描间隔（秒，默认 300）。</summary>
    public int ScanIntervalSeconds { get; set; } = 300;
}
