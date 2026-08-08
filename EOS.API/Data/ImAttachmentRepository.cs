using System.Data;
using EOS.API.Models;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data;

/// <summary>附件仓储：二进制入库（方案 B 下随消息暂存清理策略管理）。</summary>
public sealed class ImAttachmentRepository(DbConnectionFactory connections) : IImAttachmentRepository
{
    public async Task<ImAttachmentDto?> UploadAsync(
        long conversationId,
        string uploaderUserId,
        string fileName,
        string contentType,
        byte[] content,
        string sha256,
        CancellationToken token)
    {
        const string sql = """
            INSERT INTO dbo.im_attachments
                (ConversationId, UploadedByUserId, FileName, ContentType, SizeBytes, Content, Sha256, UploadedAt)
            OUTPUT inserted.Id, inserted.UploadedAt
            VALUES (@ConversationId, @Uploader, @FileName, @ContentType, @SizeBytes, @Content, @Sha256, SYSUTCDATETIME());
            """;
        await using var connection = connections.CreateIm();
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@ConversationId", SqlDbType.BigInt).Value = conversationId;
        command.Parameters.Add("@Uploader", SqlDbType.NVarChar, 64).Value = uploaderUserId.Trim();
        command.Parameters.Add("@FileName", SqlDbType.NVarChar, 255).Value = fileName;
        command.Parameters.Add("@ContentType", SqlDbType.NVarChar, 128).Value = contentType;
        command.Parameters.Add("@SizeBytes", SqlDbType.BigInt).Value = content.LongLength;
        command.Parameters.Add("@Content", SqlDbType.VarBinary).Value = content;
        command.Parameters.Add("@Sha256", SqlDbType.Char, 64).Value = sha256;
        await connection.OpenAsync(token);
        long id;
        DateTime uploadedAt;
        await using (var reader = await command.ExecuteReaderAsync(token))
        {
            if (!await reader.ReadAsync(token))
            {
                return null;
            }

            id = reader.GetInt64(0);
            uploadedAt = reader.GetDateTime(1);
        }

        return new ImAttachmentDto(
            id, conversationId, uploaderUserId.Trim(), fileName, contentType, content.LongLength, sha256,
            new DateTimeOffset(DateTime.SpecifyKind(uploadedAt, DateTimeKind.Utc)));
    }

    public async Task<ImAttachmentDto?> GetAsync(long fileId, CancellationToken token)
    {
        const string sql = """
            SELECT Id, ConversationId, UploadedByUserId, FileName, ContentType, SizeBytes, Sha256, UploadedAt
            FROM dbo.im_attachments
            WHERE Id = @FileId;
            """;
        await using var connection = connections.CreateIm();
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@FileId", SqlDbType.BigInt).Value = fileId;
        await connection.OpenAsync(token);
        await using var reader = await command.ExecuteReaderAsync(token);
        return await reader.ReadAsync(token) ? ReadMeta(reader) : null;
    }

    public async Task<ImAttachmentFile?> GetFileAsync(long fileId, CancellationToken token)
    {
        const string sql = """
            SELECT Id, ConversationId, UploadedByUserId, FileName, ContentType, SizeBytes, Sha256, UploadedAt, Content
            FROM dbo.im_attachments
            WHERE Id = @FileId;
            """;
        await using var connection = connections.CreateIm();
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@FileId", SqlDbType.BigInt).Value = fileId;
        await connection.OpenAsync(token);
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token))
        {
            return null;
        }

        var meta = ReadMeta(reader);
        var content = (byte[])reader.GetValue(reader.GetOrdinal("Content"));
        return new ImAttachmentFile(meta, content);
    }

    public async Task<bool> LinkToMessageAsync(long fileId, long messageId, CancellationToken token)
    {
        const string sql = """
            UPDATE dbo.im_attachments
            SET MessageId = @MessageId
            WHERE Id = @FileId AND MessageId IS NULL;
            """;
        await using var connection = connections.CreateIm();
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@FileId", SqlDbType.BigInt).Value = fileId;
        command.Parameters.Add("@MessageId", SqlDbType.BigInt).Value = messageId;
        await connection.OpenAsync(token);
        return await command.ExecuteNonQueryAsync(token) == 1;
    }

    private static ImAttachmentDto ReadMeta(SqlDataReader reader)
        => new(
            reader.GetInt64("Id"),
            reader.GetInt64("ConversationId"),
            reader.GetString("UploadedByUserId").Trim(),
            reader.GetString("FileName"),
            reader.GetString("ContentType"),
            reader.GetInt64("SizeBytes"),
            reader.GetString("Sha256").Trim(),
            new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime("UploadedAt"), DateTimeKind.Utc)));
}
