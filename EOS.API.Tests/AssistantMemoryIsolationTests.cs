using EOS.API.Data;
using EOS.API.Features.Assistant.Memory;
using EOS.API.Security;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// M3/M9 隔离集成测试：跨用户隔离、删除即时生效、pending 不注入、忘记我清空、
/// 确认覆盖同名。需真库 + 迁移 043；无连接时跳过（跳过≠已验证）。
/// </summary>
public sealed class AssistantMemoryIsolationTests
{
    private sealed class DenyAllPermissions : IPermissionService
    {
        public Task<ModulePermission> GetAsync(string userId, int moduleId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ModulePermission> RequireAsync(string userId, int moduleId, PermissionAction action, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private static string? TestConnection() =>
        Environment.GetEnvironmentVariable("EOS_ERP_TEST_CONNECTION");

    private static async Task<bool> TablesReadyAsync(string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            "SELECT 1 WHERE OBJECT_ID(N'dbo.ASSISTANT_MEMORY', N'U') IS NOT NULL AND OBJECT_ID(N'dbo.ASSISTANT_PROFILE', N'U') IS NOT NULL;",
            connection);
        return await command.ExecuteScalarAsync() is not null;
    }

    private static AssistantMemoryStore CreateStore(string connectionString)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:ErpDatabase"] = connectionString,
            })
            .Build();
        return new(new DbConnectionFactory(config), new DenyAllPermissions());
    }

    private static async Task CleanupUserAsync(string connectionString, string userId)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            "DELETE FROM dbo.ASSISTANT_MEMORY WHERE USER_ID = @UserId; DELETE FROM dbo.ASSISTANT_PROFILE WHERE USER_ID = @UserId;",
            connection);
        command.Parameters.AddWithValue("@UserId", userId);
        await command.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task CrossUser_IsolationHolds()
    {
        var connectionString = TestConnection();
        if (connectionString is null) return;
        if (!await TablesReadyAsync(connectionString)) return;

        var store = CreateStore(connectionString);
        var userA = "test-iso-a-" + Guid.NewGuid().ToString("N")[..8];
        var userB = "test-iso-b-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            var item = await store.AddMemoryAsync(userA, "fact", "常用模块", "先看送货单", null, CancellationToken.None);
            Assert.Empty(await store.ListMemoriesAsync(userB, CancellationToken.None));
            Assert.False(await store.DeleteMemoryAsync(userB, item.Id, CancellationToken.None));
            Assert.Equal("not_found", await store.ResolvePendingAsync(userB, item.Id, true, CancellationToken.None));
            Assert.True(await store.DeleteMemoryAsync(userA, item.Id, CancellationToken.None));
            Assert.Empty(await store.ListMemoriesAsync(userA, CancellationToken.None));
        }
        finally
        {
            await CleanupUserAsync(connectionString, userA);
            await CleanupUserAsync(connectionString, userB);
        }
    }

    [Fact]
    public async Task Pending_StaysOutOfActive_UntilConfirmed()
    {
        var connectionString = TestConnection();
        if (connectionString is null) return;
        if (!await TablesReadyAsync(connectionString)) return;

        var store = CreateStore(connectionString);
        var user = "test-pending-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            var pending = await store.AddPendingAsync(user, "fact", "常用模块", "先看送货单", null, 82, CancellationToken.None);
            Assert.Empty(await store.ListMemoriesAsync(user, CancellationToken.None));
            Assert.Single(await store.ListPendingAsync(user, CancellationToken.None));

            Assert.Equal("rejected", await store.ResolvePendingAsync(user, pending.Id, false, CancellationToken.None));
            Assert.Empty(await store.ListPendingAsync(user, CancellationToken.None));
            Assert.Empty(await store.ListMemoriesAsync(user, CancellationToken.None));

            var second = await store.AddPendingAsync(user, "fact", "常用模块", "先看送货单", null, 82, CancellationToken.None);
            Assert.Equal("confirmed", await store.ResolvePendingAsync(user, second.Id, true, CancellationToken.None));
            Assert.Single(await store.ListMemoriesAsync(user, CancellationToken.None));
        }
        finally
        {
            await CleanupUserAsync(connectionString, user);
        }
    }

    [Fact]
    public async Task Confirm_Overwrites_SameKey_AndForgetMe_ClearsAll()
    {
        var connectionString = TestConnection();
        if (connectionString is null) return;
        if (!await TablesReadyAsync(connectionString)) return;

        var store = CreateStore(connectionString);
        var user = "test-forget-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await store.AddMemoryAsync(user, "fact", "常用模块", "旧值", null, CancellationToken.None);
            var pending = await store.AddPendingAsync(user, "fact", "常用模块", "新值", null, 90, CancellationToken.None);
            Assert.Equal("confirmed", await store.ResolvePendingAsync(user, pending.Id, true, CancellationToken.None));
            var active = Assert.Single(await store.ListMemoriesAsync(user, CancellationToken.None));
            Assert.Equal("新值", active.MemoryValue);

            await store.SetPreferencesAsync(user, """{"theme":"dark"}""", CancellationToken.None);
            await store.ForgetMeAsync(user, CancellationToken.None);
            Assert.Empty(await store.ListMemoriesAsync(user, CancellationToken.None));
            Assert.Null(await store.GetPreferencesAsync(user, CancellationToken.None));
        }
        finally
        {
            await CleanupUserAsync(connectionString, user);
        }
    }
}
