using System.Data;
using EOS.API.Models;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data;

/// <summary>
/// 邮件待办仓储（EOS.Mail 独立库）。所有查询强制按 UserId 过滤：
/// UserId 一律由 EOS.API 从登录会话读取后传入，不接受客户端提交；
/// 任何人只能读写自己的任务。表结构见 Data/MailMigrations/001_mail_tasks.sql。
/// </summary>
public sealed class MailTaskRepository(DbConnectionFactory connections)
{
    private const string SelectList = """
        Id, Title, Description, SourceKind, SourceMailbox, SourceMessageId, SourceSubject,
        DueDate, Priority, Status, CreatedAt, CompletedAt, UpdatedAt
        """;

    private const string OutputList = """
        INSERTED.Id, INSERTED.Title, INSERTED.Description, INSERTED.SourceKind, INSERTED.SourceMailbox,
        INSERTED.SourceMessageId, INSERTED.SourceSubject, INSERTED.DueDate, INSERTED.Priority,
        INSERTED.Status, INSERTED.CreatedAt, INSERTED.CompletedAt, INSERTED.UpdatedAt
        """;

    public async Task<MailTaskDto> CreateAsync(
        string userId, MailTaskCreateRequest input, CancellationToken token)
    {
        await using var connection = connections.CreateMail();
        await connection.OpenAsync(token);
        await using var command = new SqlCommand($"""
            INSERT INTO dbo.mail_tasks
                (UserId, Title, Description, SourceKind, SourceMailbox, SourceMessageId,
                 SourceSubject, DueDate, Priority, Status)
            OUTPUT {OutputList}
            VALUES (@UserId, @Title, @Description, N'email', @SourceMailbox, @SourceMessageId,
                    @SourceSubject, @DueDate, @Priority, N'open');
            """, connection);
        AddParameter(command, "@UserId", SqlDbType.NVarChar, 64, userId);
        AddParameter(command, "@Title", SqlDbType.NVarChar, 200, input.Title.Trim());
        AddParameter(command, "@Description", SqlDbType.NVarChar, 1000, input.Description);
        AddParameter(command, "@SourceMailbox", SqlDbType.NVarChar, 255, input.SourceMailbox);
        AddParameter(command, "@SourceMessageId", SqlDbType.NVarChar, 255, input.SourceMessageId);
        AddParameter(command, "@SourceSubject", SqlDbType.NVarChar, 255, input.SourceSubject);
        AddParameter(command, "@DueDate", SqlDbType.DateTime2, null, input.DueDate?.UtcDateTime);
        AddParameter(command, "@Priority", SqlDbType.TinyInt, null, input.Priority);
        await using var reader = await command.ExecuteReaderAsync(token);
        await reader.ReadAsync(token);
        return ReadMailTask(reader);
    }

    /// <summary>列出当前用户任务。status 为 null 表示全部，否则 open/done/cancelled。</summary>
    public async Task<MailTaskListDto> ListMineAsync(
        string userId, string? status, int limit, int offset, CancellationToken token)
    {
        await using var connection = connections.CreateMail();
        await connection.OpenAsync(token);

        await using var countCommand = new SqlCommand("""
            SELECT COUNT_BIG(1) FROM dbo.mail_tasks
            WHERE UserId = @UserId AND (@Status IS NULL OR Status = @Status);
            """, connection);
        AddParameter(countCommand, "@UserId", SqlDbType.NVarChar, 64, userId);
        AddParameter(countCommand, "@Status", SqlDbType.NVarChar, 16, status);
        var total = Convert.ToInt32(await countCommand.ExecuteScalarAsync(token));

        await using var listCommand = new SqlCommand($"""
            SELECT {SelectList} FROM dbo.mail_tasks
            WHERE UserId = @UserId AND (@Status IS NULL OR Status = @Status)
            ORDER BY CASE Status WHEN N'open' THEN 0 ELSE 1 END, CreatedAt DESC
            OFFSET @Offset ROWS FETCH NEXT @Limit ROWS ONLY;
            """, connection);
        AddParameter(listCommand, "@UserId", SqlDbType.NVarChar, 64, userId);
        AddParameter(listCommand, "@Status", SqlDbType.NVarChar, 16, status);
        AddParameter(listCommand, "@Offset", SqlDbType.Int, null, offset);
        AddParameter(listCommand, "@Limit", SqlDbType.Int, null, limit);

        var tasks = new List<MailTaskDto>();
        await using (var reader = await listCommand.ExecuteReaderAsync(token))
        {
            while (await reader.ReadAsync(token))
            {
                tasks.Add(ReadMailTask(reader));
            }
        }

        return new MailTaskListDto(tasks, total, limit, offset);
    }

    public async Task<MailTaskDto?> GetMineAsync(string userId, long id, CancellationToken token)
    {
        await using var connection = connections.CreateMail();
        await connection.OpenAsync(token);
        await using var command = new SqlCommand($"""
            SELECT {SelectList} FROM dbo.mail_tasks
            WHERE Id = @Id AND UserId = @UserId;
            """, connection);
        AddParameter(command, "@Id", SqlDbType.BigInt, null, id);
        AddParameter(command, "@UserId", SqlDbType.NVarChar, 64, userId);
        await using var reader = await command.ExecuteReaderAsync(token);
        return await reader.ReadAsync(token) ? ReadMailTask(reader) : null;
    }

