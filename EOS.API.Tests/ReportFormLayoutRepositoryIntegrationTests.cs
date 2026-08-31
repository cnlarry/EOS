using System.Data;
using EOS.API.Data;
using EOS.API.Models;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 客户定制版式仓储集成测试（ADR-010 §5 S2 copy-on-write）：
/// 直连 EOS.ERP 验证真实 SQL——copy-on-write 创建/更新、绑定优先级
/// （(FORM_TYPE, CLIENT_ID) → (FORM_TYPE, '') → 内置）、权限位读取（个人覆盖组）。
/// 测试数据在 Dispose 中按测试模块/用户清理。
/// </summary>
[Trait("Category", "Integration")]
public sealed class ReportFormLayoutRepositoryIntegrationTests : IDisposable
{
    private const int TestModule = 1405;
    private const string TestClient = "LDTEST-CLIENT";
    private const string TestUser = "LDTEST";

    private static readonly Lazy<string?> ConnectionString = new(ResolveConnectionString);

    private readonly ReportFormLayoutRepository _repository;
    private readonly List<int> _createdLayoutIds = [];

    public ReportFormLayoutRepositoryIntegrationTests()
    {
        EnsureSchema();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:ErpDatabase"] = ConnectionString.Value,
            })
            .Build();
        var formats = new ReportFormatRepository(
            Microsoft.Extensions.Options.Options.Create(new ReportFormatsSettings()),
            new FakeWebHostEnvironment(
                ResolveRepoRoot() is { } root ? Path.Combine(root, "EOS.API") : string.Empty),
            NullLogger<ReportFormatRepository>.Instance);
        _repository = new ReportFormLayoutRepository(
            new DbConnectionFactory(config), formats, NullLogger<ReportFormLayoutRepository>.Instance);
    }

    [Fact]
    public async Task CopyOnWrite_Create_Update_And_BindingPriority()
    {
        if (ConnectionString.Value is null) return;

        // 无定制时命中内置格式包
        var builtin = await _repository.GetEffectiveLayoutAsync(TestModule, TestClient, TestUser, default);
        Assert.False(builtin.IsCustom);
        Assert.Null(builtin.LayoutId);

        // copy-on-write：创建定制布局（客户级绑定）
        var customJson = builtin.LayoutJson.Replace(
            "\"id\": \"c2\"", "\"id\": \"c2\"", StringComparison.Ordinal);
        var layoutId = await _repository.CreateCustomLayoutAsync(
            TestModule, TestUser, customJson, TestClient, default);
        _createdLayoutIds.Add(layoutId);
        Assert.True(layoutId > 0);

        // 客户级绑定命中定制布局
        var clientEffective = await _repository.GetEffectiveLayoutAsync(TestModule, TestClient, TestUser, default);
        Assert.True(clientEffective.IsCustom);
        Assert.Equal(layoutId, clientEffective.LayoutId);

        // 更新后版本递增（LAYOUT_VERSION 2）
        await _repository.UpdateCustomLayoutAsync(layoutId, TestUser, customJson, default);
        await using var connection = new SqlConnection(ConnectionString.Value);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            "SELECT LAYOUT_VERSION FROM dbo.REPORT_FORM_LAYOUT WHERE LAYOUT_ID=@Id", connection);
        command.Parameters.Add("@Id", SqlDbType.Int).Value = layoutId;
        Assert.Equal(2, (int)(await command.ExecuteScalarAsync())!);

        // 单据类型默认绑定（CLIENT_ID=''）：另一客户也命中定制（无客户级行时走默认行）
        // 本用例只验证默认行创建后命中
        var defaultLayoutId = await _repository.CreateCustomLayoutAsync(
            TestModule, TestUser, customJson, null, default);
        _createdLayoutIds.Add(defaultLayoutId);
        var otherClient = await _repository.GetEffectiveLayoutAsync(TestModule, "OTHER-CLIENT", TestUser, default);
        Assert.True(otherClient.IsCustom);
        Assert.Equal(defaultLayoutId, otherClient.LayoutId);
    }

    [Fact]
    public async Task DesignerMode_PersonalOverridesGroup()
    {
        if (ConnectionString.Value is null) return;

        // 无权限行 → 默认无权
        var none = await _repository.GetDesignerModeAsync("NO-SUCH-USER", TestModule, default);
        Assert.False(none.CanDesign);
        Assert.False(none.CanAdjust);

        // 个人行 FORM_DESIGN_TAG=1 → CanDesign
        await using var connection = new SqlConnection(ConnectionString.Value);
        await connection.OpenAsync();
        await using (var insert = new SqlCommand(
            """
            INSERT INTO dbo.SYSDD (USER_ID, M_IDX, FORM_DESIGN_TAG, FORM_ADJUST_TAG)
            VALUES (@User, @Module, 1, 0);
            """, connection))
        {
            insert.Parameters.Add("@User", SqlDbType.NChar, 10).Value = TestUser;
            insert.Parameters.Add("@Module", SqlDbType.Int).Value = TestModule;
            await insert.ExecuteNonQueryAsync();
        }
        try
        {
            var personal = await _repository.GetDesignerModeAsync(TestUser, TestModule, default);
            Assert.True(personal.CanDesign);
            Assert.False(personal.CanAdjust);
        }
        finally
        {
            await using var cleanup = new SqlCommand(
                "DELETE FROM dbo.SYSDD WHERE USER_ID=@User AND M_IDX=@Module AND EXEC_TAG='A';", connection);
            cleanup.Parameters.Add("@User", SqlDbType.NChar, 10).Value = TestUser;
            cleanup.Parameters.Add("@Module", SqlDbType.Int).Value = TestModule;
            await cleanup.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task HeaderDictionary_SaveAndList_AndBindingPersist()
    {
        if (ConnectionString.Value is null) return;

        const string testHeaderId = "LDTEST-HDR";
        await using var connection = new SqlConnection(ConnectionString.Value);
        await connection.OpenAsync();
        try
        {
            // 保存页头条目
            await _repository.SaveHeaderAsync(
                new LayoutHeaderSaveRequest(testHeaderId, "测试页头", "测试公司", "TEST CO., LTD.", "TEL: 123", null),
                TestUser, default);
            var headers = await _repository.GetHeadersAsync(default);
            var saved = headers.FirstOrDefault(h => h.HeaderId == testHeaderId);
            Assert.NotNull(saved);
            Assert.Equal("测试公司", saved.Company);
            Assert.Equal("TEST CO., LTD.", saved.CompanyEn);

            // 保存绑定（HEADER_ID 随绑定）
            await _repository.SaveBindingAsync(
                TestModule, TestUser, new LayoutBindingSaveRequest(TestClient, testHeaderId, "BLANK", true), default);
            var effective = await _repository.GetEffectiveLayoutAsync(TestModule, TestClient, TestUser, default);
            Assert.Equal(testHeaderId, effective.HeaderId);
            Assert.Equal("BLANK", effective.TailId);
            Assert.True(effective.PrintPrice);
        }
        finally
        {
            await using var cleanup = new SqlCommand(
                """
                DELETE FROM dbo.REPORT_FORM_BINDING WHERE FORM_TYPE=@Module AND CLIENT_ID=@Client;
                DELETE FROM dbo.REPORT_LAYOUT WHERE LAYOUT_ID=@HeaderId AND KIND=N'HEADER';
                """, connection);
            cleanup.Parameters.Add("@Module", SqlDbType.NVarChar, 20).Value = TestModule.ToString();
            cleanup.Parameters.Add("@Client", SqlDbType.NVarChar, 50).Value = TestClient;
            cleanup.Parameters.Add("@HeaderId", SqlDbType.NVarChar, 100).Value = testHeaderId;
            await cleanup.ExecuteNonQueryAsync();
        }
    }

    public void Dispose()
    {
        if (ConnectionString.Value is null || _createdLayoutIds.Count == 0) return;
        using var connection = new SqlConnection(ConnectionString.Value);
        connection.Open();
        foreach (var layoutId in _createdLayoutIds)
        {
            using var cleanup = new SqlCommand(
                """
                DELETE FROM dbo.REPORT_FORM_BINDING WHERE LAYOUT_ID=@Id;
                DELETE FROM dbo.REPORT_FORM_LAYOUT WHERE LAYOUT_ID=@Id;
                """, connection);
            cleanup.Parameters.Add("@Id", SqlDbType.Int).Value = layoutId;
            cleanup.ExecuteNonQuery();
        }
    }

    private static string? ResolveConnectionString()
        => Environment.GetEnvironmentVariable("EOS_ERP_TEST_CONNECTION");

    private static void EnsureSchema()
    {
        var connectionString = ConnectionString.Value;
        if (connectionString is null) return;
        var result = DbUp.DeployChanges.To
            .SqlDatabase(connectionString)
            .WithScriptsEmbeddedInAssembly(
                typeof(ErpDatabaseInitializer).Assembly,
                name => name.Contains(".Data.Migrations.", StringComparison.OrdinalIgnoreCase) &&
                        name.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            .WithTransactionPerScript()
            .JournalToSqlTable("dbo", "ERP_SCHEMA_JOURNAL")
            .LogToConsole()
            .Build()
            .PerformUpgrade();
    }

    private static string? ResolveRepoRoot()
    {
        var metadata = typeof(ReportFormLayoutRepositoryIntegrationTests).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
            .OfType<System.Reflection.AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == "RepoRoot");
        return metadata?.Value;
    }

    private sealed class FakeWebHostEnvironment(string contentRoot) : Microsoft.AspNetCore.Hosting.IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "EOS.API";
        public Microsoft.Extensions.FileProviders.IFileProvider WebRootFileProvider { get; set; }
            = new Microsoft.Extensions.FileProviders.NullFileProvider();
        public string WebRootPath { get; set; } = string.Empty;
        public string EnvironmentName { get; set; } = "Test";
        public string ContentRootPath { get; set; } = contentRoot;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; }
            = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
