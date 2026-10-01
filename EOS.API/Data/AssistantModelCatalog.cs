using Microsoft.Data.SqlClient;

namespace EOS.API.Data;

/// <summary>
/// <c>dbo.ASSISTANT_PROVIDER</c> 的一行：一个接入点（端点 + 密钥环境变量名 + 默认超时）。
///
/// <para>
/// **密钥挂在这一级**——一个供应商一个端点、一把密钥，通吃它下面的所有模型。这正是现实的样子：
/// DeepSeek 的 Flash 与 Pro 共用同一个端点与同一把密钥，没有理由在模型行上重复填两遍。
/// </para>
///
/// <para>
/// <see cref="ApiKeyEnvVar"/> 存的是环境变量**名**，不是密钥（ADR-030 §3）。
/// </para>
/// </summary>
public sealed record AssistantProviderRow(
    int ProviderId,
    string Code,
    string DisplayName,
    string BaseUrl,
    string ApiKeyEnvVar,
    int TimeoutSeconds,
    bool Enabled,
    int SortIdx,
    string? Remark,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>新增 / 修改供应商的可写字段。</summary>
public sealed record AssistantProviderWrite(
    string Code,
    string DisplayName,
    string BaseUrl,
    string ApiKeyEnvVar,
    int TimeoutSeconds,
    bool Enabled,
    int SortIdx,
    string? Remark);

/// <summary>
/// <c>dbo.ASSISTANT_MODEL</c> 的一行：一个模型。
///
/// <para>
/// 窗口、最大输出、单价、工具能力这几列**都会被真的消费**（不是配了放着）：
/// 窗口用于推算能带多少历史、单价用于计费与限额、工具能力决定要不要带 <c>tools</c> 去请求。
/// </para>
/// </summary>
public sealed record AssistantModelRow(
    int ModelId,
    int ProviderId,
    string ModelCode,
    string DisplayName,
    int? ContextWindow,
    int? MaxOutputTokens,
    decimal? DefaultTemperature,
    int? TimeoutSeconds,
    decimal? InputPerMillionYuan,
    decimal? OutputPerMillionYuan,
    bool SupportsTools,
    bool IsActive,
    bool Enabled,
    int SortIdx,
    string? Remark,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>新增 / 修改模型的可写字段（不含"是否为当前"，那是单独的动作）。</summary>
public sealed record AssistantModelWrite(
    int ProviderId,
    string ModelCode,
    string DisplayName,
    int? ContextWindow,
    int? MaxOutputTokens,
    decimal? DefaultTemperature,
    int? TimeoutSeconds,
    decimal? InputPerMillionYuan,
    decimal? OutputPerMillionYuan,
    bool SupportsTools,
    bool Enabled,
    int SortIdx,
    string? Remark);

/// <summary>当前生效的模型 + 它所属的供应商。解析模型配置需要同时拿到这两级。</summary>
public sealed record AssistantActiveModel(AssistantModelRow Model, AssistantProviderRow Provider);

/// <summary>
/// 助手模型配置的读写（菜单组 31 / 模块 3102，见 ADR-030 §3）。
///
/// <para>
/// 两级：**供应商**（端点 + 密钥变量名 + 默认超时）与**模型**（模型标识 + 窗口 + 单价 + 能力）。
/// 这里**不碰密钥**，只读写"用哪个环境变量"——把密钥与配置分开存，才能做到"数据库里没有密钥"。
/// </para>
/// </summary>
public interface IAssistantModelCatalog
{
    // ---- 供应商 ----

    /// <summary>列供应商（按排序号、主键稳定排列）。</summary>
    Task<IReadOnlyList<AssistantProviderRow>> ListProvidersAsync(CancellationToken token);

    Task<AssistantProviderRow?> GetProviderAsync(int providerId, CancellationToken token);

    /// <summary>新增供应商，返回新行主键。</summary>
    Task<int> CreateProviderAsync(AssistantProviderWrite write, string actor, CancellationToken token);

    Task<bool> UpdateProviderAsync(int providerId, AssistantProviderWrite write, string actor, CancellationToken token);

    /// <summary>
    /// 删除供应商。**名下还有模型就删不掉**——级联删模型太容易误伤（一次手滑带走一整家供应商的配置）。
    /// </summary>
    Task<bool> DeleteProviderAsync(int providerId, CancellationToken token);

    /// <summary>某供应商名下有多少模型（用于删除前提示与校验）。</summary>
    Task<int> CountModelsAsync(int providerId, CancellationToken token);

    // ---- 模型 ----

    Task<IReadOnlyList<AssistantModelRow>> ListModelsAsync(CancellationToken token);

    Task<AssistantModelRow?> GetModelAsync(int modelId, CancellationToken token);

    /// <summary>
    /// 当前生效的模型（含其供应商）。至多一条（筛选唯一索引保证），且**供应商被停用则视同没有当前模型**。
    /// </summary>
    Task<AssistantActiveModel?> GetActiveAsync(CancellationToken token);

    Task<int> CreateModelAsync(AssistantModelWrite write, string actor, CancellationToken token);

    /// <summary>批量新增（界面上"从预设勾选几个模型一次添加"走这里），返回成功写入的条数。</summary>
    Task<int> CreateModelsAsync(IReadOnlyList<AssistantModelWrite> writes, string actor, CancellationToken token);

    Task<bool> UpdateModelAsync(int modelId, AssistantModelWrite write, string actor, CancellationToken token);

    /// <summary>删除模型。**当前模型删不掉**（<c>IS_ACTIVE = 1</c> 的行受影响行数为 0）。</summary>
    Task<bool> DeleteModelAsync(int modelId, CancellationToken token);

    /// <summary>设为当前模型：先清空其他行的"当前"，再置位这一行；只接受**已启用且所属供应商已启用**的行。</summary>
    Task<bool> ActivateModelAsync(int modelId, string actor, CancellationToken token);

    /// <summary>取消当前（回到"未配置模型"）。</summary>
    Task ClearActiveModelAsync(CancellationToken token);
}

/// <summary>模型配置两级的 SQL Server 实现。</summary>
public sealed class AssistantModelCatalog(DbConnectionFactory connections) : IAssistantModelCatalog
{
    private const string ProviderColumns = """
        PROVIDER_ID, CODE, DISPLAY_NAME, BASE_URL, API_KEY_ENV_VAR, TIMEOUT_SECONDS,
        ENABLED, SORT_IDX, REMARK, CREATED_AT, UPDATED_AT
        """;

    private const string ModelColumns = """
        MODEL_ID, PROVIDER_ID, MODEL_CODE, DISPLAY_NAME, CONTEXT_WINDOW, MAX_OUTPUT_TOKENS,
        DEFAULT_TEMPERATURE, TIMEOUT_SECONDS, INPUT_PER_MILLION_YUAN, OUTPUT_PER_MILLION_YUAN,
        SUPPORTS_TOOLS, IS_ACTIVE, ENABLED, SORT_IDX, REMARK, CREATED_AT, UPDATED_AT
        """;

    // 联查用的列清单写成两段**带别名的字面量**，而不是把上面的字面量做字符串替换：
    // 替换那种写法一旦列名或顺序变了就会悄悄拼出一句错的 SQL，而这里改错会立刻编译不过或查不出列。
    private const string ActiveModelColumns = """
        m.MODEL_ID, m.PROVIDER_ID, m.MODEL_CODE, m.DISPLAY_NAME, m.CONTEXT_WINDOW, m.MAX_OUTPUT_TOKENS,
        m.DEFAULT_TEMPERATURE, m.TIMEOUT_SECONDS, m.INPUT_PER_MILLION_YUAN, m.OUTPUT_PER_MILLION_YUAN,
        m.SUPPORTS_TOOLS, m.IS_ACTIVE, m.ENABLED, m.SORT_IDX, m.REMARK, m.CREATED_AT, m.UPDATED_AT
        """;

    private const string ActiveProviderColumns = """
        p.PROVIDER_ID, p.CODE, p.DISPLAY_NAME, p.BASE_URL, p.API_KEY_ENV_VAR, p.TIMEOUT_SECONDS,
        p.ENABLED, p.SORT_IDX, p.REMARK, p.CREATED_AT, p.UPDATED_AT
        """;

    // ==================================================================
    // 供应商
    // ==================================================================

    /// <inheritdoc />
    public async Task<IReadOnlyList<AssistantProviderRow>> ListProvidersAsync(CancellationToken token)
    {
        var sql = $"SELECT {ProviderColumns} FROM dbo.ASSISTANT_PROVIDER WITH (NOLOCK) ORDER BY SORT_IDX, PROVIDER_ID;";
        var items = new List<AssistantProviderRow>();
        await using var conn = connections.Create();
        await conn.OpenAsync(token);
        await using var cmd = new SqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            items.Add(ReadProvider(reader));
        }

        return items;
    }

    /// <inheritdoc />
    public async Task<AssistantProviderRow?> GetProviderAsync(int providerId, CancellationToken token)
    {
        var sql = $"SELECT {ProviderColumns} FROM dbo.ASSISTANT_PROVIDER WHERE PROVIDER_ID = @Id;";
        await using var conn = connections.Create();
        await conn.OpenAsync(token);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@Id", providerId);
        await using var reader = await cmd.ExecuteReaderAsync(token);
        return await reader.ReadAsync(token) ? ReadProvider(reader) : null;
    }

    /// <inheritdoc />
    public async Task<int> CreateProviderAsync(AssistantProviderWrite write, string actor, CancellationToken token)
    {
        const string sql = """
            INSERT INTO dbo.ASSISTANT_PROVIDER
                (CODE, DISPLAY_NAME, BASE_URL, API_KEY_ENV_VAR, TIMEOUT_SECONDS, ENABLED, SORT_IDX, REMARK, CREATED_BY, UPDATED_BY)
            OUTPUT INSERTED.PROVIDER_ID
            VALUES
                (@Code, @DisplayName, @BaseUrl, @EnvVar, @Timeout, @Enabled, @SortIdx, @Remark, @Actor, @Actor);
            """;
        await using var conn = connections.Create();
        await conn.OpenAsync(token);
        await using var cmd = new SqlCommand(sql, conn);
        BindProvider(cmd, write);
        cmd.Parameters.AddWithValue("@Actor", actor);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(token));
    }

    /// <inheritdoc />
    public async Task<bool> UpdateProviderAsync(int providerId, AssistantProviderWrite write, string actor, CancellationToken token)
    {
        const string sql = """
            UPDATE dbo.ASSISTANT_PROVIDER
            SET CODE = @Code, DISPLAY_NAME = @DisplayName, BASE_URL = @BaseUrl, API_KEY_ENV_VAR = @EnvVar,
                TIMEOUT_SECONDS = @Timeout, ENABLED = @Enabled, SORT_IDX = @SortIdx, REMARK = @Remark,
                UPDATED_AT = SYSUTCDATETIME(), UPDATED_BY = @Actor
            WHERE PROVIDER_ID = @Id;
            """;
        await using var conn = connections.Create();
        await conn.OpenAsync(token);
        await using var cmd = new SqlCommand(sql, conn);
        BindProvider(cmd, write);
        cmd.Parameters.AddWithValue("@Actor", actor);
        cmd.Parameters.AddWithValue("@Id", providerId);
        return await cmd.ExecuteNonQueryAsync(token) > 0;
    }

    /// <inheritdoc />
    public async Task<bool> DeleteProviderAsync(int providerId, CancellationToken token)
    {
        // 名下还有模型就不给删：级联会一次带走整家供应商的配置，手滑的代价太大
        const string sql = """
            DELETE FROM dbo.ASSISTANT_PROVIDER
            WHERE PROVIDER_ID = @Id
              AND NOT EXISTS (SELECT 1 FROM dbo.ASSISTANT_MODEL WHERE PROVIDER_ID = @Id);
            """;
        await using var conn = connections.Create();
        await conn.OpenAsync(token);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@Id", providerId);
        return await cmd.ExecuteNonQueryAsync(token) > 0;
    }

    /// <inheritdoc />
    public async Task<int> CountModelsAsync(int providerId, CancellationToken token)
    {
        await using var conn = connections.Create();
        await conn.OpenAsync(token);
        await using var cmd = new SqlCommand(
            "SELECT COUNT(*) FROM dbo.ASSISTANT_MODEL WITH (NOLOCK) WHERE PROVIDER_ID = @Id;", conn);
        cmd.Parameters.AddWithValue("@Id", providerId);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(token));
    }

    // ==================================================================
    // 模型
    // ==================================================================

    /// <inheritdoc />
    public async Task<IReadOnlyList<AssistantModelRow>> ListModelsAsync(CancellationToken token)
    {
        var sql = $"SELECT {ModelColumns} FROM dbo.ASSISTANT_MODEL WITH (NOLOCK) ORDER BY PROVIDER_ID, IS_ACTIVE DESC, SORT_IDX, MODEL_ID;";
        var items = new List<AssistantModelRow>();
        await using var conn = connections.Create();
        await conn.OpenAsync(token);
        await using var cmd = new SqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            items.Add(ReadModel(reader));
        }

        return items;
    }

    /// <inheritdoc />
    public async Task<AssistantModelRow?> GetModelAsync(int modelId, CancellationToken token)
    {
        var sql = $"SELECT {ModelColumns} FROM dbo.ASSISTANT_MODEL WHERE MODEL_ID = @Id;";
        await using var conn = connections.Create();
        await conn.OpenAsync(token);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@Id", modelId);
        await using var reader = await cmd.ExecuteReaderAsync(token);
        return await reader.ReadAsync(token) ? ReadModel(reader) : null;
    }

    /// <inheritdoc />
    public async Task<AssistantActiveModel?> GetActiveAsync(CancellationToken token)
    {
        // 供应商被停用 = 它的模型一律不可用：停用一家供应商应当立刻让整家下线，
        // 而不是要管理员再去逐个停用模型
        var sql = $"""
            SELECT TOP (1) {ActiveModelColumns}, {ActiveProviderColumns}
            FROM dbo.ASSISTANT_MODEL m
            INNER JOIN dbo.ASSISTANT_PROVIDER p ON p.PROVIDER_ID = m.PROVIDER_ID
            WHERE m.IS_ACTIVE = 1 AND m.ENABLED = 1 AND p.ENABLED = 1
            ORDER BY m.MODEL_ID;
            """;
        await using var conn = connections.Create();
        await conn.OpenAsync(token);
        await using var cmd = new SqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token))
        {
            return null;
        }

        var model = ReadModel(reader, 0);
        var provider = ReadProvider(reader, 17);
        return new AssistantActiveModel(model, provider);
    }

    /// <inheritdoc />
    public async Task<int> CreateModelAsync(AssistantModelWrite write, string actor, CancellationToken token)
    {
        await using var conn = connections.Create();
        await conn.OpenAsync(token);
        return await InsertModelAsync(conn, null, write, actor, token);
    }

    /// <inheritdoc />
    public async Task<int> CreateModelsAsync(IReadOnlyList<AssistantModelWrite> writes, string actor, CancellationToken token)
    {
        if (writes.Count == 0)
        {
            return 0;
        }

        // 一个事务：批量添加要么都进去要么都不进。勾了 5 个模型，结果只进去 3 个、
        // 还不知道是哪 3 个——那种状态比失败难查得多。
        await using var conn = connections.Create();
        await conn.OpenAsync(token);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(token);
        var created = 0;
        foreach (var write in writes)
        {
            // InsertModelAsync 返回的是**新主键**，不是条数：累加它会把 8+9+10 当成"插了 27 条"
            await InsertModelAsync(conn, tx, write, actor, token);
            created++;
        }

        await tx.CommitAsync(token);
        return created;
    }

    /// <inheritdoc />
    public async Task<bool> UpdateModelAsync(int modelId, AssistantModelWrite write, string actor, CancellationToken token)
    {
        const string sql = """
            UPDATE dbo.ASSISTANT_MODEL
            SET PROVIDER_ID = @ProviderId, MODEL_CODE = @ModelCode, DISPLAY_NAME = @DisplayName,
                CONTEXT_WINDOW = @ContextWindow, MAX_OUTPUT_TOKENS = @MaxOutput,
                DEFAULT_TEMPERATURE = @Temperature, TIMEOUT_SECONDS = @Timeout,
                INPUT_PER_MILLION_YUAN = @InputPrice, OUTPUT_PER_MILLION_YUAN = @OutputPrice,
                SUPPORTS_TOOLS = @SupportsTools, ENABLED = @Enabled, SORT_IDX = @SortIdx, REMARK = @Remark,
                UPDATED_AT = SYSUTCDATETIME(), UPDATED_BY = @Actor
            WHERE MODEL_ID = @Id;
            """;
        await using var conn = connections.Create();
        await conn.OpenAsync(token);
        await using var cmd = new SqlCommand(sql, conn);
        BindModel(cmd, write);
        cmd.Parameters.AddWithValue("@Actor", actor);
        cmd.Parameters.AddWithValue("@Id", modelId);
        return await cmd.ExecuteNonQueryAsync(token) > 0;
    }

    /// <inheritdoc />
    public async Task<bool> DeleteModelAsync(int modelId, CancellationToken token)
    {
        // 当前模型不给删：删掉它会让助手在无人察觉的情况下变成"未配置"
        const string sql = "DELETE FROM dbo.ASSISTANT_MODEL WHERE MODEL_ID = @Id AND IS_ACTIVE = 0;";
        await using var conn = connections.Create();
        await conn.OpenAsync(token);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@Id", modelId);
        return await cmd.ExecuteNonQueryAsync(token) > 0;
    }

    /// <inheritdoc />
    public async Task<bool> ActivateModelAsync(int modelId, string actor, CancellationToken token)
    {
        // 一个事务里"先清后置"：中途失败不会留下两条当前模型
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
            UPDATE m
            SET m.IS_ACTIVE = 1, m.UPDATED_AT = SYSUTCDATETIME(), m.UPDATED_BY = @Actor
            FROM dbo.ASSISTANT_MODEL m
            INNER JOIN dbo.ASSISTANT_PROVIDER p ON p.PROVIDER_ID = m.PROVIDER_ID
            WHERE m.MODEL_ID = @Id AND m.ENABLED = 1 AND p.ENABLED = 1;
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
    public async Task ClearActiveModelAsync(CancellationToken token)
    {
        await using var conn = connections.Create();
        await conn.OpenAsync(token);
        await using var cmd = new SqlCommand(
            "UPDATE dbo.ASSISTANT_MODEL SET IS_ACTIVE = 0 WHERE IS_ACTIVE = 1;", conn);
        await cmd.ExecuteNonQueryAsync(token);
    }

    // ==================================================================
    // 私有
    // ==================================================================

    private static async Task<int> InsertModelAsync(
        SqlConnection conn, SqlTransaction? tx, AssistantModelWrite write, string actor, CancellationToken token)
    {
        const string sql = """
            INSERT INTO dbo.ASSISTANT_MODEL
                (PROVIDER_ID, MODEL_CODE, DISPLAY_NAME, CONTEXT_WINDOW, MAX_OUTPUT_TOKENS,
                 DEFAULT_TEMPERATURE, TIMEOUT_SECONDS, INPUT_PER_MILLION_YUAN, OUTPUT_PER_MILLION_YUAN,
                 SUPPORTS_TOOLS, IS_ACTIVE, ENABLED, SORT_IDX, REMARK, CREATED_BY, UPDATED_BY)
            OUTPUT INSERTED.MODEL_ID
            VALUES
                (@ProviderId, @ModelCode, @DisplayName, @ContextWindow, @MaxOutput,
                 @Temperature, @Timeout, @InputPrice, @OutputPrice,
                 @SupportsTools, 0, @Enabled, @SortIdx, @Remark, @Actor, @Actor);
            """;
        await using var cmd = tx is null ? new SqlCommand(sql, conn) : new SqlCommand(sql, conn, tx);
        BindModel(cmd, write);
        cmd.Parameters.AddWithValue("@Actor", actor);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(token));
    }

    private static void BindProvider(SqlCommand cmd, AssistantProviderWrite write)
    {
        cmd.Parameters.AddWithValue("@Code", write.Code);
        cmd.Parameters.AddWithValue("@DisplayName", write.DisplayName);
        cmd.Parameters.AddWithValue("@BaseUrl", write.BaseUrl);
        cmd.Parameters.AddWithValue("@EnvVar", write.ApiKeyEnvVar);
        cmd.Parameters.AddWithValue("@Timeout", write.TimeoutSeconds);
        cmd.Parameters.AddWithValue("@Enabled", write.Enabled ? 1 : 0);
        cmd.Parameters.AddWithValue("@SortIdx", write.SortIdx);
        cmd.Parameters.AddWithValue("@Remark", (object?)write.Remark ?? DBNull.Value);
    }

    private static void BindModel(SqlCommand cmd, AssistantModelWrite write)
    {
        cmd.Parameters.AddWithValue("@ProviderId", write.ProviderId);
        cmd.Parameters.AddWithValue("@ModelCode", write.ModelCode);
        cmd.Parameters.AddWithValue("@DisplayName", write.DisplayName);
        cmd.Parameters.AddWithValue("@ContextWindow", (object?)write.ContextWindow ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@MaxOutput", (object?)write.MaxOutputTokens ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Temperature", (object?)write.DefaultTemperature ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Timeout", (object?)write.TimeoutSeconds ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@InputPrice", (object?)write.InputPerMillionYuan ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@OutputPrice", (object?)write.OutputPerMillionYuan ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@SupportsTools", write.SupportsTools ? 1 : 0);
        cmd.Parameters.AddWithValue("@Enabled", write.Enabled ? 1 : 0);
        cmd.Parameters.AddWithValue("@SortIdx", write.SortIdx);
        cmd.Parameters.AddWithValue("@Remark", (object?)write.Remark ?? DBNull.Value);
    }

    private static AssistantProviderRow ReadProvider(SqlDataReader reader, int offset = 0) => new(
        reader.GetInt32(offset),
        reader.GetString(offset + 1),
        reader.GetString(offset + 2),
        reader.GetString(offset + 3),
        reader.GetString(offset + 4),
        reader.GetInt32(offset + 5),
        reader.GetBoolean(offset + 6),
        reader.GetInt32(offset + 7),
        reader.IsDBNull(offset + 8) ? null : reader.GetString(offset + 8),
        ToUtc(reader.GetDateTime(offset + 9)),
        ToUtc(reader.GetDateTime(offset + 10)));

    private static AssistantModelRow ReadModel(SqlDataReader reader, int offset = 0) => new(
        reader.GetInt32(offset),
        reader.GetInt32(offset + 1),
        reader.GetString(offset + 2),
        reader.GetString(offset + 3),
        reader.IsDBNull(offset + 4) ? null : reader.GetInt32(offset + 4),
        reader.IsDBNull(offset + 5) ? null : reader.GetInt32(offset + 5),
        reader.IsDBNull(offset + 6) ? null : reader.GetDecimal(offset + 6),
        reader.IsDBNull(offset + 7) ? null : reader.GetInt32(offset + 7),
        reader.IsDBNull(offset + 8) ? null : reader.GetDecimal(offset + 8),
        reader.IsDBNull(offset + 9) ? null : reader.GetDecimal(offset + 9),
        reader.GetBoolean(offset + 10),
        reader.GetBoolean(offset + 11),
        reader.GetBoolean(offset + 12),
        reader.GetInt32(offset + 13),
        reader.IsDBNull(offset + 14) ? null : reader.GetString(offset + 14),
        ToUtc(reader.GetDateTime(offset + 15)),
        ToUtc(reader.GetDateTime(offset + 16)));

    private static DateTimeOffset ToUtc(DateTime value) =>
        new(DateTime.SpecifyKind(value, DateTimeKind.Utc));
}
