using System.Text.Json;
using EOS.API.Models;
using EOS.API.Security;
using EOS.API.Features.Assistant.Tools;
using Xunit;

namespace EOS.API.Tests;

public sealed class AssistantSchemaToolTests
{
    private sealed class FakePermissions(bool canSetup) : IPermissionService
    {
        public Task<ModulePermission> GetAsync(string userId, int moduleId, CancellationToken cancellationToken) =>
            Task.FromResult(new ModulePermission(Rights(canSetup)));

        public Task<ModulePermission> RequireAsync(string userId, int moduleId, PermissionAction action, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FakeSchema : IAssistantSchemaGateway
    {
        public Task<IReadOnlyList<FieldAdminTable>> ListTablesAsync(string? kind, CancellationToken token) =>
            Task.FromResult<IReadOnlyList<FieldAdminTable>>([
                new("CLIENT", "客户", "P", "TABLE", 2, 1, 0),
            ]);

        public Task<(FieldAdminTableDetail? Table, IReadOnlyList<FieldAdminFieldSummary> Fields, IReadOnlyList<FieldAdminUnmanagedField> Unmanaged)> DescribeTableAsync(string tableId, CancellationToken token) =>
            Task.FromResult<(FieldAdminTableDetail?, IReadOnlyList<FieldAdminFieldSummary>, IReadOnlyList<FieldAdminUnmanagedField>)>(
                (new FieldAdminTableDetail(tableId, "客户", "P", "TABLE", null, null, null, null, null, null, null, null, null, false, null, null),
                [new FieldAdminFieldSummary(tableId, "CLIENT_ID", "客户编号", "nvarchar", false, true, true, true, false, false, false, true, true)],
                [new FieldAdminUnmanagedField("TMP_FLAG", "bit")]));

        public Task<IReadOnlyList<AssistantSchemaView>> ListViewsAsync(string? keyword, CancellationToken token) =>
            Task.FromResult<IReadOnlyList<AssistantSchemaView>>([new("V_CLIENT")]);

        public Task<IReadOnlyList<AssistantSchemaProcedure>> ListProceduresAsync(string? keyword, CancellationToken token) =>
            Task.FromResult<IReadOnlyList<AssistantSchemaProcedure>>([new("P_CLIENT", "P_CLIENT(@CLIENT_ID nvarchar)")]);
    }

    private static ModuleRights Rights(bool canSetup) => new(
        CanBrowse: false, CanViewCost: false, CanViewSecrecy: false, CanSetup: canSetup,
        DeniedMasterFields: new HashSet<string>(), DeniedDetailFields: new HashSet<string>(),
        CanAddNew: false, CanEdit: false, CanDelete: false, CanApprove: false, CanDeapprove: false,
        CanEndCase: false, CanUnEndCase: false, CanFileView: false, CanFileUpda: false,
        CanFileEdit: false, CanFileDele: false, DenyNewMasterFields: new HashSet<string>(),
        DenyNewDetailFields: new HashSet<string>(), DenyModiMasterFields: new HashSet<string>(),
        DenyModiDetailFields: new HashSet<string>(), DataFilter: string.Empty, ExecuteTag: "A");

    [Fact]
    public async Task SchemaTools_FailClosed_WithoutCanSetup()
    {
        var schema = new FakeSchema();
        var permissions = new FakePermissions(false);
        var args = JsonDocument.Parse("{}").RootElement;

        Assert.False((await new ListTablesTool(schema, permissions).ExecuteAsync("u1", args, CancellationToken.None)).Ok);
        Assert.False((await new ListViewsTool(schema, permissions).ExecuteAsync("u1", args, CancellationToken.None)).Ok);
        Assert.False((await new ListProceduresTool(schema, permissions).ExecuteAsync("u1", args, CancellationToken.None)).Ok);
    }

    [Fact]
    public async Task SchemaTools_ReturnCompressedMetadata_WithCanSetup()
    {
        var schema = new FakeSchema();
        var permissions = new FakePermissions(true);

        var tables = await new ListTablesTool(schema, permissions).ExecuteAsync("u1", JsonDocument.Parse("{}").RootElement, CancellationToken.None);
        var table = await new DescribeTableTool(schema, permissions).ExecuteAsync("u1",
            JsonSerializer.SerializeToElement(new { table_id = "CLIENT" }), CancellationToken.None);
        var views = await new ListViewsTool(schema, permissions).ExecuteAsync("u1", JsonDocument.Parse("{}").RootElement, CancellationToken.None);
        var procedures = await new ListProceduresTool(schema, permissions).ExecuteAsync("u1", JsonDocument.Parse("{}").RootElement, CancellationToken.None);

        Assert.Contains("CLIENT 客户", tables.ContentForModel);
        Assert.Contains("CLIENT_ID 客户编号", table.ContentForModel);
        Assert.Contains("TMP_FLAG", table.ContentForModel);
        Assert.Contains("V_CLIENT", views.ContentForModel);
        Assert.Contains("P_CLIENT(@CLIENT_ID nvarchar)", procedures.ContentForModel);
    }

    [Fact]
    public async Task ListTables_ShowsUnmanagedAndOrphanCounts()
    {
        var result = await new ListTablesTool(new FakeSchema(), new FakePermissions(true)).ExecuteAsync(
            "u1", JsonDocument.Parse("{}").RootElement, CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Contains("未纳管 1", result.ContentForModel);
        Assert.Contains("幽灵 0", result.ContentForModel);
    }

    [Fact]
    public async Task DescribeTable_DeniesMissingTable()
    {
        var schema = new MissingTableSchema();
        var result = await new DescribeTableTool(schema, new FakePermissions(true)).ExecuteAsync(
            "u1", JsonSerializer.SerializeToElement(new { table_id = "NOPE" }), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("不存在", result.ContentForModel);
    }

    private sealed class MissingTableSchema : IAssistantSchemaGateway
    {
        public Task<IReadOnlyList<FieldAdminTable>> ListTablesAsync(string? kind, CancellationToken token) =>
            Task.FromResult<IReadOnlyList<FieldAdminTable>>([]);

        public Task<(FieldAdminTableDetail? Table, IReadOnlyList<FieldAdminFieldSummary> Fields, IReadOnlyList<FieldAdminUnmanagedField> Unmanaged)> DescribeTableAsync(string tableId, CancellationToken token) =>
            Task.FromResult<(FieldAdminTableDetail?, IReadOnlyList<FieldAdminFieldSummary>, IReadOnlyList<FieldAdminUnmanagedField>)>(
                (null, [], []));

        public Task<IReadOnlyList<AssistantSchemaView>> ListViewsAsync(string? keyword, CancellationToken token) =>
            Task.FromResult<IReadOnlyList<AssistantSchemaView>>([]);

        public Task<IReadOnlyList<AssistantSchemaProcedure>> ListProceduresAsync(string? keyword, CancellationToken token) =>
            Task.FromResult<IReadOnlyList<AssistantSchemaProcedure>>([]);
    }

    [Fact]
    public async Task DescribeTable_RejectsInvalidIdentifier()
    {
        var result = await new DescribeTableTool(new FakeSchema(), new FakePermissions(true)).ExecuteAsync(
            "u1", JsonSerializer.SerializeToElement(new { table_id = "CLIENT;DROP TABLE CLIENT" }), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("非法表名", result.ContentForModel);
    }
}
