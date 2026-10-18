using System.Data;
using System.Text.RegularExpressions;
using EOS.API.Data;
using EOS.API.Data.DocumentActions;
using EOS.API.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// ADR-035 三条新规则的**真库**用例：
/// ① 「启用批核」声明为真但主表缺批核三列 ⇒ 保存被拒，且消息**指名缺哪列**（服务端 fail-closed）；
/// ② 联动不变量：未启用批核 ⇒ 自动批核必须为假（服务端抹平，不只靠前端联动）；
/// ③ 批量停用（`DisableAllActionsAsync`，ADR-035 §2.4）：只停效果链动作、不动自定义按钮、标脏留审计、
///    再点一次是"无事发生"（返回 0，不重复标脏）；
/// ④ 置位集合自证：迁移 345 置 1 的每个模块，主表批核三列齐全（否则批核会炸在真单据上）。
///
/// **为什么非要有真库用例**：这四条判据都要打库（`sys.columns` / 行级 `ENABLED` / 模块行 / 脏标记），
/// 编译、纯单测与静态门禁都拦不住回归——尤其①，它的"缺列改名"正是最容易在重构里悄悄放跑的一类。
///
/// 用例自建临时模块（编号 98xxxxxx 段），Dispose 清理；拿不到连接串时跳过。
/// </summary>
[Collection("live-database")]
[Trait("Category", "Integration")]
public sealed class ModuleApproveEntryLiveTests : IDisposable
{
    private static readonly Lazy<string?> ConnectionString = new(ResolveConnectionString);

    private readonly MenuAdminRepository _menus;
    private readonly ModuleBusinessConfigRepository _businessConfig;
    private readonly int _moduleId;

