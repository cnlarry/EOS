using EOS.API.Data;
using EOS.API.Features.Assistant.Catalog;
using EOS.API.Features.Assistant.Tools;
using EOS.API.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 能力目录与元数据的一致性（真库）：目录里出现的模块号 / 表，必须逐条在对应的真值来源里找得到。
/// 目录一旦编造、或写死了一个已退役的标识，这里即变红。
/// </summary>
[Trait("Category", "Integration")]
[Collection("live-database")]
public sealed class SystemCapabilityCatalogLiveTests
{
    private static readonly string? ConnectionString =
        Environment.GetEnvironmentVariable("MSSQL_ERP_CONN");

    private static readonly Lazy<(SystemCapabilityCatalog Catalog, FieldAdminRepository Fields)> Fixture =
        new(() => CreateFixture(ConnectionString!));

    private static (SystemCapabilityCatalog Catalog, FieldAdminRepository Fields) CreateFixture(string connectionString)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:ErpDatabase"] = connectionString,
            })
            .Build();
        var connections = new DbConnectionFactory(configuration);
        var fieldAdmin = new FieldAdminRepository(
            connections,
            new WorkbenchDirtyMarker(connections),
            new WorkbenchAuditWriter(connections, new HttpContextAccessor(),
                new WorkbenchDefinitionProvider(connections, NullLogger<WorkbenchDefinitionProvider>.Instance),
                Options.Create(new AuditSettings())),
            new RestrictedExpressionService(connections),
            new WorkbenchIdempotency(),
            NullLogger<FieldAdminRepository>.Instance);
        var catalog = new SystemCapabilityCatalog(
            new AssistantSchemaGateway(fieldAdmin, connections),
            Options.Create(new UnifiedFormEditorSettings()));
        return (catalog, fieldAdmin);
    }

    private static IReadOnlyList<string> FactsOf(CapabilityExplanation explanation, CapabilityFactKind kind) =>
        [.. explanation.Facts.Where(fact => fact.Kind == kind).Select(fact => fact.Value)];

    [Fact]
    public async Task 目录里的模块号都真实存在于模块元数据()
    {
        if (string.IsNullOrWhiteSpace(ConnectionString)) return;
        var (catalog, fields) = Fixture.Value;

        var named = FactsOf(await catalog.DescribeAsync(CapabilityTopic.Module, CancellationToken.None),
            CapabilityFactKind.Module);
        Assert.NotEmpty(named);

        var real = (await fields.GetModulesAsync(CancellationToken.None))
            .Select(module => module.Id.ToString())
            .ToHashSet(StringComparer.Ordinal);

        var orphans = named.Where(value => !real.Contains(value))
            .OrderBy(value => value, StringComparer.Ordinal).ToList();
        Assert.True(orphans.Count == 0,
            "目录里出现了模块元数据中不存在的模块号：" + string.Join("、", orphans));
    }

    [Fact]
    public async Task 表单主题的模块号都真实存在于模块元数据()
    {
        if (string.IsNullOrWhiteSpace(ConnectionString)) return;
        var (catalog, fields) = Fixture.Value;

        var named = FactsOf(await catalog.DescribeAsync(CapabilityTopic.Form, CancellationToken.None),
            CapabilityFactKind.Module);
        var real = (await fields.GetModulesAsync(CancellationToken.None))
            .Select(module => module.Id.ToString())
            .ToHashSet(StringComparer.Ordinal);

        var orphans = named.Where(value => !real.Contains(value)).ToList();
        Assert.True(orphans.Count == 0,
            "表单名单里出现了模块元数据中不存在的模块号（写名单/只读名单须随模块退役一起清理）："
            + string.Join("、", orphans));
    }

    [Fact]
    public async Task 目录里的表都真实存在于表元数据()
    {
        if (string.IsNullOrWhiteSpace(ConnectionString)) return;
        var (catalog, fields) = Fixture.Value;

        var facts = FactsOf(await catalog.DescribeAsync(CapabilityTopic.Fields, CancellationToken.None),
            CapabilityFactKind.Table);
        Assert.NotEmpty(facts);

        var configured = (await fields.GetTablesAsync(null, CancellationToken.None))
            .Select(table => table.TableId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var orphans = facts.Where(value => !configured.Contains(value)).ToList();
        Assert.True(orphans.Count == 0, "目录里出现了表元数据中不存在的表：" + string.Join("、", orphans));
    }
}
