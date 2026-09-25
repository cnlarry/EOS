using System.Text.RegularExpressions;
using EOS.API.Data;
using EOS.API.Data.Forms;
using EOS.API.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 设计态读态的真库用例：版式行与字段池构成一个划分、锁定规则命中主键与必填、
/// 套用来源只列同主表模块、设计权按用户判定。
///
/// 只读，不写库；保存与"保存即重发布"由端到端脚本 `EOS.API.Tests/FormLayoutDesignE2E.ps1` 覆盖
/// （那条路径要经过发布校验器与快照表，走真实接口更能说明问题）。
/// </summary>
[Collection("live-database")]
public sealed class FormLayoutDesignLiveTests
{
    private const int ModuleId = 1405;                     // 客户订单（COP_ORDER_M / COP_ORDER_D）
    private const string MasterTable = "COP_ORDER_M";
    private const string DesignUser = "admin";

    private static readonly Lazy<string?> ConnectionString = new(ResolveConnectionString);

    private static string? ResolveConnectionString()
    {
        var env = Environment.GetEnvironmentVariable("EOS_ERP_TEST_CONNECTION");
        if (!string.IsNullOrWhiteSpace(env)) return env;
        try
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "config.toml");
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

    private static DbConnectionFactory Connections()
    {
        if (ConnectionString.Value is null)
        {
            throw new InvalidOperationException("真库集成测试需要 EOS_ERP_TEST_CONNECTION；未配置即失败（无连接跳过≠已验证）。");
        }
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:ErpDatabase"] = ConnectionString.Value,
            })
            .Build();
        return new DbConnectionFactory(config);
    }

    /// <summary>读态用例只碰读路径，快照服务不参与（保存路径由端到端脚本覆盖）。</summary>
    private static FormLayoutRepository Repository(DbConnectionFactory connections)
        => new(connections, new WorkbenchDirtyMarker(connections), null!, new WorkbenchIdempotency(), null!,
            NullLogger<FormLayoutRepository>.Instance);

    private static ModuleRights AllVisibleRights() => new(
        CanBrowse: true, CanViewCost: true, CanViewSecrecy: true, CanSetup: true,
        DeniedMasterFields: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
        DeniedDetailFields: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
        CanAddNew: true, CanEdit: true, CanDelete: true, CanApprove: true, CanDeapprove: true,
        CanEndCase: true, CanUnEndCase: true, CanFileView: true, CanFileUpda: true, CanFileEdit: true,
        CanFileDele: true,
        DenyNewMasterFields: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
        DenyNewDetailFields: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
        DenyModiMasterFields: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
        DenyModiDetailFields: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
        DataFilter: string.Empty, ExecuteTag: "Z");

    [Fact]
    public async Task DesignState_PartitionsRegisteredFieldsBetweenLayoutAndPool()
    {
        var repository = Repository(Connections());

        var state = await repository.ReadDesignStateAsync(ModuleId, AllVisibleRights(), CancellationToken.None);

        Assert.NotNull(state);
        Assert.Equal(MasterTable, state!.MasterTable);
        Assert.NotNull(state.DetailTable);
        Assert.True(state.Columns >= 1);
        Assert.Contains(state.Tabs, tab => tab.No == 1);
        Assert.NotEmpty(state.Master.Layout);
        Assert.Empty(state.Master.Pool);          // 零配置：推导默认即"全部已排"

        var layoutKeys = state.Master.Layout.Select(row => row.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.All(state.Master.Layout, row => Assert.False(string.IsNullOrWhiteSpace(row.Label)));
        Assert.All(state.Master.Layout, row => Assert.InRange(row.Span, 1, state.Columns));
        Assert.All(state.Master.Layout, row => Assert.InRange(row.RowSpan, 1, FormLayoutDerivation.MaxRowSpan));
        // 版式行不允许重复字段（主键约束），这里再钉一次读取侧不重不漏
        Assert.Equal(layoutKeys.Count, state.Master.Layout.Count);
    }

    [Fact]
    public async Task DesignState_LocksPrimaryKeyAndUserFillableRequiredFields()
    {
        var repository = Repository(Connections());

        var state = await repository.ReadDesignStateAsync(ModuleId, AllVisibleRights(), CancellationToken.None);

        var primary = state!.Master.Layout.Where(row => row.IsPrimaryKey).ToList();
        Assert.NotEmpty(primary);
        Assert.All(primary, row =>
        {
            Assert.True(row.Locked);
            Assert.Equal("主键列，始终显示", row.LockReason);
        });

        var required = state.Master.Layout.Where(row => row.Locked && !row.IsPrimaryKey).ToList();
        Assert.NotEmpty(required);
        Assert.All(required, row => Assert.False(string.IsNullOrWhiteSpace(row.LockReason)));

        // 服务端自填的审计列不是"用户可填的必填"，因此仍可隐藏（ADR 例外条款）
        var lifecycle = state.Master.Layout.FirstOrDefault(row => row.Key is "CREATE_PERSON" or "LAST_UPDATE_BY");
        if (lifecycle is not null)
        {
            Assert.Equal("单据系统列，始终显示", lifecycle.LockReason);
        }
    }

    [Fact]
    public async Task Templates_ListOnlyModulesSharingTheSameMasterTable()
    {
        var repository = Repository(Connections());

        var templates = await repository.ReadTemplatesAsync(ModuleId, CancellationToken.None);

        var expected = await ReadSameMasterModulesAsync();
        Assert.Equal(expected, templates.Select(template => template.ModuleId).OrderBy(id => id).ToArray());
        Assert.All(templates, template => Assert.Equal(MasterTable, template.MasterTable));
    }

    [Fact]
    public async Task CanDesign_FollowsTheDesignPermissionBit()
    {
        var repository = Repository(Connections());

        Assert.True(await repository.CanDesignAsync(DesignUser, ModuleId, CancellationToken.None));
        Assert.False(await repository.CanDesignAsync("__no_such_user__", ModuleId, CancellationToken.None));
    }

    private static async Task<int[]> ReadSameMasterModulesAsync()
    {
        await using var connection = Connections().Create();
        await connection.OpenAsync();
        await using var command = new Microsoft.Data.SqlClient.SqlCommand(
            "SELECT M_IDX FROM dbo.MODULES WHERE LTRIM(RTRIM(ISNULL(MASTER_TABLE,''))) = @Table AND M_IDX <> @ModuleId ORDER BY M_IDX;",
            connection);
        command.Parameters.Add("@Table", System.Data.SqlDbType.VarChar, 100).Value = MasterTable;
        command.Parameters.Add("@ModuleId", System.Data.SqlDbType.Int).Value = ModuleId;
        await using var reader = await command.ExecuteReaderAsync();
        var ids = new List<int>();
        while (await reader.ReadAsync())
        {
            ids.Add(reader.GetInt32(0));
        }
        return ids.OrderBy(id => id).ToArray();
    }
}
