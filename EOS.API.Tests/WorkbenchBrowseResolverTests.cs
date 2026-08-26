using EOS.API.Data;
using Microsoft.Data.SqlClient;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 跨模块关联字段浏览链接解析器测试（BROWSE_URL → 现代记录浏览描述符）。
/// 解析为纯函数测试；解析落库（目标模块/主键/来源列）为直连 EOS.ERP 集成测试，
/// 连接串来源与跳过策略同 AssistantRepositoryIntegrationTests（EOS_ERP_TEST_CONNECTION）。
/// </summary>
public sealed class WorkbenchBrowseResolverTests
{
    private static readonly Lazy<string?> ConnectionString = new(ResolveConnectionString);

    private static string? ResolveConnectionString()
        => Environment.GetEnvironmentVariable("EOS_ERP_TEST_CONNECTION");

    // ---------- 纯解析 ----------

    [Fact]
    public void ParseBrowseUrl_SingleIdxKey_MapsTargetToSource()
    {
        var mapping = WorkbenchBrowseResolver.ParseBrowseUrl("~/COP/CLIENT.aspx?IDX=CLIENT_ID={CLIENT_ID}");
        Assert.Equal("CLIENT_ID", mapping["CLIENT_ID"]);
        Assert.Single(mapping);
    }

    [Fact]
    public void ParseBrowseUrl_CompositeIdxKeys_PreservesOrder()
    {
        var mapping = WorkbenchBrowseResolver.ParseBrowseUrl("~/COP/Account.aspx?IDX=ACCOUNT_TYPE={ACCOUNT_TYPE}^ACCOUNT_NO={ACCOUNT_NO}");
        Assert.Equal(new[] { "ACCOUNT_TYPE", "ACCOUNT_NO" }, mapping.Keys);
        Assert.Equal("ACCOUNT_TYPE", mapping["ACCOUNT_TYPE"]);
        Assert.Equal("ACCOUNT_NO", mapping["ACCOUNT_NO"]);
    }

    [Fact]
    public void ParseBrowseUrl_BareQueryParam_MapFallback()
    {
        var mapping = WorkbenchBrowseResolver.ParseBrowseUrl("~/COP/ClientSearch_Frame.aspx?CLIENT_ID={CLIENT_ID}&title=x");
        Assert.Equal("CLIENT_ID", mapping["CLIENT_ID"]);
        Assert.Single(mapping);
    }

    [Fact]
    public void ParseBrowseUrl_NoPlaceholders_EmptyMapping()
    {
        var mapping = WorkbenchBrowseResolver.ParseBrowseUrl("~/Admin/x.aspx?title=x");
        Assert.Empty(mapping);
    }

    [Fact]
    public void ParseBrowseUrl_TargetColumn_IsCaseInsensitive()
    {
        var mapping = WorkbenchBrowseResolver.ParseBrowseUrl("~/x.aspx?IDX=client_id={CLIENT_ID}");
        Assert.True(mapping.ContainsKey("CLIENT_ID"));
    }