    public ModuleApproveEntryLiveTests()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:ErpDatabase"] = ConnectionString.Value,
            })
            .Build();
        var connections = new DbConnectionFactory(config);
        var provider = new WorkbenchDefinitionProvider(connections, NullLogger<WorkbenchDefinitionProvider>.Instance);
        var auditWriter = new WorkbenchAuditWriter(
            connections, new HttpContextAccessor(), provider, Options.Create(new AuditSettings()));
        var dirtyMarker = new WorkbenchDirtyMarker(connections);
        var documentActions = new DocumentActionRegistry(
            Array.Empty<IDocumentUserAction>(), NullLogger<DocumentActionRegistry>.Instance);
        _businessConfig = new ModuleBusinessConfigRepository(
            connections, dirtyMarker, auditWriter, documentActions, new WorkbenchIdempotency(),
            LiveFormCapability.FromEnvironment(),
            NullLogger<ModuleBusinessConfigRepository>.Instance);
        _menus = new MenuAdminRepository(
            connections, _businessConfig, dirtyMarker, auditWriter, NullLogger<MenuAdminRepository>.Instance);
        _moduleId = 98_000_000 + Random.Shared.Next(0, 9_999_999);
    }

    [Fact]
    public async Task 启用批核但主表缺三列时被拒并指名缺哪列()
    {
        if (ConnectionString.Value is null) return;
        var masterTable = await FindRegisteredTableMissingApproveColumnsAsync();
        if (masterTable is null) return;
        await SeedModuleAsync(masterTable);

        var module = await _menus.GetModuleAsync(_moduleId, CancellationToken.None);
        Assert.NotNull(module);

        var error = await Assert.ThrowsAsync<ArgumentException>(() =>
            _menus.SaveAllAsync(
                new SaveMenuModuleRequest(module! with { ApproveEnabled = true, AUTO_APPROVE = false }),
                _moduleId, "integration-test", CancellationToken.None));

        Assert.Contains(masterTable, error.Message);
        var missing = await MissingApproveColumnsAsync(masterTable);
        Assert.NotEmpty(missing);   // 夹具选错（该表三列齐全）时立刻报出来，而不是让用例静默通过
        Assert.All(missing, column => Assert.Contains(column, error.Message));
        // 被拒就是被拒：声明不能落库，否则界面会显示一个点不得的入口
        var after = await _menus.GetModuleAsync(_moduleId, CancellationToken.None);
        Assert.False(after!.ApproveEnabled);
    }

    [Fact]
    public async Task 未启用批核时自动批核被服务端抹平()
    {
        if (ConnectionString.Value is null) return;
        await SeedModuleAsync("ACC_ACCOUNT");   // 主表三列齐全：把"缺列"这一维从本用例里排除

        var module = await _menus.GetModuleAsync(_moduleId, CancellationToken.None);
        Assert.NotNull(module);

        await _menus.SaveAllAsync(
            new SaveMenuModuleRequest(module! with { AUTO_APPROVE = true, ApproveEnabled = false }),
            _moduleId, "integration-test", CancellationToken.None);

        var saved = await _menus.GetModuleAsync(_moduleId, CancellationToken.None);
        Assert.NotNull(saved);
        Assert.False(saved!.ApproveEnabled);
        Assert.False(saved.AUTO_APPROVE);
    }

    [Fact]
    public async Task 批量停用只停效果链动作不动按钮并标脏()
    {
        if (ConnectionString.Value is null) return;
        await SeedModuleAsync("ACC_ACCOUNT");
        await SeedActionsAsync();

        var disabled = await _businessConfig.DisableAllActionsAsync(
            _moduleId, "integration-test", CancellationToken.None);

        Assert.Equal(2, disabled);   // 两条效果链动作（SAVE / APPROVE_EFFECT）
        Assert.Equal(2, await CountAsync(
            "SELECT COUNT(*) FROM dbo.MODULE_BUSINESS_ACTION WHERE M_IDX=@Id AND EVENT_CODE<>N'MANUAL' AND ISNULL(ENABLED,1)=0;"));
        Assert.Equal(1, await CountAsync(
            "SELECT COUNT(*) FROM dbo.MODULE_BUSINESS_ACTION WHERE M_IDX=@Id AND EVENT_CODE=N'MANUAL' AND ISNULL(ENABLED,1)=1;"));
        Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM dbo.WORKBENCH_MODULE_DIRTY WHERE M_IDX=@Id;"));
        Assert.Equal(1, await CountAsync(
            "SELECT COUNT(*) FROM dbo.AUDIT_EVENT WHERE M_IDX=@Id AND ACTION=N'DISABLE_ALL';"));

        // 再点一次是"无事发生"：返回 0，不重复留痕（否则积压与审计里全是噪声）
        Assert.Equal(0, await _businessConfig.DisableAllActionsAsync(
            _moduleId, "integration-test", CancellationToken.None));
        Assert.Equal(1, await CountAsync(
            "SELECT COUNT(*) FROM dbo.AUDIT_EVENT WHERE M_IDX=@Id AND ACTION=N'DISABLE_ALL';"));
    }

    [Fact]
    public async Task 新增模块按父编号加两位序号取号()
    {
        if (ConnectionString.Value is null) return;
        // 真实父号取 2301：分配器是"父编号 × 100 + 段内空序号"，临时大号会溢出 int（父号须 < 21,000,000）。
        var parent = await _menus.GetModuleAsync(2301, CancellationToken.None);
        if (parent is null) return;   // 库里还没有系统功能目录时跳过（迁移 347 之前）
        var child = parent with
        {
            M_IDX = 0,
            M_DESC = "编号分配集成测试-子模块",
            M_P_IDX = 2301,
            SORT_IDX = 0,
            M_TAG = true,
        };

        var createdId = await _menus.SaveAllAsync(
            new SaveMenuModuleRequest(child), null, "integration-test", CancellationToken.None);
        try
        {
            Assert.InRange(createdId, 230101, 230199);   // 父 2301 ⇒ 230101 起，不再被别段的大号带跑
            var created = await _menus.GetModuleAsync(createdId, CancellationToken.None);
            Assert.Equal(2301, created!.M_P_IDX);
        }
        finally
        {
            await DeleteModuleAsync(createdId);
        }
    }

    private async Task DeleteModuleAsync(int moduleId)
    {
        await using var connection = new SqlConnection(ConnectionString.Value);
        await connection.OpenAsync();
        await using var command = new SqlCommand("DELETE FROM dbo.MODULES WHERE M_IDX=@Id;", connection);
        command.Parameters.Add("@Id", SqlDbType.Int).Value = moduleId;
        await command.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task 迁移置位的模块主表批核三列齐全()
    {
        if (ConnectionString.Value is null) return;
        Assert.True(await CountAsync(
            "SELECT COUNT(*) FROM dbo.MODULES WHERE ISNULL(APPROVE_ENABLED,0)=1;") > 0,
            "库里应当有已声明批核入口的模块（迁移 345 按\"今天有入口\"置位）");
        var broken = await CountAsync("""
            SELECT COUNT(*) FROM dbo.MODULES m
            WHERE ISNULL(m.APPROVE_ENABLED,0)=1
              AND LTRIM(RTRIM(ISNULL(m.MASTER_TABLE,N''))) <> N''
              AND (COL_LENGTH(N'dbo.'+LTRIM(RTRIM(m.MASTER_TABLE)),N'CONFIRM_TAG') IS NULL
                OR COL_LENGTH(N'dbo.'+LTRIM(RTRIM(m.MASTER_TABLE)),N'CONFIRM_PERSON') IS NULL
                OR COL_LENGTH(N'dbo.'+LTRIM(RTRIM(m.MASTER_TABLE)),N'CONFIRM_DATE') IS NULL);
            """);
        Assert.Equal(0, broken);
    }

    private async Task SeedModuleAsync(string masterTable)
    {
        await using var connection = new SqlConnection(ConnectionString.Value);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            """
            INSERT INTO dbo.MODULES (M_IDX,M_DESC,M_URL,M_ROOT_IDX,SORT_IDX,M_TAG,MASTER_TABLE,LAST_UPDATE_BY)
            VALUES (@Id,N'批核入口集成测试-临时模块',N'/workbench',@Id,9998,1,@Table,N'integration-test');
            """, connection);
        command.Parameters.Add("@Id", SqlDbType.Int).Value = _moduleId;
        command.Parameters.Add("@Table", SqlDbType.NVarChar, 128).Value = masterTable;
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>两条效果链动作 + 一条自定义按钮，全部启用（批量停用只能碰前两条）。</summary>
    private async Task SeedActionsAsync()
    {
        await using var connection = new SqlConnection(ConnectionString.Value);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            """
            INSERT INTO dbo.MODULE_BUSINESS_ACTION
                (M_IDX,EVENT_CODE,SEQ,EFFECT_KEY,ENABLED,FAIL_MODE,CREATE_PERSON,CREATE_DATE,CONFIRM_TAG)
            VALUES
                (@Id,N'SAVE',1,N'field-accumulate',1,N'BLOCK',N'integration-test',GETDATE(),0),
                (@Id,N'APPROVE_EFFECT',2,N'inventory-move',1,N'BLOCK',N'integration-test',GETDATE(),0),
                (@Id,N'MANUAL',3,N'recalc-account',1,N'BLOCK',N'integration-test',GETDATE(),0);
            """, connection);
        command.Parameters.Add("@Id", SqlDbType.Int).Value = _moduleId;
        await command.ExecuteNonQueryAsync();
    }

    private async Task<int> CountAsync(string sql)
    {
        await using var connection = new SqlConnection(ConnectionString.Value);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@Id", SqlDbType.Int).Value = _moduleId;
        return Convert.ToInt32(await command.ExecuteScalarAsync() ?? 0);
    }

    /// <summary>已登记进 TABLES、且批核三列不齐的第一张表（按表名稳定排序，避免用例时好时坏）。</summary>
    private async Task<string?> FindRegisteredTableMissingApproveColumnsAsync() => await ScalarStringAsync("""
        SELECT TOP 1 t.name
        FROM sys.tables t
        WHERE t.name IN (SELECT LTRIM(RTRIM(T_ID)) FROM dbo.TABLES)
          AND (COL_LENGTH(N'dbo.'+t.name,N'CONFIRM_TAG') IS NULL
            OR COL_LENGTH(N'dbo.'+t.name,N'CONFIRM_PERSON') IS NULL
            OR COL_LENGTH(N'dbo.'+t.name,N'CONFIRM_DATE') IS NULL)
        ORDER BY t.name;
        """);

    private async Task<List<string>> MissingApproveColumnsAsync(string table)
    {
        var missing = new List<string>();
        foreach (var column in WorkflowStates.ApproveColumns)
        {
            var length = await ScalarStringAsync($"SELECT CONVERT(nvarchar(50), COL_LENGTH(N'dbo.{table}', N'{column}'));");
            if (length is null) missing.Add(column);
        }
        return missing;
    }

    private async Task<string?> ScalarStringAsync(string sql)
    {
        await using var connection = new SqlConnection(ConnectionString.Value);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        var value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? null : Convert.ToString(value);
    }

    public void Dispose()
    {
        if (ConnectionString.Value is null) return;
        try
        {
            using var connection = new SqlConnection(ConnectionString.Value);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                DELETE FROM dbo.MODULE_BUSINESS_ACTION WHERE M_IDX=@Id;
                DELETE FROM dbo.WORKBENCH_MODULE_DIRTY WHERE M_IDX=@Id;
                DELETE FROM dbo.MODULES WHERE M_IDX=@Id;
                """;
            command.Parameters.Add("@Id", SqlDbType.Int).Value = _moduleId;
            command.ExecuteNonQuery();
        }
        catch (SqlException)
        {
            // 清理失败不该掩盖用例本身的结论；临时模块编号在 98xxxxxx 段，不参与任何业务。
        }
    }

    private static string? ResolveConnectionString()
    {
        var env = Environment.GetEnvironmentVariable("MSSQL_ERP_CONN");
        if (!string.IsNullOrWhiteSpace(env))
        {
            return env;
        }

        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "config.toml");
            var text = File.ReadAllText(path);
            var match = Regex.Match(text, "\\[mcp_servers\\.mssql\\.env\\][\\s\\S]*?MSSQL_ERP_CONN\\s*=\\s*\"([^\"]+)\"");
            return match.Success && match.Groups[1].Value.Contains("Database=EOS.ERP")
                ? match.Groups[1].Value
                : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}
