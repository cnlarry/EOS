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
}
