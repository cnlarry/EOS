using EOS.API.Data;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 助手设置表（<c>dbo.ASSISTANT_SETTING</c>）的真库集成测试——见 ADR-030 §8 与迁移 293。
///
/// <para>
/// 验的是"缺行 = 代码默认值"这条语义在**存储层**也成立：写入 → 读回 → 删除 → 回到缺行。
/// 后置迁移会真的建表，所以这些断言同时也在验证迁移本身。
/// </para>
///
/// <para>
/// 清理只删自己写入的键（用测试专用前缀），**不碰管理员真实配过的参数**。
/// </para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class AssistantSettingStoreIntegrationTests : IDisposable
{
    private static readonly Lazy<string?> ConnectionString = new(
        () => Environment.GetEnvironmentVariable("MSSQL_ERP_CONN"));

    private readonly AssistantSettingStore _store;
    private readonly List<string> _keys = [];

    public AssistantSettingStoreIntegrationTests()
    {
        EnsureSchema();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:ErpDatabase"] = ConnectionString.Value,
            })
            .Build();
        _store = new AssistantSettingStore(new DbConnectionFactory(config));
    }

    private static void EnsureSchema()
    {
        var connectionString = ConnectionString.Value;
        if (connectionString is null)
        {
            return;
        }

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
        if (!result.Successful)
        {
            throw new InvalidOperationException("测试前置：EOS.ERP 助手设置表迁移失败", result.Error);
        }
    }

    /// <summary>测试专用键前缀：清理时只删自己写的，不去动管理员真实配过的参数。</summary>
    private string NewKey()
    {
        var key = $"eosdev-probe-{Guid.NewGuid():N}"[..24];
        _keys.Add(key);
        return key;
    }

    [Fact]
    public async Task Upsert_Read_Delete_Roundtrip()
    {
        if (ConnectionString.Value is null) return;
        var token = CancellationToken.None;

        var key = NewKey();
        await _store.UpsertAsync(key, "第一次", "string", "集成测试用", "eosdev-test", token);

        var afterInsert = await _store.ListAsync(token);
        var row = afterInsert.Single(item => item.ParamKey == key);
        Assert.Equal("第一次", row.ParamValue);
        Assert.Equal("eosdev-test", row.UpdatedBy);

        // 再写一次走的是 UPDATE 分支：并发保存同一个键不该抛主键冲突
        await _store.UpsertAsync(key, "第二次", "string", "集成测试用", "eosdev-test", token);
        var afterUpdate = await _store.ListAsync(token);
        Assert.Equal("第二次", afterUpdate.Single(item => item.ParamKey == key).ParamValue);
        Assert.Single(afterUpdate.Where(item => item.ParamKey == key));

        // 删除 = 恢复默认（缺行）
        Assert.True(await _store.DeleteAsync(key, token));
        _keys.Remove(key);
        var afterDelete = await _store.ListAsync(token);
        Assert.DoesNotContain(afterDelete, item => item.ParamKey == key);
        // 再删一次：没命中行也要如实返回 false，而不是假装成功
        Assert.False(await _store.DeleteAsync(key, token));
    }

    /// <summary>
    /// 空值与非 ASCII 值都要能原样往返：提示词是长中文文本，被截断或转码坏掉会很难发现
    /// （表现为"模型突然不守格式约定了"）。
    /// </summary>
    [Fact]
    public async Task Long_Unicode_Value_Survives_The_Roundtrip()
    {
        if (ConnectionString.Value is null) return;
        var token = CancellationToken.None;

        var key = NewKey();
        var text = string.Concat(Enumerable.Repeat("回答用 Markdown；字段名用 `反引号`。", 40));
        await _store.UpsertAsync(key, text, "string", null, "eosdev-test", token);

        var row = (await _store.ListAsync(token)).Single(item => item.ParamKey == key);
        Assert.Equal(text, row.ParamValue);
    }

    public void Dispose()
    {
        var connectionString = ConnectionString.Value;
        if (connectionString is null || _keys.Count == 0) return;
        using var conn = new Microsoft.Data.SqlClient.SqlConnection(connectionString);
        conn.Open();
        foreach (var key in _keys)
        {
            using var cmd = new Microsoft.Data.SqlClient.SqlCommand(
                "DELETE FROM dbo.ASSISTANT_SETTING WHERE PARAM_KEY = @Key;", conn);
            cmd.Parameters.AddWithValue("@Key", key);
            cmd.ExecuteNonQuery();
        }
    }
}
