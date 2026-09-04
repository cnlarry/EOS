using System.Text.Json;
using EOS.API.Features.Assistant.Tools;
using EOS.API.Models;
using EOS.API.Security;
using Xunit;

namespace EOS.API.Tests;

public sealed class ModulePlanToolTests
{
    private sealed class FakePermissions(bool canSetup) : IPermissionService
    {
        public Task<ModulePermission> GetAsync(string userId, int moduleId, CancellationToken cancellationToken) =>
            Task.FromResult(new ModulePermission(Rights(canSetup)));

        public Task<ModulePermission> RequireAsync(string userId, int moduleId, PermissionAction action, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        private static ModuleRights Rights(bool canSetup) => new(
            CanBrowse: true, CanViewCost: false, CanViewSecrecy: false, CanSetup: canSetup,
            DeniedMasterFields: new HashSet<string>(), DeniedDetailFields: new HashSet<string>(),
            CanAddNew: false, CanEdit: false, CanDelete: false, CanApprove: false, CanDeapprove: false,
            CanEndCase: false, CanUnEndCase: false, CanFileView: false, CanFileUpda: false,
            CanFileEdit: false, CanFileDele: false, DenyNewMasterFields: new HashSet<string>(),
            DenyNewDetailFields: new HashSet<string>(), DenyModiMasterFields: new HashSet<string>(),
            DenyModiDetailFields: new HashSet<string>(), DataFilter: string.Empty, ExecuteTag: "A");
    }

    private sealed class FakeCatalog : IModulePlanCatalog
    {
        public int ReadCalls { get; private set; }

        public Task<IReadOnlyList<FieldAdminTable>> ListTablesAsync(CancellationToken token)
        {
            ReadCalls++;
            return Task.FromResult<IReadOnlyList<FieldAdminTable>>(
                [new("COP_ORDER_M", "客户订单", "P", "TABLE", 3, 1, 1)]);
        }

        public Task<IReadOnlyList<FieldAdminFieldSummary>> ListFieldsAsync(string tableId, CancellationToken token)
        {
            ReadCalls++;
            return Task.FromResult<IReadOnlyList<FieldAdminFieldSummary>>(
            [
                new(tableId, "ORDER_NO", "订单号", "nvarchar", false, true, true, true, false, false, false, true, true),
                new(tableId, "OLD_TEL", "旧电话", "nvarchar", false, true, false, false, false, false, false, false, false),
            ]);
        }

        public Task<IReadOnlyList<FieldAdminUnmanagedField>> ListUnmanagedAsync(string tableId, CancellationToken token)
        {
            ReadCalls++;
            return Task.FromResult<IReadOnlyList<FieldAdminUnmanagedField>>(
                [new("TMP_FLAG", "bit")]);
        }
    }

    private static DiagnoseModuleTool CreateTool(bool canSetup = true, FakeCatalog? catalog = null) =>
        new(catalog ?? new FakeCatalog(), new FakePermissions(canSetup));

    [Fact]
    public async Task Denies_WithoutCanSetup()
    {
        var result = await CreateTool(canSetup: false).ExecuteAsync("u1",
            JsonSerializer.SerializeToElement(new { goal = "加一页", tables = new[] { new { table = "COP_ORDER_M" } } }),
            CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("CanSetup", result.ContentForModel);
    }

    [Fact]
    public async Task UnregisteredTable_ProducesRegisterAction()
    {
        var result = await CreateTool().ExecuteAsync("u1",
            JsonSerializer.SerializeToElement(new { goal = "加一页回访单", tables = new[] { new { table = "COP_VISIT_M" } } }),
            CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Contains("未在 TABLES 登记", result.ContentForModel);
        Assert.Contains("register_table", result.ContentForModel);
    }

    [Fact]
    public async Task MissingAndGhostFields_AreReported_WithPreviewOnlyChangeset()
    {
        var catalog = new FakeCatalog();
        var result = await CreateTool(catalog: catalog).ExecuteAsync("u1",
            JsonSerializer.SerializeToElement(new
            {
                goal = "加一页",
                tables = new[] { new { table = "COP_ORDER_M", fields = new[] { "ORDER_NO", "TMP_FLAG", "OLD_TEL", "NEW_FLAG" } } },
            }),
            CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Contains("字典未注册，可批量生成", result.ContentForModel);
        Assert.Contains("幽灵字段", result.ContentForModel);
        Assert.Contains("字典与物理列均无", result.ContentForModel);
        Assert.Contains("\"previewOnly\":true", result.ContentForModel);
        Assert.Contains("未执行任何写入", result.ContentForModel);
        Assert.True(catalog.ReadCalls > 0);
    }

    [Fact]
    public async Task IllegalTableName_IsDenied()
    {
        var result = await CreateTool().ExecuteAsync("u1",
            JsonSerializer.SerializeToElement(new { goal = "加一页", tables = new[] { new { table = "A;DROP" } } }),
            CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("非法表名", result.ContentForModel);
    }
}
