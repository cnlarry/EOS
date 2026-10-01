using Microsoft.Data.SqlClient;

namespace EOS.API.Data;

/// <summary>
/// <c>dbo.ASSISTANT_MODEL</c> 的一行。
///
/// <para>
/// **这一行里没有密钥**：<see cref="ApiKeyEnvVar"/> 是环境变量**名**，密钥本体只存在于进程/用户
/// 环境变量里（见 <c>IAssistantSecretStore</c>）。所以这张表的任何导出、备份、诊断包都不含密钥。
/// </para>
/// </summary>
public sealed record AssistantModelRow(
    int ModelId,
    string DisplayName,
    string Provider,
    string ModelName,
    string BaseUrl,
    string ApiKeyEnvVar,
    int TimeoutSeconds,
    decimal? Temperature,
    int? MaxTokens,
    bool IsActive,
    bool Enabled,
    int SortIdx,
    string? Remark,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>新增 / 修改模型时的可写字段（不含"是否为当前模型"，那是单独的动作）。</summary>
public sealed record AssistantModelWrite(
    string DisplayName,
    string Provider,
    string ModelName,
    string BaseUrl,
    string ApiKeyEnvVar,
    int TimeoutSeconds,
    decimal? Temperature,
    int? MaxTokens,
    bool Enabled,
    int SortIdx,
    string? Remark);

/// <summary>
/// 助手模型表的读写（菜单组 31 / 模块 3102，见 ADR-030 §3）。
///
/// <para>
/// 这里**不碰密钥**：只读写"用哪个环境变量"。把密钥与配置分开存，才能做到"数据库里没有密钥"。
/// </para>
/// </summary>
public interface IAssistantModelCatalog
{
    /// <summary>列出全部模型（管理页用；按排序号、主键稳定排列）。</summary>
    Task<IReadOnlyList<AssistantModelRow>> ListAsync(CancellationToken token);

    Task<AssistantModelRow?> GetAsync(int modelId, CancellationToken token);

    /// <summary>当前生效的模型（至多一行，见筛选唯一索引）；表为空或没启用的行时为 null。</summary>
    Task<AssistantModelRow?> GetActiveAsync(CancellationToken token);

    /// <summary>新增，返回新行主键。</summary>
    Task<int> CreateAsync(AssistantModelWrite write, string actor, CancellationToken token);

    /// <summary>修改（不含密钥与"是否当前"）。返回是否命中行。</summary>
    Task<bool> UpdateAsync(int modelId, AssistantModelWrite write, string actor, CancellationToken token);

    /// <summary>删除。**当前模型删不掉**（`IS_ACTIVE = 1` 的行受影响行数为 0）。</summary>
    Task<bool> DeleteAsync(int modelId, CancellationToken token);

    /// <summary>设为当前模型：先清空其他行的"当前"，再置位这一行；只接受**已启用**的行。</summary>
    Task<bool> ActivateAsync(int modelId, string actor, CancellationToken token);

    /// <summary>取消当前（回到"用配置文件里的模型"）。</summary>
    Task ClearActiveAsync(CancellationToken token);
}

/// <summary>模型表的 SQL Server 实现。</summary>
public sealed class AssistantModelCatalog(DbConnectionFactory connections) : IAssistantModelCatalog
{
    private const string SelectColumns = """
        MODEL_ID, DISPLAY_NAME, PROVIDER, MODEL_NAME, BASE_URL, API_KEY_ENV_VAR,
        TIMEOUT_SECONDS, TEMPERATURE, MAX_TOKENS, IS_ACTIVE, ENABLED, SORT_IDX, REMARK,
        CREATED_AT, UPDATED_AT
        """;

    /// <inheritdoc />
    public async Task<IReadOnlyList<AssistantModelRow>> ListAsync(CancellationToken token)
    {
        var sql = $"SELECT {SelectColumns} FROM dbo.ASSISTANT_MODEL WITH (NOLOCK) ORDER BY IS_ACTIVE DESC, SORT_IDX, MODEL_ID;";
        var items = new List<AssistantModelRow>();
        await using var conn = connections.Create();
        await conn.OpenAsync(token);
        await using var cmd = new SqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            items.Add(Read(reader));
        }

        return items;
    }

    /// <inheritdoc />
    public async Task<AssistantModelRow?> GetAsync(int modelId, CancellationToken token)
    {
        var sql = $"SELECT {SelectColumns} FROM dbo.ASSISTANT_MODEL WHERE MODEL_ID = @Id;";
        await using var conn = connections.Create();
        await conn.OpenAsync(token);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@Id", modelId);
        await using var reader = await cmd.ExecuteReaderAsync(token);
        return await reader.ReadAsync(token) ? Read(reader) : null;
    }

    /// <inheritdoc />
    public async Task<AssistantModelRow?> GetActiveAsync(CancellationToken token)
    {
        // 筛选唯一索引保证至多一行，但查询仍写 TOP (1)：即使索引哪天被改掉，这里也不会因为多行而抛错
        var sql = $"""
            SELECT TOP (1) {SelectColumns}
            FROM dbo.ASSISTANT_MODEL
            WHERE IS_ACTIVE = 1 AND ENABLED = 1
            ORDER BY MODEL_ID;
            """;
        await using var conn = connections.Create();
        await conn.OpenAsync(token);
        await using var cmd = new SqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(token);
        return await reader.ReadAsync(token) ? Read(reader) : null;
    }

    /// <inheritdoc />
    public async Task<int> CreateAsync(AssistantModelWrite write, string actor, CancellationToken token)
    {
        const string sql = """
            INSERT INTO dbo.ASSISTANT_MODEL
                (DISPLAY_NAME, PROVIDER, MODEL_NAME, BASE_URL, API_KEY_ENV_VAR, TIMEOUT_SECONDS,
                 TEMPERATURE, MAX_TOKENS, IS_ACTIVE, ENABLED, SORT_IDX, REMARK, CREATED_BY, UPDATED_BY)
            OUTPUT INSERTED.MODEL_ID
            VALUES
                (@DisplayName, @Provider, @ModelName, @BaseUrl, @EnvVar, @Timeout,
                 @Temperature, @MaxTokens, 0, @Enabled, @SortIdx, @Remark, @Actor, @Actor);
            """;
        await using var conn = connections.Create();
        await conn.OpenAsync(token);
        await using var cmd = new SqlCommand(sql, conn);
        Bind(cmd, write);
        cmd.Parameters.AddWithValue("@Actor", actor);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(token));
    }

    /// <inheritdoc />
    public async Task<bool> UpdateAsync(int modelId, AssistantModelWrite write, string actor, CancellationToken token)
    {
        const string sql = """
            UPDATE dbo.ASSISTANT_MODEL
            SET DISPLAY_NAME = @DisplayName, PROVIDER = @Provider, MODEL_NAME = @ModelName,
                BASE_URL = @BaseUrl, API_KEY_ENV_VAR = @EnvVar, TIMEOUT_SECONDS = @Timeout,
                TEMPERATURE = @Temperature, MAX_TOKENS = @MaxTokens, ENABLED = @Enabled,
                SORT_IDX = @SortIdx, REMARK = @Remark, UPDATED_AT = SYSUTCDATETIME(), UPDATED_BY = @Actor
            WHERE MODEL_ID = @Id;
            """;
        await using var conn = connections.Create();
        await conn.OpenAsync(token);
        await using var cmd = new SqlCommand(sql, conn);
        Bind(cmd, write);
        cmd.Parameters.AddWithValue("@Actor", actor);
        cmd.Parameters.AddWithValue("@Id", modelId);
        return await cmd.ExecuteNonQueryAsync(token) > 0;
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(int modelId, CancellationToken token)
    {
        // 当前模型不给删：删掉它会让助手在无人察觉的情况下退回配置文件里的模型
        const string sql = "DELETE FROM dbo.ASSISTANT_MODEL WHERE MODEL_ID = @Id AND IS_ACTIVE = 0;";
        await using var conn = connections.Create();
        await conn.OpenAsync(token);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@Id", modelId);
        return await cmd.ExecuteNonQueryAsync(token) > 0;
    }

    /// <inheritdoc />
    public async Task<bool> ActivateAsync(int modelId, string actor, CancellationToken token)
    {
        // 一个事务里"先清后置"：这样中途失败不会留下两条当前模型
        await using var conn = connections.Create();
        await conn.OpenAsync(token);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(token);

        await using (var clear = new SqlCommand(
            "UPDATE dbo.ASSISTANT_MODEL SET IS_ACTIVE = 0 WHERE IS_ACTIVE = 1;", conn, tx))
        {
            await clear.ExecuteNonQueryAsync(token);
        }

        int affected;
        await using (var set = new SqlCommand("""
            UPDATE dbo.ASSISTANT_MODEL
            SET IS_ACTIVE = 1, UPDATED_AT = SYSUTCDATETIME(), UPDATED_BY = @Actor
            WHERE MODEL_ID = @Id AND ENABLED = 1;
            """, conn, tx))
        {
            set.Parameters.AddWithValue("@Id", modelId);
            set.Parameters.AddWithValue("@Actor", actor);
            affected = await set.ExecuteNonQueryAsync(token);
        }

        await tx.CommitAsync(token);
        return affected > 0;
    }

    /// <inheritdoc />
    public async Task ClearActiveAsync(CancellationToken token)
    {
        await using var conn = connections.Create();
        await conn.OpenAsync(token);
        await using var cmd = new SqlCommand(
            "UPDATE dbo.ASSISTANT_MODEL SET IS_ACTIVE = 0 WHERE IS_ACTIVE = 1;", conn);
        await cmd.ExecuteNonQueryAsync(token);
    }

    private static void Bind(SqlCommand cmd, AssistantModelWrite write)
    {
        cmd.Parameters.AddWithValue("@DisplayName", write.DisplayName);
        cmd.Parameters.AddWithValue("@Provider", write.Provider);
        cmd.Parameters.AddWithValue("@ModelName", write.ModelName);
        cmd.Parameters.AddWithValue("@BaseUrl", write.BaseUrl);
        cmd.Parameters.AddWithValue("@EnvVar", write.ApiKeyEnvVar);
        cmd.Parameters.AddWithValue("@Timeout", write.TimeoutSeconds);
        cmd.Parameters.AddWithValue("@Temperature", (object?)write.Temperature ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@MaxTokens", (object?)write.MaxTokens ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Enabled", write.Enabled ? 1 : 0);
        cmd.Parameters.AddWithValue("@SortIdx", write.SortIdx);
        cmd.Parameters.AddWithValue("@Remark", (object?)write.Remark ?? DBNull.Value);
    }

    private static AssistantModelRow Read(SqlDataReader reader) => new(
        reader.GetInt32(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetString(3),
        reader.GetString(4),
        reader.GetString(5),
        reader.GetInt32(6),
        reader.IsDBNull(7) ? null : reader.GetDecimal(7),
        reader.IsDBNull(8) ? null : reader.GetInt32(8),
        reader.GetBoolean(9),
        reader.GetBoolean(10),
        reader.GetInt32(11),
        reader.IsDBNull(12) ? null : reader.GetString(12),
        new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(13), DateTimeKind.Utc)),
        new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(14), DateTimeKind.Utc)));
}
