using System.Data;
using EOS.API.Models;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data;

/// <summary>
/// 客户定制版式仓储（ADR-010 决策 3/4/5，S2）：
/// - copy-on-write：首次"自定义"复制内置格式包 layout.json → REPORT_FORM_LAYOUT，
///   绑定写 REPORT_FORM_BINDING（FORM_TYPE × CLIENT_ID，空 CLIENT_ID = 单据类型默认）；
/// - 生效布局优先级：(FORM_TYPE, CLIENT_ID) → (FORM_TYPE, '') → 内置格式包；
/// - 权限门：SYSDD.FORM_DESIGN_TAG / FORM_ADJUST_TAG 个人覆盖组（组布尔 OR），
///   无个人行时取组位，与既有 SYSDD/SYSDH 权限语义一致。
/// 绑定行 HEADER_ID / TAIL_ID / PRINT_PRICE 供未来打印解析（S2 绑定优先级命中）。
/// </summary>
public sealed class ReportFormLayoutRepository(
    DbConnectionFactory connections,
    ReportFormatRepository formatRepository,
    ILogger<ReportFormLayoutRepository> logger)
{
    /// <summary>设计器权限：个人 FORM_DESIGN/ADJUST_TAG 优先，否则组 OR（ADR-010 决策 4/5）。</summary>
    public async Task<LayoutDesignerMode> GetDesignerModeAsync(
        string userId, int moduleId, CancellationToken token)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(token);

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
                return new LayoutDesignerMode(reader.GetBoolean("CAN_DESIGN"), reader.GetBoolean("CAN_ADJUST"));
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
            return new LayoutDesignerMode(
                groupReader.GetInt32("CAN_DESIGN") != 0, groupReader.GetInt32("CAN_ADJUST") != 0);
        return new LayoutDesignerMode(false, false);
    }

    /// <summary>是否存在任一模块的完整设计权限（页头字典等全局资产维护用）。</summary>
    public async Task<bool> HasAnyDesignPermissionAsync(string userId, CancellationToken token)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
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

    /// <summary>生效版式：绑定 (FORM_TYPE, CLIENT_ID) → (FORM_TYPE, '') → 内置格式包。</summary>
    public async Task<EffectiveLayout> GetEffectiveLayoutAsync(
        int moduleId, string? clientId, string userId, CancellationToken token)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(token);

        var binding = await ReadBindingAsync(connection, moduleId, clientId, token)
            ?? await ReadBindingAsync(connection, moduleId, null, token);
        if (binding?.LayoutId is { } layoutId)
        {
            var custom = await ReadCustomLayoutAsync(connection, layoutId, token);
            if (custom is not null)
            {
                logger.LogDebug("生效定制版式 module={ModuleId} client={ClientId} layoutId={LayoutId}",
                    moduleId, clientId, layoutId);
                return new EffectiveLayout(custom, true, layoutId, binding.HeaderId, binding.TailId, binding.PrintPrice);
            }
        }

        var builtin = formatRepository.GetDocumentFormat(moduleId);
        if (builtin is null)
            throw new InvalidOperationException($"模块 {moduleId} 无内置格式包。");
        return new EffectiveLayout(builtin.RawLayoutJson, false, null,
            binding?.HeaderId, binding?.TailId, binding?.PrintPrice);
    }

    /// <summary>copy-on-write：创建客户定制布局并写绑定（首次保存）。返回 LAYOUT_ID。</summary>
    public async Task<int> CreateCustomLayoutAsync(
        int moduleId, string userId, string layoutJson, string? clientId, CancellationToken token)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
        try
        {
            const string insertLayoutSql = """
                INSERT INTO dbo.REPORT_FORM_LAYOUT
                    (BASE_FORMAT_ID, OWNER_ID, LAYOUT_JSON, LAYOUT_VERSION, KIND, CREATE_PERSON, LAST_UPDATE_BY, LAST_UPDATE_DATE)
                OUTPUT INSERTED.LAYOUT_ID
                VALUES (@ModuleId, @OwnerId, @LayoutJson, 1, 'document', @UserId, @UserId, SYSDATETIME());
                """;
            int layoutId;
            await using (var command = new SqlCommand(insertLayoutSql, connection, transaction))
            {
                AddLayoutParameters(command, moduleId, userId, layoutJson);
                command.Parameters.Add("@OwnerId", SqlDbType.NVarChar, 50).Value = userId.Trim();
                layoutId = (int)(await command.ExecuteScalarAsync(token))!;
            }
            await UpsertBindingAsync(connection, transaction, moduleId, clientId, layoutId, userId, token);
            await transaction.CommitAsync(token);
            logger.LogInformation("创建客户定制版式 module={ModuleId} client={ClientId} layoutId={LayoutId}",
                moduleId, clientId, layoutId);
            return layoutId;
        }
        catch
        {
            await transaction.RollbackAsync(token);
            throw;
        }
    }

    /// <summary>更新定制布局（LAYOUT_VERSION 递增，内置升级不覆盖定制）。</summary>
    public async Task UpdateCustomLayoutAsync(
        int layoutId, string userId, string layoutJson, CancellationToken token)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        const string sql = """
            UPDATE dbo.REPORT_FORM_LAYOUT
            SET LAYOUT_JSON = @LayoutJson,
                LAYOUT_VERSION = LAYOUT_VERSION + 1,
                LAST_UPDATE_BY = @UserId,
                LAST_UPDATE_DATE = SYSDATETIME()
            WHERE LAYOUT_ID = @LayoutId;
            """;
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@LayoutId", SqlDbType.Int).Value = layoutId;
        command.Parameters.Add("@LayoutJson", SqlDbType.NVarChar, -1).Value = layoutJson;
        command.Parameters.Add("@UserId", SqlDbType.NChar, 40).Value = userId;
        await command.ExecuteNonQueryAsync(token);
        logger.LogInformation("更新客户定制版式 layoutId={LayoutId} version+1", layoutId);
    }

    /// <summary>页头条目列表（REPORT_LAYOUT KIND='HEADER'，ADR-009 §9.4.2 字典引用）。</summary>
    public async Task<IReadOnlyList<LayoutHeaderOption>> GetHeadersAsync(CancellationToken token)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        const string sql = """
            SELECT LTRIM(RTRIM(LAYOUT_ID)), LTRIM(RTRIM(ISNULL(LAYOUT_DESC, LAYOUT_ID))),
                   LTRIM(RTRIM(ISNULL(CONTENT, ''))), IMAGE_PATH
            FROM dbo.REPORT_LAYOUT WITH (NOLOCK)
            WHERE KIND = N'HEADER'
            ORDER BY LAYOUT_ID;
            """;
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(token);
        var result = new List<LayoutHeaderOption>();
        while (await reader.ReadAsync(token))
        {
            var id = reader.GetString(0).Trim();
            var name = reader.GetString(1);
            var contentJson = reader.GetString(2);
            var logoPath = reader.IsDBNull(3) ? null : reader.GetString(3).Trim();
            var (company, companyEn, headerText) = ParseHeaderContent(contentJson);
            result.Add(new LayoutHeaderOption(id, name, company, companyEn, headerText, logoPath));
        }
        return result;
    }

    /// <summary>保存页头条目（新增/更新，完整设计权限 CanDesign 由控制器把关）。</summary>
    public async Task SaveHeaderAsync(LayoutHeaderSaveRequest request, string userId, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(request.HeaderId))
            throw new ArgumentException("页头 ID 不能为空。", nameof(request.HeaderId));
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        var json = System.Text.Json.JsonSerializer.Serialize(new
        {
            companyName = request.Company ?? string.Empty,
            companyNameEn = request.CompanyEn ?? string.Empty,
            headerText = request.HeaderText ?? string.Empty,
        });
        const string sql = """
            IF EXISTS (SELECT 1 FROM dbo.REPORT_LAYOUT WHERE KIND = N'HEADER' AND LAYOUT_ID = @HeaderId)
                UPDATE dbo.REPORT_LAYOUT
                SET LAYOUT_DESC = @Name, CONTENT = @Content, IMAGE_PATH = @LogoPath,
                    LAST_UPDATE_BY = @UserId, LAST_UPDATE_DATE = SYSDATETIME()
                WHERE KIND = N'HEADER' AND LAYOUT_ID = @HeaderId;
            ELSE
                INSERT INTO dbo.REPORT_LAYOUT
                    (LAYOUT_ID, KIND, LAYOUT_DESC, CONTENT, IMAGE_PATH, IS_DEFAULT,
                     CREATE_PERSON, CREATE_DATE, LAST_UPDATE_BY, LAST_UPDATE_DATE)
                VALUES
                    (@HeaderId, N'HEADER', @Name, @Content, @LogoPath, 0,
                     @UserId, SYSDATETIME(), @UserId, SYSDATETIME());
            """;
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@HeaderId", SqlDbType.NVarChar, 100).Value = request.HeaderId.Trim();
        command.Parameters.Add("@Name", SqlDbType.NVarChar, 200).Value = request.Name?.Trim() ?? request.HeaderId.Trim();
        command.Parameters.Add("@Content", SqlDbType.NVarChar, -1).Value = json;
        command.Parameters.Add("@LogoPath", SqlDbType.NVarChar, 1000).Value =
            string.IsNullOrWhiteSpace(request.LogoPath) ? (object)DBNull.Value : request.LogoPath.Trim();
        command.Parameters.Add("@UserId", SqlDbType.NChar, 80).Value = userId;
        await command.ExecuteNonQueryAsync(token);
        logger.LogInformation("保存页头条目 headerId={HeaderId}", request.HeaderId);
    }

    /// <summary>保存版式绑定（HEADER_ID/TAIL_ID/PRINT_PRICE 随绑定，不覆盖既有 LAYOUT_ID）。</summary>
    public async Task SaveBindingAsync(
        int moduleId, string userId, LayoutBindingSaveRequest request, CancellationToken token)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        const string sql = """
            IF EXISTS (SELECT 1 FROM dbo.REPORT_FORM_BINDING WHERE FORM_TYPE = @ModuleId AND CLIENT_ID = @ClientId)
                UPDATE dbo.REPORT_FORM_BINDING
                SET HEADER_ID = @HeaderId, TAIL_ID = @TailId, PRINT_PRICE = @PrintPrice,
                    LAST_UPDATE_BY = @UserId, LAST_UPDATE_DATE = SYSDATETIME()
                WHERE FORM_TYPE = @ModuleId AND CLIENT_ID = @ClientId;
            ELSE
                INSERT INTO dbo.REPORT_FORM_BINDING
                    (FORM_TYPE, CLIENT_ID, HEADER_ID, TAIL_ID, PRINT_PRICE,
                     CREATE_PERSON, CREATE_DATE, LAST_UPDATE_BY, LAST_UPDATE_DATE)
                VALUES
                    (@ModuleId, @ClientId, @HeaderId, @TailId, @PrintPrice,
                     @UserId, SYSDATETIME(), @UserId, SYSDATETIME());
            """;
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@ModuleId", SqlDbType.NVarChar, 20).Value = moduleId.ToString(
            System.Globalization.CultureInfo.InvariantCulture);
        command.Parameters.Add("@ClientId", SqlDbType.NVarChar, 50).Value = request.ClientId ?? string.Empty;
        command.Parameters.Add("@HeaderId", SqlDbType.NVarChar, 100).Value =
            string.IsNullOrWhiteSpace(request.HeaderId) ? (object)DBNull.Value : request.HeaderId.Trim();
        command.Parameters.Add("@TailId", SqlDbType.NVarChar, 100).Value =
            string.IsNullOrWhiteSpace(request.TailId) ? (object)DBNull.Value : request.TailId.Trim();
        command.Parameters.Add("@PrintPrice", SqlDbType.Bit).Value =
            request.PrintPrice is { } printPrice ? printPrice : (object)DBNull.Value;
        command.Parameters.Add("@UserId", SqlDbType.NChar, 40).Value = userId;
        await command.ExecuteNonQueryAsync(token);
        logger.LogInformation("保存版式绑定 module={ModuleId} client={ClientId} header={HeaderId} tail={TailId}",
            moduleId, request.ClientId, request.HeaderId, request.TailId);
    }

    private static (string? Company, string? CompanyEn, string? HeaderText) ParseHeaderContent(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return (null, null, null);
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;
            var company = root.TryGetProperty("companyName", out var c) ? c.GetString() : null;
            var companyEn = root.TryGetProperty("companyNameEn", out var ce) ? ce.GetString() : null;
            var headerText = root.TryGetProperty("headerText", out var h) ? h.GetString() : null;
            return (company, companyEn, headerText);
        }
        catch
        {
            return (null, null, null);
        }
    }

    private static async Task<ReportFormBindingRow?> ReadBindingAsync(
        SqlConnection connection, int moduleId, string? clientId, CancellationToken token)
    {
        const string sql = """
            SELECT LAYOUT_ID, HEADER_ID, TAIL_ID, PRINT_PRICE
            FROM dbo.REPORT_FORM_BINDING WITH (NOLOCK)
            WHERE FORM_TYPE = @ModuleId AND CLIENT_ID = @ClientId;
            """;
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@ModuleId", SqlDbType.NVarChar, 20).Value = moduleId.ToString(
            System.Globalization.CultureInfo.InvariantCulture);
        command.Parameters.Add("@ClientId", SqlDbType.NVarChar, 50).Value = clientId ?? string.Empty;
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token)) return null;
        return new ReportFormBindingRow(
            reader.IsDBNull(0) ? null : reader.GetInt32(0),
            reader.IsDBNull(1) ? null : reader.GetString(1).Trim(),
            reader.IsDBNull(2) ? null : reader.GetString(2).Trim(),
            reader.IsDBNull(3) ? null : reader.GetBoolean(3));
    }

    private static async Task<string?> ReadCustomLayoutAsync(
        SqlConnection connection, int layoutId, CancellationToken token)
    {
        const string sql = """
            SELECT LAYOUT_JSON FROM dbo.REPORT_FORM_LAYOUT WITH (NOLOCK)
            WHERE LAYOUT_ID = @LayoutId;
            """;
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@LayoutId", SqlDbType.Int).Value = layoutId;
        await using var reader = await command.ExecuteReaderAsync(token);
        return await reader.ReadAsync(token) ? reader.GetString(0) : null;
    }

    private static async Task UpsertBindingAsync(
        SqlConnection connection, SqlTransaction transaction, int moduleId, string? clientId,
        int layoutId, string userId, CancellationToken token)
    {
        const string sql = """
            IF EXISTS (SELECT 1 FROM dbo.REPORT_FORM_BINDING WHERE FORM_TYPE = @ModuleId AND CLIENT_ID = @ClientId)
                UPDATE dbo.REPORT_FORM_BINDING
                SET LAYOUT_ID = @LayoutId, LAST_UPDATE_BY = @UserId, LAST_UPDATE_DATE = SYSDATETIME()
                WHERE FORM_TYPE = @ModuleId AND CLIENT_ID = @ClientId;
            ELSE
                INSERT INTO dbo.REPORT_FORM_BINDING
                    (FORM_TYPE, CLIENT_ID, LAYOUT_ID, CREATE_PERSON, CREATE_DATE, LAST_UPDATE_BY, LAST_UPDATE_DATE)
                VALUES
                    (@ModuleId, @ClientId, @LayoutId, @UserId, SYSDATETIME(), @UserId, SYSDATETIME());
            """;
        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@ModuleId", SqlDbType.NVarChar, 20).Value = moduleId.ToString(
            System.Globalization.CultureInfo.InvariantCulture);
        command.Parameters.Add("@ClientId", SqlDbType.NVarChar, 50).Value = clientId ?? string.Empty;
        command.Parameters.Add("@LayoutId", SqlDbType.Int).Value = layoutId;
        command.Parameters.Add("@UserId", SqlDbType.NChar, 40).Value = userId;
        await command.ExecuteNonQueryAsync(token);
    }

    private static void AddLayoutParameters(SqlCommand command, int moduleId, string userId, string layoutJson)
    {
        command.Parameters.Add("@ModuleId", SqlDbType.NVarChar, 50).Value = moduleId.ToString(
            System.Globalization.CultureInfo.InvariantCulture);
        command.Parameters.Add("@LayoutJson", SqlDbType.NVarChar, -1).Value = layoutJson;
        command.Parameters.Add("@UserId", SqlDbType.NChar, 40).Value = userId;
    }

    private sealed record ReportFormBindingRow(
        int? LayoutId, string? HeaderId, string? TailId, bool? PrintPrice);
}
