using System.Data;
using System.Text.Json;
using EOS.API.Data;
using Microsoft.Data.SqlClient;

namespace EOS.API.Features.Import;

/// <summary>
/// 导入映射记忆：按（用户 + 模块）存一份"源列名 → 目标字段键"。
///
/// <para>
/// 映射是实施期的**资产**而不是一次性的临时状态：同一个客户、同一张表往往要导很多次，
/// 每次重配一遍是这个功能最容易被骂的地方。存的是列名而不是列下标——补导的文件常由 Excel
/// 重新另存，列顺序会变而列名不变。
/// </para>
///
/// <para>
/// 这是用户偏好级数据：不参与发布、不进统一表单、不写审计（写入者由 LAST_UPDATE_BY 留档）。
/// 读写在同一条参数化语句里完成，表名不来自任何输入。
/// </para>
/// </summary>
public sealed class ImportMappingStore(DbConnectionFactory connections)
{
    /// <summary>单次最多记住的列数：与 <see cref="ImportLimits.MaxColumns"/> 同量级，防呆而已。</summary>
    private const int MaxEntries = 500;

    private const string SelectSql = """
        SELECT TOP 1 LTRIM(RTRIM(ISNULL(SOURCE_NAME,''))), MAPPING_JSON, LAST_UPDATE_DATE,
               LTRIM(RTRIM(ISNULL(LAST_UPDATE_BY,'')))
        FROM dbo.IMPORT_MAPPING
        WHERE USER_ID = @UserId AND M_IDX = @ModuleId;
        """;

    /// <summary>
    /// 主键在（USER_ID, M_IDX）上，故用 MERGE + HOLDLOCK 做 Upsert：
    /// 两个标签页同时保存同一模块时，先到的持锁、后到的走 UPDATE 分支，不会撞出主键冲突。
    /// </summary>
    private const string UpsertSql = """
        SET NOCOUNT ON;
        MERGE dbo.IMPORT_MAPPING WITH (HOLDLOCK) AS target
        USING (SELECT @UserId AS USER_ID, @ModuleId AS M_IDX) AS source
           ON target.USER_ID = source.USER_ID AND target.M_IDX = source.M_IDX
        WHEN MATCHED THEN
            UPDATE SET SOURCE_NAME = @SourceName, MAPPING_JSON = @MappingJson,
                       LAST_UPDATE_BY = @UpdatedBy, LAST_UPDATE_DATE = SYSDATETIME()
        WHEN NOT MATCHED THEN
            INSERT (USER_ID, M_IDX, SOURCE_NAME, MAPPING_JSON, LAST_UPDATE_BY, LAST_UPDATE_DATE)
            VALUES (@UserId, @ModuleId, @SourceName, @MappingJson, @UpdatedBy, SYSDATETIME());
        """;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<ImportMappingSnapshot?> GetAsync(string userId, int moduleId, CancellationToken token)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        await using var command = new SqlCommand(SelectSql, connection);
        command.Parameters.Add("@UserId", SqlDbType.NVarChar, 100).Value = userId;
        command.Parameters.Add("@ModuleId", SqlDbType.Int).Value = moduleId;
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token)) return null;
        var sourceName = reader.GetString(0);
        var json = reader.GetString(1);
        // 经办人/日期列按全库约定可空（NULL = 还没发生过），故读侧要认 DBNull
        var updatedAt = reader.IsDBNull(2) ? (DateTime?)null : reader.GetDateTime(2);
        var updatedBy = reader.IsDBNull(3) ? string.Empty : reader.GetString(3);
        return new ImportMappingSnapshot(moduleId, sourceName, Deserialize(json), updatedAt, updatedBy);
    }

    public async Task SaveAsync(
        string userId,
        string updatedBy,
        int moduleId,
        ImportMappingSaveRequest request,
        CancellationToken token)
    {
        var entries = request.Entries
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Column))
            .Take(MaxEntries)
            .Select(entry => new ImportMappingEntry(
                entry.Column.Trim(),
                string.IsNullOrWhiteSpace(entry.Field) ? null : entry.Field.Trim()))
            .ToList();
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        await using var command = new SqlCommand(UpsertSql, connection);
        command.Parameters.Add("@UserId", SqlDbType.NVarChar, 100).Value = userId;
        command.Parameters.Add("@ModuleId", SqlDbType.Int).Value = moduleId;
        command.Parameters.Add("@SourceName", SqlDbType.NVarChar, 300).Value =
            Truncate(request.SourceName?.Trim() ?? string.Empty, 300);
        command.Parameters.Add("@MappingJson", SqlDbType.NVarChar, -1).Value =
            JsonSerializer.Serialize(entries, JsonOptions);
        command.Parameters.Add("@UpdatedBy", SqlDbType.NVarChar, 80).Value = Truncate(updatedBy, 80);
        await command.ExecuteNonQueryAsync(token);
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];

    /// <summary>存的是自己序列化的形状，但库里的行可能被人手改过——坏 JSON 一律当"没有映射"。</summary>
    private static IReadOnlyList<ImportMappingEntry> Deserialize(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<ImportMappingEntry>>(json, JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
