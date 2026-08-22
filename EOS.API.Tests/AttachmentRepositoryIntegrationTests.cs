using System.Reflection;
using System.Text.RegularExpressions;
using DbUp;
using EOS.API.Data;
using EOS.API.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// ATTACHMENT 附件仓储集成测试：直连 EOS.ERP 开发库验证真实 SQL（创建/列表/改备注/删除/项次自增）。
/// 连接串来自 env EOS_ERP_TEST_CONNECTION 或本机 Codex 配置；拿不到连接串时测试空跑跳过。
/// 测试前置用与生产一致的 DbUp 迁移创建/升级 ATTACHMENT 表；测试数据在 Dispose 中清理。
/// </summary>
[Trait("Category", "Integration")]
public sealed class AttachmentRepositoryIntegrationTests : IDisposable
{
    private static readonly Lazy<string?> ConnectionString = new(ResolveConnectionString);

    private readonly AttachmentRepository _attachments;
    private readonly List<long> _createdIds = [];

    public AttachmentRepositoryIntegrationTests()
    {
        EnsureSchema();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:ErpDatabase"] = ConnectionString.Value,
            })
            .Build();
        var options = Options.Create(new AttachmentSettings
        {
            StorageRoot = Path.Combine(Path.GetTempPath(), "eos-attachment-test"),
            MaxSizeBytes = 50L * 1024 * 1024,
            AllowedExtensions = ".pdf;.doc;.docx",
        });
        _attachments = new AttachmentRepository(new DbConnectionFactory(config), options);
    }

    /// <summary>测试前置：用与生产一致的 DbUp 迁移创建/升级 EOS.ERP ATTACHMENT 表。</summary>
    private static void EnsureSchema()
    {
        var connectionString = ConnectionString.Value;
        if (connectionString is null)
        {
            return;
        }

        var result = DeployChanges.To
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
            throw new InvalidOperationException("测试前置：EOS.ERP ATTACHMENT 迁移失败", result.Error);
        }
    }

    public void Dispose()
    {
        if (ConnectionString.Value is null)
        {
            return;
        }

        try
        {
            using var connection = new DbConnectionFactory(
                new ConfigurationBuilder()
                    .AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["ConnectionStrings:ErpDatabase"] = ConnectionString.Value,
                    })
                    .Build()).Create();
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = $"DELETE FROM dbo.ATTACHMENT WHERE ID IN ({string.Join(',', _createdIds)});";
            command.ExecuteNonQuery();
        }
        catch
        {
            // 清理失败不影响测试结论；数据残留仅限开发库
        }
    }

    [Fact]
    public async Task Create_List_GetSerial_Increases()
    {
        if (ConnectionString.Value is null)
        {
            return;
        }

        var key = $"[\"{Guid.NewGuid():N}\"]";
        var first = await _attachments.CreateAsync(
            1209, "PRODUCT_EDITION", key, 1, "1.pdf", "合同.pdf", "application/pdf", 1024,
            "abc", "说明", "u1", "张三", CancellationToken.None);
        Track(first.Id);
        Assert.Equal(1, first.SerialNo);
        Assert.Equal("合同.pdf", first.ClientFileName);
        Assert.Equal("u1", first.UploadedBy);

        var next = await _attachments.GetNextSerialAsync(1209, "PRODUCT_EDITION", key, CancellationToken.None);
        Assert.Equal(2, next);

        var created = await _attachments.CreateAsync(
            1209, "PRODUCT_EDITION", key, next, "2.pdf", "报价单.pdf", "application/pdf", 2048,
            "def", null, "u1", "张三", CancellationToken.None);
        Track(created.Id);
        Assert.Equal(2, created.SerialNo);

        var list = await _attachments.ListAsync(1209, "PRODUCT_EDITION", key, CancellationToken.None);
        Assert.Equal(2, list.Count);
        Assert.Equal([1, 2], list.Select(item => item.SerialNo).OrderBy(value => value).ToArray());
    }

    [Fact]
    public async Task UpdateRemark_And_Delete_Work()
    {
        if (ConnectionString.Value is null)
        {
            return;
        }

        var key = $"[\"{Guid.NewGuid():N}\"]";
        var created = await _attachments.CreateAsync(
            1209, "PRODUCT_EDITION", key, 1, "1.pdf", "订单.pdf", "application/pdf", 1024,
            "abc", "旧备注", "u1", "张三", CancellationToken.None);
        Track(created.Id);

        var updated = await _attachments.UpdateRemarkAsync(created.Id, "客户邮件原文", CancellationToken.None);
        Assert.NotNull(updated);
        Assert.Equal("客户邮件原文", updated!.Remark);

        var deleted = await _attachments.DeleteAsync(created.Id, CancellationToken.None);
        Assert.NotNull(deleted);
        Assert.Equal("客户邮件原文", deleted!.Remark);
        Assert.Null(await _attachments.GetAsync(created.Id, CancellationToken.None));
    }

    [Fact]
    public void ResolveRelativePath_IsScopedAndSanitized()
    {
        var key = "[\"A-1\"]";
        var dto = new AttachmentDto(
            1, 1209, "PRODUCT_EDITION", key, 1, "1.pdf", "订单.pdf", "application/pdf", 1024,
            "abc", null, "u1", "张三", DateTime.UtcNow);
        var relative = _attachments.ResolveRelativePath(dto);
        Assert.StartsWith("PRODUCT_EDITION/", relative);
        Assert.EndsWith("/1.pdf", relative);
        Assert.DoesNotContain(Path.DirectorySeparatorChar == '/' ? "\\" : "/", relative.Split('/')[0]);
    }

    private void Track(long id) => _createdIds.Add(id);

    private static string? ResolveConnectionString()
    {
        var env = Environment.GetEnvironmentVariable("EOS_ERP_TEST_CONNECTION");
        if (!string.IsNullOrWhiteSpace(env))
        {
            return env;
        }

        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "config.toml");
            var text = File.ReadAllText(path);
            var match = Regex.Match(text, "\\[mcp_servers\\.mssql-erp\\.env\\][\\s\\S]*?MSSQL_CONNECTION_STRING\\s*=\\s*\"([^\"]+)\"");
            return match.Success ? match.Groups[1].Value : null;
        }
        catch
        {
            return null;
        }
    }
}