    // ---------- 落库解析（直连 EOS.ERP） ----------

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Resolve_SingleKeyWorkbenchTarget_ProducesRecordBrowse()
    {
        var field = await ReadFieldAsync("COP_ORDER_M", "CLIENT_ID");
        if (field is null || ConnectionString.Value is null) return; // 环境无连接串时跳过

        var resolved = await ResolveAsync(field, new HashSet<int> { 1401 });
        Assert.NotNull(resolved);
        Assert.Equal(1401, resolved.BrowseModuleId);
        Assert.Equal(new[] { "CLIENT_ID" }, resolved.BrowseKeyFields);
        Assert.Null(resolved.BrowseUrl);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Resolve_CompositeKey_InTargetPkOrder()
    {
        var field = await ReadFieldAsync("COP_RECEIPT_D", "ACCOUNT_NO");
        if (field is null || ConnectionString.Value is null) return;

        var resolved = await ResolveAsync(field, new HashSet<int> { 170101 });
        Assert.NotNull(resolved);
        Assert.Equal(170101, resolved.BrowseModuleId);
        // 目标 170101 主键顺序 = (ACCOUNT_TYPE, ACCOUNT_NO)，键源列须按该顺序下发
        Assert.Equal(new[] { "ACCOUNT_TYPE", "ACCOUNT_NO" }, resolved.BrowseKeyFields);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Resolve_TargetNotInUnifiedFormWhitelist_ListFallback()
    {
        var field = await ReadFieldAsync("COP_ORDER_M", "CLIENT_ID");
        if (field is null || ConnectionString.Value is null) return;

        var resolved = await ResolveAsync(field, new HashSet<int>());
        Assert.NotNull(resolved);
        Assert.Equal(1401, resolved.BrowseModuleId);      // 保留：降级为目标模块列表链接
        Assert.Null(resolved.BrowseKeyFields);            // 无记录浏览
        Assert.Null(resolved.BrowseUrl);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Resolve_SpecialPageTarget_PlainText()
    {
        var field = await ReadFieldAsync("CLIENT", "OWNER"); // BROWSE_M_IDX=2306（用户权限设定，特殊页）
        if (field is null || ConnectionString.Value is null) return;

        var resolved = await ResolveAsync(field, new HashSet<int> { 2306 });
        Assert.NotNull(resolved);
        Assert.Null(resolved.BrowseModuleId);
        Assert.Null(resolved.BrowseKeyFields);
        Assert.Null(resolved.BrowseUrl);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Resolve_NoBrowseMetadata_Unchanged()
    {
        var field = await ReadFieldAsync("COP_ORDER_M", "CLIENT_NAME");
        if (field is null || ConnectionString.Value is null) return;

        var resolved = await ResolveAsync(field, new HashSet<int> { 1401 });
        Assert.NotNull(resolved);
        Assert.True(resolved.BrowseModuleId is null or 0); // 无浏览元数据保持原样
        Assert.Null(resolved.BrowseKeyFields);
    }

    private async Task<WorkbenchField?> ResolveAsync(WorkbenchField field, IReadOnlySet<int> whitelist)
    {
        await using var connection = new SqlConnection(ConnectionString.Value);
        await connection.OpenAsync();
        var result = await WorkbenchBrowseResolver.ResolveAsync(connection, [field], SourceTableFor(field), whitelist, CancellationToken.None);
        return result.Count == 1 ? result[0] : null;
    }

    private static string SourceTableFor(WorkbenchField field) => FieldTableCache[field.Key];

    private static readonly Dictionary<string, string> FieldTableCache = new(StringComparer.OrdinalIgnoreCase)
    {
        ["CLIENT_ID"] = "COP_ORDER_M",
        ["ACCOUNT_NO"] = "COP_RECEIPT_D",
        ["CLIENT_NAME"] = "COP_ORDER_M",
        ["OWNER"] = "CLIENT",
    };

    /// <summary>从 FIELDS 读取真实 BROWSE 元数据构造 WorkbenchField（无连接串或无记录时返回 null）。</summary>
    private static async Task<WorkbenchField?> ReadFieldAsync(string table, string fieldId)
    {
        var connectionString = ConnectionString.Value;
        if (connectionString is null) return null;
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        const string sql = "SELECT F_DESC,F_TYPE,COALESCE(DISPLAY_LENGTH,100),BROWSE_URL,BROWSE_M_IDX " +
                           "FROM dbo.FIELDS WITH (NOLOCK) WHERE T_ID=@Table AND F_ID=@Field";
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@Table", System.Data.SqlDbType.VarChar, 100).Value = table;
        command.Parameters.Add("@Field", System.Data.SqlDbType.VarChar, 100).Value = fieldId;
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;
        var label = reader.IsDBNull(0) ? fieldId : reader.GetString(0).Trim();
        var type = reader.IsDBNull(1) ? "nvarchar" : reader.GetString(1).Trim();
        var width = reader.IsDBNull(2) ? 100 : Math.Clamp(reader.GetInt32(2), 40, 300);
        var browseUrl = reader.IsDBNull(3) ? null : reader.GetString(3);
        var browseModuleId = reader.IsDBNull(4) ? (int?)null : reader.GetInt32(4);
        return new WorkbenchField(fieldId, label, type, width, null, false, BrowseUrl: browseUrl, BrowseModuleId: browseModuleId);
    }
}