using System.Data;
using System.Text.RegularExpressions;
using EOS.API.Models;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace EOS.API.Data;

/// <summary>
/// 业务单据附件仓储（表 dbo.ATTACHMENT，位于 EOS.ERP —— 全本系统的唯一业务数据库）。
/// 元数据 + SHA-256 入库，文件二进制存文件系统（Attachment:StorageRoot，默认 AppContext.BaseDirectory/attachments），
/// 本仓储只负责元数据。定位：统一表单（DocumentWorkbench）单据级附件，
/// 按 M_IDX + MASTER_TABLE + KEY_VALUES(JSON) + SERIAL_NO 定位。
/// 权限由控制器按 FILE_VIEW/UPDA/EDIT/DELE_TAG 服务端强制校验，本仓储不做权限判断。
/// 表结构见 Data/Migrations/001_attachments.sql（对象名全大写，遵守 AGENTS.md 命名规范）。
/// </summary>
public sealed class AttachmentRepository(
    DbConnectionFactory connections,
    IOptions<AttachmentSettings> settings)
{
    private const string SelectList = """
        ID, M_IDX, MASTER_TABLE, KEY_VALUES, SERIAL_NO, FILE_NAME, CLIENT_FILE_NAME, CONTENT_TYPE,
        SIZE_BYTES, SHA256, REMARK, UPLOADED_BY, UPLOADED_BY_DISPLAY, UPLOADED_AT
        """;

    private const string OutputList = """
        INSERTED.ID, INSERTED.M_IDX, INSERTED.MASTER_TABLE, INSERTED.KEY_VALUES, INSERTED.SERIAL_NO,
        INSERTED.FILE_NAME, INSERTED.CLIENT_FILE_NAME, INSERTED.CONTENT_TYPE, INSERTED.SIZE_BYTES,
        INSERTED.SHA256, INSERTED.REMARK, INSERTED.UPLOADED_BY, INSERTED.UPLOADED_BY_DISPLAY, INSERTED.UPLOADED_AT
        """;

    /// <summary>取同一单据下一个附件项次（SERIAL_NO，1 起自增）。</summary>
    public async Task<int> GetNextSerialAsync(
        int moduleId, string masterTable, string keyValues, CancellationToken token)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        await using var command = new SqlCommand("""
            SELECT ISNULL(MAX(SERIAL_NO), 0) FROM dbo.ATTACHMENT
            WHERE M_IDX = @ModuleId AND MASTER_TABLE = @MasterTable AND KEY_VALUES = @KeyValues;
            """, connection);
        AddParameter(command, "@ModuleId", SqlDbType.Int, null, moduleId);
        AddParameter(command, "@MasterTable", SqlDbType.NVarChar, 128, masterTable);
        AddParameter(command, "@KeyValues", SqlDbType.NVarChar, 2000, keyValues);
        var max = Convert.ToInt32(await command.ExecuteScalarAsync(token));
        return max + 1;
    }

    public async Task<AttachmentDto> CreateAsync(
        int moduleId,
        string masterTable,
        string keyValues,
        int serialNo,
        string fileName,
        string clientFileName,
        string contentType,
        long sizeBytes,
        string sha256,
        string? remark,
        string uploadedBy,
        string? uploadedByDisplay,
        CancellationToken token)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        await using var command = new SqlCommand($"""
            INSERT INTO dbo.ATTACHMENT
                (M_IDX, MASTER_TABLE, KEY_VALUES, SERIAL_NO, FILE_NAME, CLIENT_FILE_NAME, CONTENT_TYPE,
                 SIZE_BYTES, SHA256, REMARK, UPLOADED_BY, UPLOADED_BY_DISPLAY, UPLOADED_AT)
            OUTPUT {OutputList}
            VALUES (@ModuleId, @MasterTable, @KeyValues, @SerialNo, @FileName, @ClientFileName, @ContentType,
                    @SizeBytes, @Sha256, @Remark, @UploadedBy, @UploadedByDisplay, SYSUTCDATETIME());
            """, connection);
        AddParameter(command, "@ModuleId", SqlDbType.Int, null, moduleId);
        AddParameter(command, "@MasterTable", SqlDbType.NVarChar, 128, masterTable);
        AddParameter(command, "@KeyValues", SqlDbType.NVarChar, 2000, keyValues);
        AddParameter(command, "@SerialNo", SqlDbType.Int, null, serialNo);
        AddParameter(command, "@FileName", SqlDbType.NVarChar, 255, fileName);
        AddParameter(command, "@ClientFileName", SqlDbType.NVarChar, 255, clientFileName);
        AddParameter(command, "@ContentType", SqlDbType.NVarChar, 128, contentType);
        AddParameter(command, "@SizeBytes", SqlDbType.BigInt, null, sizeBytes);
        AddParameter(command, "@Sha256", SqlDbType.Char, 64, sha256);
        AddParameter(command, "@Remark", SqlDbType.NVarChar, 500, remark);
        AddParameter(command, "@UploadedBy", SqlDbType.NVarChar, 64, uploadedBy.Trim());
        AddParameter(command, "@UploadedByDisplay", SqlDbType.NVarChar, 100, uploadedByDisplay?.Trim());
        await using var reader = await command.ExecuteReaderAsync(token);
        await reader.ReadAsync(token);
        return ReadAttachment(reader);
    }

    public async Task<IReadOnlyList<AttachmentDto>> ListAsync(
        int moduleId, string masterTable, string keyValues, CancellationToken token)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        await using var command = new SqlCommand($"""
            SELECT {SelectList} FROM dbo.ATTACHMENT
            WHERE M_IDX = @ModuleId AND MASTER_TABLE = @MasterTable AND KEY_VALUES = @KeyValues
            ORDER BY SERIAL_NO;
            """, connection);
        AddParameter(command, "@ModuleId", SqlDbType.Int, null, moduleId);
        AddParameter(command, "@MasterTable", SqlDbType.NVarChar, 128, masterTable);
        AddParameter(command, "@KeyValues", SqlDbType.NVarChar, 2000, keyValues);
        var result = new List<AttachmentDto>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            result.Add(ReadAttachment(reader));
        }

        return result;
    }

    public async Task<AttachmentDto?> GetAsync(long id, CancellationToken token)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        await using var command = new SqlCommand($"""
            SELECT {SelectList} FROM dbo.ATTACHMENT WHERE ID = @Id;
            """, connection);
        AddParameter(command, "@Id", SqlDbType.BigInt, null, id);
        await using var reader = await command.ExecuteReaderAsync(token);
        return await reader.ReadAsync(token) ? ReadAttachment(reader) : null;
    }

    public async Task<AttachmentDto?> UpdateRemarkAsync(long id, string? remark, CancellationToken token)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        await using var command = new SqlCommand($"""
            UPDATE dbo.ATTACHMENT
            SET REMARK = @Remark
            OUTPUT {OutputList}
            WHERE ID = @Id;
            """, connection);
        AddParameter(command, "@Id", SqlDbType.BigInt, null, id);
        AddParameter(command, "@Remark", SqlDbType.NVarChar, 500, string.IsNullOrWhiteSpace(remark) ? null : remark.Trim());
        await using var reader = await command.ExecuteReaderAsync(token);
        return await reader.ReadAsync(token) ? ReadAttachment(reader) : null;
    }

    public async Task<AttachmentDto?> DeleteAsync(long id, CancellationToken token)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        await using var command = new SqlCommand($"""
            DELETE FROM dbo.ATTACHMENT
            OUTPUT DELETED.ID, DELETED.M_IDX, DELETED.MASTER_TABLE, DELETED.KEY_VALUES, DELETED.SERIAL_NO,
                   DELETED.FILE_NAME, DELETED.CLIENT_FILE_NAME, DELETED.CONTENT_TYPE, DELETED.SIZE_BYTES,
                   DELETED.SHA256, DELETED.REMARK, DELETED.UPLOADED_BY, DELETED.UPLOADED_BY_DISPLAY, DELETED.UPLOADED_AT
            WHERE ID = @Id;
            """, connection);
        AddParameter(command, "@Id", SqlDbType.BigInt, null, id);
        await using var reader = await command.ExecuteReaderAsync(token);
        return await reader.ReadAsync(token) ? ReadAttachment(reader) : null;
    }

    /// <summary>把元数据记录映射为控制器可用的存储路径（存文件系统的相对子路径）。</summary>
    public string ResolveRelativePath(AttachmentDto attachment)
    {
        // 目录 = {MasterTable}/{KeyHash}，文件名 = {SerialNo}{ext}，防路径穿越（服务端生成）。
        var tableDir = SanitizeSegment(attachment.MasterTable);
        var keyHash = SanitizeSegment(Sha256Short(attachment.KeyValues));
        return $"{tableDir}/{keyHash}/{attachment.SerialNo}{Path.GetExtension(attachment.FileName)}";
    }

    public string StorageRoot => settings.Value.StorageRoot;

    private static string Sha256Short(string value)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    private static string SanitizeSegment(string value)
    {
        var cleaned = Regex.Replace(value, "[^A-Za-z0-9_-]", "_");
        return cleaned.Length == 0 ? "_" : cleaned;
    }

    private static AttachmentDto ReadAttachment(SqlDataReader reader) => new(
        reader.GetInt64(0),
        reader.GetInt32(1),
        reader.GetString(2),
        reader.GetString(3),
        reader.GetInt32(4),
        reader.GetString(5),
        reader.GetString(6),
        reader.GetString(7),
        reader.GetInt64(8),
        reader.GetString(9).Trim(),
        reader.IsDBNull(10) ? null : reader.GetString(10),
        reader.GetString(11).Trim(),
        reader.IsDBNull(12) ? null : reader.GetString(12),
        DateTime.SpecifyKind(reader.GetDateTime(13), DateTimeKind.Utc));

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