using EOS.API.Data;
using EOS.API.Data.DocumentActions;
using EOS.API.Data.DocumentActions.Handlers;
using EOS.API.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 2301 配置读侧的只读数据通道真库校验：字段中文名与定位键候选。
///
/// 为什么需要这条：两者都是纯 SQL 读取，编译与单测都发现不了 SQL 层的错
/// （如多语句共享 CTE 的作用域问题、主副表解析写错），只在页面打开那一刻炸 500 或给出空标签。
/// 这里按"逐键与既有元数据对应"钉住，而不是断言某个中文词。
/// </summary>
[Collection("live-database")]
public sealed class ModuleBusinessConfigReadLiveTests
{
    private static readonly string? ConnectionString =
        Environment.GetEnvironmentVariable("MSSQL_ERP_CONN");

    private const int ReceiveModuleId = 1607;

    private static ModuleBusinessConfigRepository CreateRepository(string connectionString)
    {
        var connections = new DbConnectionFactory(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:ErpDatabase"] = connectionString,
            })
            .Build());
        var dirtyMarker = new WorkbenchDirtyMarker(connections);
        var auditWriter = new WorkbenchAuditWriter(connections, new HttpContextAccessor(),
            new WorkbenchDefinitionProvider(connections, NullLogger<WorkbenchDefinitionProvider>.Instance),
            Options.Create(new AuditSettings()));
        return new ModuleBusinessConfigRepository(connections, dirtyMarker, auditWriter,
            new DocumentActionRegistry([new DocumentActionProbeHandler()], NullLogger<DocumentActionRegistry>.Instance),
            NullLogger<ModuleBusinessConfigRepository>.Instance);
    }

    [Fact]
    public async Task 字段中文名按模块范围返回且与_FIELDS_元数据逐键一致()
    {
        if (string.IsNullOrWhiteSpace(ConnectionString)) return;
        var token = CancellationToken.None;
        var repository = CreateRepository(ConnectionString);

        var labels = await repository.GetFieldLabelsAsync(ReceiveModuleId, token);

        // 模块自身的操作主/副表与配置里出现的效果表都应在范围内。
        Assert.Contains("PUR_RECEIVE_M", labels.Tables.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("PUR_RECEIVE_D", labels.Tables.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("PUR_PURCHASE_D", labels.Tables.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.NotEmpty(labels.Fields);

        // 逐键与库内 FIELDS 对拍：读侧不得丢键、错位或写空标签。
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(token);
        var expected = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        await using (var command = new SqlCommand(
            """
            SELECT LTRIM(RTRIM(f.T_ID)) + '.' + LTRIM(RTRIM(f.F_ID)), LTRIM(RTRIM(f.F_DESC))
            FROM dbo.FIELDS f WITH (NOLOCK)
            WHERE f.F_DESC IS NOT NULL AND LTRIM(RTRIM(f.F_DESC))<>''
              AND LTRIM(RTRIM(f.T_ID)) IN ('PUR_RECEIVE_M','PUR_RECEIVE_D','PUR_PURCHASE_D');
            """, connection))
        {
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
                expected[reader.GetString(0)] = reader.GetString(1);
        }

        Assert.NotEmpty(expected);
        foreach (var (key, value) in expected)
        {
            Assert.True(labels.Fields.TryGetValue(key, out var actual), $"缺少字段中文名：{key}");
            Assert.Equal(value, actual);
        }
    }

    [Fact]
    public async Task 定位键候选只返回目标表命中且来源为主副表的已登记边组()
    {
        if (string.IsNullOrWhiteSpace(ConnectionString)) return;
        var token = CancellationToken.None;
        var repository = CreateRepository(ConnectionString);

        var groups = await repository.GetMatchRelationsAsync(ReceiveModuleId, "PUR_PURCHASE_D", null, token);

        Assert.NotEmpty(groups);
        foreach (var group in groups)
        {
            Assert.NotEmpty(group.Keys);
            foreach (var key in group.Keys)
            {
                Assert.Equal("PUR_PURCHASE_D", key.ToTable, ignoreCase: true);
                Assert.True(
                    key.FromTable.Equals("PUR_RECEIVE_M", StringComparison.OrdinalIgnoreCase)
                    || key.FromTable.Equals("PUR_RECEIVE_D", StringComparison.OrdinalIgnoreCase),
                    $"来源表 {key.FromTable} 不是本模块主/副表");
            }
        }

        // 与库内效果边登记同源：键序按 KEY_ORDINAL 升序。
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(token);
        await using var command = new SqlCommand(
            """
            SELECT COUNT(*) FROM dbo.FIELD_RELATION WITH (NOLOCK)
            WHERE RELATION_KIND=N'EFFECT' AND TO_TABLE=N'PUR_PURCHASE_D'
              AND FROM_TABLE IN (N'PUR_RECEIVE_M',N'PUR_RECEIVE_D');
            """, connection);
        var expectedKeys = Convert.ToInt32(await command.ExecuteScalarAsync(token));
        Assert.Equal(expectedKeys, groups.Sum(group => group.Keys.Count));
    }

    [Fact]
    public async Task 目标表不参与已登记关系时返回空集合而不是报错()
    {
        if (string.IsNullOrWhiteSpace(ConnectionString)) return;
        var repository = CreateRepository(ConnectionString);

        var groups = await repository.GetMatchRelationsAsync(ReceiveModuleId, "MODULES", null, CancellationToken.None);

        Assert.Empty(groups);
    }

    /// <summary>
    /// 事件使用次数是"入门路径怎么对待各事件"判据的一半，另一半是事件本身接不接得到效果链
    /// （<see cref="BusinessActionCatalog.InertEvents"/>）。两者合起来才做对界面：
    /// 结案与取消结案都已有派发点且库内**都有行** ⇒ 都保持可选、都不再标"配了不跑"。
    ///
    /// 这条事实会随配置漂移（谁删掉一行就变了），所以让它有守卫，而不是靠人记得。
    /// </summary>
    [Fact]
    public async Task 事件使用次数_结案与取消结案都有行()
    {
        if (string.IsNullOrWhiteSpace(ConnectionString)) return;
        var repository = CreateRepository(ConnectionString);

        var usage = await repository.CountEventUsageAsync(CancellationToken.None);

        Assert.True(usage.TryGetValue("ENDCASE", out var endcase) && endcase >= 1,
            "库内 ENDCASE 行数为 0：结案钩子（来源结案释放预留）就没有承载行，先看 Migrations/246 是否落库。");
        Assert.True(usage.TryGetValue("UNENDCASE", out var unendcase) && unendcase >= 1,
            "库内 UNENDCASE 行数为 0：取消结案收不回结案释放的预留，先看 Migrations/266 是否落库。");
        // 会跑的事件必须有行，否则"事件下拉里出现过、库里却没人用"这件事说明别处也有洞。
        Assert.True(usage.TryGetValue("APPROVE_EFFECT", out var approve) && approve > 0);
        Assert.True(usage.TryGetValue("SAVE", out var save) && save > 0);
    }
}
