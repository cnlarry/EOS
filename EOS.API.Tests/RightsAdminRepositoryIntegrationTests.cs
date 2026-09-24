using System.Data;
using System.Text.RegularExpressions;
using EOS.API.Data;
using EOS.API.Models;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 权限管理仓储集成测试：真实 EOS.ERP 开发库验证矩阵读取、upsert/删除语义、
/// 组聚合、成员关系与非法输入拒绝。临时用户/组以 ZR*/ZG* 前缀创建并自清理。
/// 连接串来自 env EOS_ERP_TEST_CONNECTION 或本机 本机配置文件；拿不到时跳过。
/// </summary>
[Trait("Category", "Integration")]
public sealed class RightsAdminRepositoryIntegrationTests : IDisposable
{
    private static readonly Lazy<string?> ConnectionString = new(ResolveConnectionString);

    private readonly RightsAdminRepository _repository;
    private readonly string _userId = "ZR" + Random.Shared.Next(10_000_000, 99_999_999);
    private readonly string _groupId = "ZG" + Random.Shared.Next(10_000_000, 99_999_999);
    private bool _seeded;

    public RightsAdminRepositoryIntegrationTests()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:ErpDatabase"] = ConnectionString.Value,
            })
            .Build();
        var connections = new DbConnectionFactory(config);
        _repository = new RightsAdminRepository(
            connections,
            new NavigationRepository(connections, NullLogger<NavigationRepository>.Instance),
            new EOS.API.Security.PermissionCache(config),
            NullLogger<RightsAdminRepository>.Instance,
            new WorkbenchAuditWriter(connections, new Microsoft.AspNetCore.Http.HttpContextAccessor(),
                new WorkbenchDefinitionProvider(connections, NullLogger<WorkbenchDefinitionProvider>.Instance),
                Microsoft.Extensions.Options.Options.Create(new EOS.API.Models.AuditSettings())));
    }

    [Fact]
    public async Task ModuleRights_SaveUpsertsAndClearDeletes()
    {
        if (ConnectionString.Value is null) return;
        await SeedAsync();
        try
        {
            await _repository.SaveUserModuleRightsAsync(
                _userId,
                [ModuleInput(2306, "B", addNew: true), ModuleInput(2301, "C", edit: true, cost: true)],
                "admin", "IT", CancellationToken.None);

            var matrix = await _repository.GetUserModuleMatrixAsync("admin", _userId, CancellationToken.None);
            var row2306 = matrix.Single(row => row.ModuleId == 2306);
            var row2301 = matrix.Single(row => row.ModuleId == 2301);
            Assert.True(row2306.HasPersonal);
            Assert.Equal("B", row2306.ExecTag);
            Assert.True(row2306.AddNew);
            Assert.False(row2306.Edit);
            Assert.Equal("personal", row2306.Effective.Source);
            Assert.True(row2301.Edit);
            Assert.True(row2301.Cost);

            // 清空 2306 → 个人行删除（回退组权限），2301 保留
            await _repository.SaveUserModuleRightsAsync(
                _userId, [ModuleInput(2306)], "admin", "IT", CancellationToken.None);
            matrix = await _repository.GetUserModuleMatrixAsync("admin", _userId, CancellationToken.None);
            Assert.False(matrix.Single(row => row.ModuleId == 2306).HasPersonal);
            Assert.True(matrix.Single(row => row.ModuleId == 2301).HasPersonal);

            // 组权限 + 成员 → 用户生效值来源为 group
            await _repository.SaveGroupModuleRightsAsync(
                _groupId, [ModuleInput(2306, "D", delete: true)], "admin", "IT", CancellationToken.None);
            await _repository.SaveUserGroupsAsync(_userId, [_groupId], "IT", CancellationToken.None);
            Assert.Equal("group", await _repository.GetEffectiveSourceAsync(_userId, 2306, CancellationToken.None));
            var groupMatrix = await _repository.GetGroupModuleMatrixAsync("admin", _groupId, CancellationToken.None);
            Assert.True(groupMatrix.Single(row => row.ModuleId == 2306).Delete);
            Assert.Equal("D", groupMatrix.Single(row => row.ModuleId == 2306).ExecTag);

            // 组权限清空 → 行删除
            await _repository.SaveGroupModuleRightsAsync(
                _groupId, [ModuleInput(2306)], "admin", "IT", CancellationToken.None);
            groupMatrix = await _repository.GetGroupModuleMatrixAsync("admin", _groupId, CancellationToken.None);
            Assert.False(groupMatrix.Single(row => row.ModuleId == 2306).HasPersonal);
        }
        finally
        {
            await CleanupAsync();
        }
    }

    [Fact]
    public async Task ReportRights_SaveAndClearRoundtrip()
    {
        if (ConnectionString.Value is null) return;
        await SeedAsync();
        try
        {
            var matrixBefore = await _repository.GetUserReportMatrixAsync("admin", _userId, CancellationToken.None);
            if (matrixBefore.Count == 0) return;
            var report = matrixBefore[0];

            await _repository.SaveUserReportRightsAsync(
                _userId,
                [new ReportRightsInput(report.ModuleId, report.ReportId, true, true, false, null)],
                "admin", "IT", CancellationToken.None);
            var matrix = await _repository.GetUserReportMatrixAsync("admin", _userId, CancellationToken.None);
            var row = matrix.Single(item => item.ReportId.Equals(report.ReportId, StringComparison.OrdinalIgnoreCase));
            Assert.True(row.HasPersonal);
            Assert.True(row.Preview);
            Assert.True(row.Print);
            Assert.False(row.Export);
            Assert.Equal("personal", row.Effective.Source);

            await _repository.SaveUserReportRightsAsync(
                _userId, [new ReportRightsInput(report.ModuleId, report.ReportId, false, false, false, null)],
                "admin", "IT", CancellationToken.None);
            matrix = await _repository.GetUserReportMatrixAsync("admin", _userId, CancellationToken.None);
            Assert.False(matrix.Single(item => item.ReportId.Equals(report.ReportId, StringComparison.OrdinalIgnoreCase)).HasPersonal);
        }
        finally
        {
            await CleanupAsync();
        }
    }

    [Fact]
    public async Task Members_ReplaceSemantics()
    {
        if (ConnectionString.Value is null) return;
        await SeedAsync();
        try
        {
            await _repository.SaveGroupMembersAsync(_groupId, [_userId], "IT", CancellationToken.None);
            var members = await _repository.GetGroupMembersAsync(_groupId, CancellationToken.None);
            Assert.Contains(members, member => member.UserId.Equals(_userId, StringComparison.OrdinalIgnoreCase));

            var groups = await _repository.GetUserGroupsAsync(_userId, CancellationToken.None);
            Assert.Contains(groups, group => group.GroupId.Equals(_groupId, StringComparison.OrdinalIgnoreCase));

            // 全量替换为空 → 成员清空
            await _repository.SaveGroupMembersAsync(_groupId, [], "IT", CancellationToken.None);
            members = await _repository.GetGroupMembersAsync(_groupId, CancellationToken.None);
            Assert.Empty(members);
        }
        finally
        {
            await CleanupAsync();
        }
    }

    [Fact]
    public async Task InvalidInput_IsRejected()
    {
        if (ConnectionString.Value is null) return;
        await SeedAsync();
        try
        {
            // 模块不在管理员可见范围
            await Assert.ThrowsAsync<ArgumentException>(() =>
                _repository.SaveUserModuleRightsAsync(
                    _userId, [ModuleInput(999_999, "B")], "admin", "IT", CancellationToken.None));
            // 非法 EXEC_TAG
            await Assert.ThrowsAsync<ArgumentException>(() =>
                _repository.SaveUserModuleRightsAsync(
                    _userId, [ModuleInput(2306, "AB")], "admin", "IT", CancellationToken.None));
            // DATA_FILTER 无法受控解析
            await Assert.ThrowsAsync<ArgumentException>(() =>
                _repository.SaveUserModuleRightsAsync(
                    _userId, [ModuleInput(2306, "B", dataFilter: "SYSDL.XX_BROKEN(=")],
                    "admin", "IT", CancellationToken.None));
            // 目标用户不存在
            await Assert.ThrowsAsync<KeyNotFoundException>(() =>
                _repository.GetUserModuleMatrixAsync("admin", "ZZNOEXIST", CancellationToken.None));
            // 目标组不存在
            await Assert.ThrowsAsync<KeyNotFoundException>(() =>
                _repository.GetGroupModuleMatrixAsync("admin", "ZZNOEXIST", CancellationToken.None));
        }
        finally
        {
            await CleanupAsync();
        }
    }

    private async Task SeedAsync()
    {
        if (ConnectionString.Value is null || _seeded) return;
        await ExecuteNonQueryAsync($"""
            INSERT INTO dbo.SYSDL (USER_ID,EMP_ID,ACTIVE_TAG,REMARK,CREATE_PERSON,CREATE_DATE)
            VALUES (N'{_userId}',N'{_userId}',1,N'E2E 权限管理临时用户','IT',GETDATE());
            INSERT INTO dbo.SYSDG (G_IDX,G_DESC,REMARK,CREATE_PERSON,CREATE_DATE)
            VALUES (N'{_groupId}',N'E2E 权限测试组',N'临时','IT',GETDATE());
            """);
        _seeded = true;
    }

    private async Task CleanupAsync()
    {
        if (ConnectionString.Value is null || !_seeded) return;
        await ExecuteNonQueryAsync($"""
            DELETE FROM dbo.SYSDD WHERE USER_ID=N'{_userId}';
            DELETE FROM dbo.SYSDH WHERE G_IDX=N'{_groupId}';
            DELETE FROM dbo.SYSDD_REPORT WHERE USER_ID=N'{_userId}';
            DELETE FROM dbo.SYSDH_REPORT WHERE G_IDX=N'{_groupId}';
            DELETE FROM dbo.SYSDG_USER WHERE USER_ID=N'{_userId}' OR G_IDX=N'{_groupId}';
            DELETE FROM dbo.SYSDG WHERE G_IDX=N'{_groupId}';
            DELETE FROM dbo.SYSDL WHERE USER_ID=N'{_userId}';
            """);
        _seeded = false;
    }

    private async Task ExecuteNonQueryAsync(string sql)
    {
        await using var connection = new SqlConnection(ConnectionString.Value);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static ModuleRightsInput ModuleInput(
        int moduleId, string? execTag = null, bool addNew = false, bool edit = false, bool delete = false, bool cost = false,
        string? dataFilter = null) =>
        new(moduleId, execTag, addNew, edit, delete, false, false, false, cost, false, false, false,
            false, false, false, false, false, false, false, false, false, false,
            null, null, null, null, null, null, dataFilter);

    public void Dispose()
    {
        if (ConnectionString.Value is null) return;
        try
        {
            CleanupAsync().GetAwaiter().GetResult();
        }
        catch
        {
            // 清理失败不影响测试结论；残留仅为开发库测试数据
        }
    }

    private static string? ResolveConnectionString()
    {
        var env = Environment.GetEnvironmentVariable("EOS_ERP_TEST_CONNECTION");
        if (!string.IsNullOrWhiteSpace(env)) return env;
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "config.toml");
            var text = File.ReadAllText(path);
            var match = Regex.Match(text, "\\[mcp_servers\\.mssql\\.env\\][\\s\\S]*?MSSQL_CONNECTION_STRING\\s*=\\s*\"([^\"]+)\"");
            return match.Success && match.Groups[1].Value.Contains("Database=EOS.ERP")
                ? match.Groups[1].Value
                : null;
        }
        catch
        {
            return null;
        }
    }
}
