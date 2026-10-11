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
/// ADR-033 §6 的**防静默损失**用例。按钮与行为动作同表（`MODULE_BUSINESS_ACTION`，按钮是
/// `EVENT_CODE='MANUAL'` 的行），而写路径原本是"整模块全删全插"——配置面拆开后，两个页面各自保存
/// 就会把另一面删干净，且两边都会标脏、都写审计"保存成功"。这类缺陷各自测各自的域时看不见。
///
/// 这里证明**作用域谓词真的护住了另一面**：存按钮域不碰非按钮动作与校验规则，存动作域不碰按钮。
/// 走真库：判据全在 SQL 谓词与表上，纯单测与门禁都拦不住回归。
/// </summary>
// 真库用例一律进 `live-database` 集合串行跑：本文件会临时增删动作/规则行，
// 与只读计数型用例（如 ConfigOverviewLiveTests）并行时会撞出"行数对不上"的假红。
[Collection("live-database")]
[Trait("Category", "Integration")]
public sealed class ModuleBusinessConfigScopeLiveTests : IDisposable
{
    private const string FakeButtonKey = "zz-scope-test";

    private static readonly Lazy<string?> ConnectionString = new(ResolveConnectionString);

    private readonly ModuleBusinessConfigRepository _config;
    private readonly int _moduleId;