    public async Task<MailTaskDto?> CompleteAsync(string userId, long id, CancellationToken token)
    {
        await using var connection = connections.CreateMail();
        await connection.OpenAsync(token);
        await using var command = new SqlCommand($"""
            UPDATE dbo.mail_tasks
            SET Status = N'done', CompletedAt = SYSUTCDATETIME(), UpdatedAt = SYSUTCDATETIME()
            OUTPUT {OutputList}
            WHERE Id = @Id AND UserId = @UserId AND Status = N'open';
            """, connection);
        AddParameter(command, "@Id", SqlDbType.BigInt, null, id);
        AddParameter(command, "@UserId", SqlDbType.NVarChar, 64, userId);
        return await ExecuteSingleAsync(command, token);
    }

    public async Task<MailTaskDto?> ReopenAsync(string userId, long id, CancellationToken token)
    {
        await using var connection = connections.CreateMail();
        await connection.OpenAsync(token);
        await using var command = new SqlCommand($"""
            UPDATE dbo.mail_tasks
            SET Status = N'open', CompletedAt = NULL, UpdatedAt = SYSUTCDATETIME()
            OUTPUT {OutputList}
            WHERE Id = @Id AND UserId = @UserId AND Status = N'done';
            """, connection);
        AddParameter(command, "@Id", SqlDbType.BigInt, null, id);
        AddParameter(command, "@UserId", SqlDbType.NVarChar, 64, userId);
        return await ExecuteSingleAsync(command, token);
    }

    public async Task<MailTaskDto?> CancelAsync(string userId, long id, CancellationToken token)
    {
        await using var connection = connections.CreateMail();
        await connection.OpenAsync(token);
        await using var command = new SqlCommand($"""
            UPDATE dbo.mail_tasks
            SET Status = N'cancelled', CompletedAt = SYSUTCDATETIME(), UpdatedAt = SYSUTCDATETIME()
            OUTPUT {OutputList}
            WHERE Id = @Id AND UserId = @UserId AND Status <> N'cancelled';
            """, connection);
        AddParameter(command, "@Id", SqlDbType.BigInt, null, id);
        AddParameter(command, "@UserId", SqlDbType.NVarChar, 64, userId);
        return await ExecuteSingleAsync(command, token);
    }

    public async Task<MailTaskDto?> UpdateAsync(
        string userId, long id, MailTaskUpdateRequest input, CancellationToken token)
    {
        await using var connection = connections.CreateMail();
        await connection.OpenAsync(token);
        await using var command = new SqlCommand($"""
            UPDATE dbo.mail_tasks
            SET Title = COALESCE(@Title, Title),
                Description = COALESCE(@Description, Description),
                DueDate = CASE WHEN @ClearDueDate = 1 THEN NULL
                               WHEN @DueDate IS NOT NULL THEN @DueDate
                               ELSE DueDate END,
                Priority = COALESCE(@Priority, Priority),
                UpdatedAt = SYSUTCDATETIME()
            OUTPUT {OutputList}
            WHERE Id = @Id AND UserId = @UserId;
            """, connection);
        AddParameter(command, "@Id", SqlDbType.BigInt, null, id);
        AddParameter(command, "@UserId", SqlDbType.NVarChar, 64, userId);
        AddParameter(command, "@Title", SqlDbType.NVarChar, 200, input.Title?.Trim());
        AddParameter(command, "@Description", SqlDbType.NVarChar, 1000, input.Description);
        AddParameter(command, "@DueDate", SqlDbType.DateTime2, null, input.DueDate?.UtcDateTime);
        AddParameter(command, "@ClearDueDate", SqlDbType.Bit, null, input.ClearDueDate);
        AddParameter(command, "@Priority", SqlDbType.TinyInt, null, input.Priority);
        return await ExecuteSingleAsync(command, token);
    }

    private static async Task<MailTaskDto?> ExecuteSingleAsync(SqlCommand command, CancellationToken token)
    {
        await using var reader = await command.ExecuteReaderAsync(token);
        return await reader.ReadAsync(token) ? ReadMailTask(reader) : null;
    }

    private static MailTaskDto ReadMailTask(SqlDataReader reader) => new(
        reader.GetInt64(0),
        reader.GetString(1),
        reader.IsDBNull(2) ? null : reader.GetString(2),
        reader.GetString(3),
        reader.IsDBNull(4) ? null : reader.GetString(4),
        reader.IsDBNull(5) ? null : reader.GetString(5),
        reader.IsDBNull(6) ? null : reader.GetString(6),
        reader.IsDBNull(7) ? null : DateTime.SpecifyKind(reader.GetDateTime(7), DateTimeKind.Utc),
        reader.GetByte(8),
        reader.GetString(9),
        DateTime.SpecifyKind(reader.GetDateTime(10), DateTimeKind.Utc),
        reader.IsDBNull(11) ? null : DateTime.SpecifyKind(reader.GetDateTime(11), DateTimeKind.Utc),
        DateTime.SpecifyKind(reader.GetDateTime(12), DateTimeKind.Utc));

    private static void AddParameter(
        SqlCommand command, string name, SqlDbType type, int? size, object? value)
    {
        var parameter = new SqlParameter(name, type) { Value = value ?? DBNull.Value };
        if (size is not null)
        {
            parameter.Size = size.Value;
        }

        command.Parameters.Add(parameter);
    }
}