    public ModuleBusinessConfigScopeLiveTests()
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
        // 按钮行的 EFFECT_KEY 必须在注册表里（校验器按 KeySet 判"这个按钮点得动吗"），故注册一个假处理器。
        var documentActions = new DocumentActionRegistry(
            new IDocumentUserAction[] { new FakeButtonAction() }, NullLogger<DocumentActionRegistry>.Instance);
        _config = new ModuleBusinessConfigRepository(
            connections, new WorkbenchDirtyMarker(connections), auditWriter, documentActions,
            new WorkbenchIdempotency(), NullLogger<ModuleBusinessConfigRepository>.Instance);
        _moduleId = 97_000_000 + Random.Shared.Next(0, 9_999_999);
    }

    [Fact]
    public async Task 保存按钮域不动行为动作与校验规则()
    {
        if (ConnectionString.Value is null) return;
        await SeedAsync();

        await SaveScopedAsync(
            new SaveModuleBusinessConfigRequest(
                [new BusinessActionDto(1, "MANUAL", FakeButtonKey, Enabled: true, Label: "测试按钮")],
                []),
            BusinessConfigScope.Manual);

        // 另一面（非 MANUAL 动作 + 全部校验规则）一行都没动
        Assert.Equal(2, await CountAsync(
            "SELECT COUNT(*) FROM dbo.MODULE_BUSINESS_ACTION WHERE M_IDX=@Id AND EVENT_CODE<>N'MANUAL';"));
        Assert.Equal(2, await CountAsync("SELECT COUNT(*) FROM dbo.MODULE_VALIDATION_RULE WHERE M_IDX=@Id;"));
        // 本面按"只替按钮"生效：仍是一行，但换成了这次提交的那行
        Assert.Equal(1, await CountAsync(
            "SELECT COUNT(*) FROM dbo.MODULE_BUSINESS_ACTION WHERE M_IDX=@Id AND EVENT_CODE=N'MANUAL' AND LABEL=N'测试按钮';"));
    }

    [Fact]
    public async Task 保存动作域不动按钮()
    {
        if (ConnectionString.Value is null) return;
        await SeedAsync();

        await SaveScopedAsync(
            new SaveModuleBusinessConfigRequest(
                [new BusinessActionDto(1, "SAVE", "job-enqueue", Enabled: true)],
                []),
            BusinessConfigScope.Actions);

        // 按钮那一面一行都没动（上一用例的镜像：这就是"两面互删"缺陷的防线）
        Assert.Equal(1, await CountAsync(
            "SELECT COUNT(*) FROM dbo.MODULE_BUSINESS_ACTION WHERE M_IDX=@Id AND EVENT_CODE=N'MANUAL';"));
        // 本面被替换：非 MANUAL 由 2 行变成提交的 1 行；校验规则属于本面，随提交替换
        Assert.Equal(1, await CountAsync(
            "SELECT COUNT(*) FROM dbo.MODULE_BUSINESS_ACTION WHERE M_IDX=@Id AND EVENT_CODE<>N'MANUAL';"));
        Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM dbo.MODULE_VALIDATION_RULE WHERE M_IDX=@Id;"));
    }

    [Fact]
    public async Task 全量域仍然整体替换()
    {
        if (ConnectionString.Value is null) return;
        await SeedAsync();

        await SaveScopedAsync(
            new SaveModuleBusinessConfigRequest(
                [new BusinessActionDto(1, "MANUAL", FakeButtonKey, Enabled: true, Label: "只剩按钮")],
                []),
            BusinessConfigScope.All);

        // 合成保存用的 all 语义不变：两面都按提交内容重写
        Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM dbo.MODULE_BUSINESS_ACTION WHERE M_IDX=@Id;"));
        Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM dbo.MODULE_VALIDATION_RULE WHERE M_IDX=@Id;"));
    }

    private async Task SaveScopedAsync(SaveModuleBusinessConfigRequest request, BusinessConfigScope scope)
    {
        await using var connection = new SqlConnection(ConnectionString.Value);
        await connection.OpenAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
        await _config.SaveScopedAsync(
            connection, transaction, _moduleId, request, "scope-test", scope, CancellationToken.None);
        await transaction.CommitAsync();
    }

    /// <summary>临时统一工作台模块（有主表 ⇒ NODE_KIND=WORKBENCH，动作配置才收）+ 2 动作 + 1 按钮 + 2 规则。</summary>
    private async Task SeedAsync()
    {
        await using var connection = new SqlConnection(ConnectionString.Value);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            """
            INSERT INTO dbo.MODULES (M_IDX,M_DESC,M_URL,M_ROOT_IDX,SORT_IDX,M_TAG,MASTER_TABLE,LAST_UPDATE_BY)
            VALUES (@Id,N'配置作用域集成测试-临时模块',N'/workbench',@Id,9997,1,N'ACC_ACCOUNT',N'scope-test');

            INSERT INTO dbo.MODULE_BUSINESS_ACTION
                (M_IDX,EVENT_CODE,SEQ,EFFECT_KEY,LABEL,ENABLED,FAIL_MODE,CREATE_PERSON,CREATE_DATE,CONFIRM_TAG)
            VALUES
                (@Id,N'SAVE',1,N'job-enqueue',NULL,1,N'BLOCK',N'scope-test',GETDATE(),0),
                (@Id,N'APPROVE_EFFECT',2,N'job-enqueue',NULL,1,N'BLOCK',N'scope-test',GETDATE(),0),
                (@Id,N'MANUAL',3,@Button,N'测试按钮',1,N'BLOCK',N'scope-test',GETDATE(),0);

            INSERT INTO dbo.MODULE_VALIDATION_RULE
                (M_IDX,STAGE,SEQ,VALIDATION_KEY,ENABLED,PARAM_STRUCT,CREATE_PERSON,CREATE_DATE)
            VALUES
                (@Id,N'SAVE',1,N'reference-exists',1,N'{}',N'scope-test',GETDATE()),
                (@Id,N'SAVE',2,N'reference-exists',1,N'{}',N'scope-test',GETDATE());
            """, connection);
        command.Parameters.Add("@Id", SqlDbType.Int).Value = _moduleId;
        command.Parameters.Add("@Button", SqlDbType.NVarChar, 120).Value = FakeButtonKey;
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

    public void Dispose()
    {
        if (ConnectionString.Value is null) return;
        try
        {
            using var connection = new SqlConnection(ConnectionString.Value);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                DELETE FROM dbo.MODULE_BUSINESS_ACTION_OP WHERE ACTION_ID IN (SELECT ACTION_ID FROM dbo.MODULE_BUSINESS_ACTION WHERE M_IDX=@Id);
                DELETE FROM dbo.MODULE_BUSINESS_ACTION WHERE M_IDX=@Id;
                DELETE FROM dbo.MODULE_VALIDATION_RULE WHERE M_IDX=@Id;
                DELETE FROM dbo.WORKBENCH_MODULE_DIRTY WHERE M_IDX=@Id;
                DELETE FROM dbo.MODULES WHERE M_IDX=@Id;
                """;
            command.Parameters.Add("@Id", SqlDbType.Int).Value = _moduleId;
            command.ExecuteNonQuery();
        }
        catch (SqlException)
        {
            // 清理失败不掩盖用例结论；临时编号在 97xxxxxx 段，不参与任何业务。
        }
    }

    private sealed class FakeButtonAction : IDocumentUserAction
    {
        public string Key => FakeButtonKey;

        public string Label => "测试按钮";

        public Task<DocumentActionResult> ExecuteAsync(DocumentActionContext context, CancellationToken token) =>
            throw new NotSupportedException("集成测试只验证配置写路径的作用域，不执行按钮。");
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

/// <summary>作用域解析：默认 all、大小写不敏感、未知值必须被判为非法（不静默回退成全量替换）。</summary>
public sealed class BusinessConfigScopesTests
{
    [Theory]
    [InlineData(null, BusinessConfigScope.All)]
    [InlineData("", BusinessConfigScope.All)]
    [InlineData("all", BusinessConfigScope.All)]
    [InlineData("ALL", BusinessConfigScope.All)]
    [InlineData("actions", BusinessConfigScope.Actions)]
    [InlineData("Actions", BusinessConfigScope.Actions)]
    [InlineData("manual", BusinessConfigScope.Manual)]
    [InlineData(" manual ", BusinessConfigScope.Manual)]
    public void 认得的字面量按其语义解析(string? value, BusinessConfigScope expected)
    {
        Assert.True(BusinessConfigScopes.TryParse(value, out var scope));
        Assert.Equal(expected, scope);
    }

    [Theory]
    [InlineData("button")]
    [InlineData("manuals")]
    [InlineData("0")]
    public void 未知字面量判非法_以免拼错变成全量替换(string value)
    {
        Assert.False(BusinessConfigScopes.TryParse(value, out _));
    }
}

/// <summary>
/// 作用域与请求体的一致性守卫（ADR-033 §6 的"显式优于推导"）：越域的行**拒不静默丢弃**——
/// 静默丢弃会让调用方以为存上了，那是"两面互删"的另一种形态。
/// </summary>
public sealed class BusinessConfigScopeGuardTests
{
    private static SaveModuleBusinessConfigRequest Request(
        IReadOnlyList<BusinessActionDto> actions,
        IReadOnlyList<ModuleValidationRuleDto>? rules = null) => new(actions, rules ?? []);

    private static BusinessActionDto Button() => new(1, "MANUAL", "zz-scope-test", Enabled: true, Label: "按钮");

    private static BusinessActionDto Chain() => new(1, "SAVE", "job-enqueue", Enabled: true);

    [Fact]
    public void 按钮域只收按钮行_塞动作行判非法()
    {
        Assert.Throws<ArgumentException>(() =>
            ModuleBusinessConfigValidator.EnsureScopeMatchesRequest(Request([Chain()]), BusinessConfigScope.Manual));
    }

    [Fact]
    public void 按钮域不收校验规则()
    {
        var rules = new List<ModuleValidationRuleDto> { new(1, "SAVE", "reference-exists", true, "{}") };
        Assert.Throws<ArgumentException>(() =>
            ModuleBusinessConfigValidator.EnsureScopeMatchesRequest(Request([Button()], rules), BusinessConfigScope.Manual));
    }

    [Fact]
    public void 动作域不收按钮行()
    {
        Assert.Throws<ArgumentException>(() =>
            ModuleBusinessConfigValidator.EnsureScopeMatchesRequest(Request([Button()]), BusinessConfigScope.Actions));
    }

    [Theory]
    [InlineData(BusinessConfigScope.Manual)]
    [InlineData(BusinessConfigScope.Actions)]
    [InlineData(BusinessConfigScope.All)]
    public void 域内请求体放行(BusinessConfigScope scope)
    {
        var request = scope switch
        {
            BusinessConfigScope.Manual => Request([Button()]),
            BusinessConfigScope.Actions => Request([Chain()]),
            _ => Request([Button(), Chain()]),
        };
        ModuleBusinessConfigValidator.EnsureScopeMatchesRequest(request, scope);
    }
}
