using System.Text.Json;
using EOS.API.Data;
using EOS.API.Features.Assistant.Tools;
using EOS.API.Models;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// resolve_reference 的工具层契约：候选输出取第一列为可回填值；服务端拒绝要原样回喂原因，
/// 不能把"没有候选"与"无权/来源缺失"混成同一句话。
/// </summary>
public sealed class AssistantReferenceToolTests
{
    private sealed class FakeLookup(ReferenceLookup? result) : IReferenceLookupGateway
    {
        public string? LastField { get; private set; }

        public Task<ReferenceLookup?> LookupAsync(
            string userId, int moduleId, string fieldKey, int? serialNo, string? keyword,
            int maxRows, CancellationToken token)
        {
            LastField = fieldKey;
            return Task.FromResult(result);
        }
    }

    private sealed class FakeWorkbench : IWorkbenchSearchGateway
    {
        public Task<IReadOnlyList<SystemKnowledgeModule>> ListAssistantModulesAsync(string? keyword, CancellationToken token)
            => Task.FromResult<IReadOnlyList<SystemKnowledgeModule>>([]);

        public Task<int?> FindGenericModuleIdByTitleAsync(string titleKeyword, CancellationToken token)
            => Task.FromResult<int?>(1606);

        public Task<WorkbenchDefinition?> GetDefinitionAsync(
            int moduleId, string userId, string? execTag, bool canViewCost, bool canViewSecrecy,
            IReadOnlySet<string> deniedMasterFields, IReadOnlySet<string> deniedDetailFields,
            CancellationToken token) => Task.FromResult<WorkbenchDefinition?>(null);

        public Task<WorkbenchData> GetRowsAsync(
            WorkbenchDefinition definition, bool detail, IReadOnlyDictionary<string, string> keys,
            int page, int pageSize, CancellationToken token, WorkbenchQuery? query = null,
            string? keyword = null, string? sortField = null, string? sortDirection = null,
            int? groupId = null, string? groupValue = null, string? dataFilter = null)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<Dictionary<string, object?>>> GetExportRowsByKeysAsync(
            WorkbenchDefinition definition, IReadOnlyList<IReadOnlyList<string>> keys, CancellationToken token,
            int? groupId = null, string? groupValue = null,
            IReadOnlyList<WorkbenchField>? exportFields = null, string? dataFilter = null)
            => throw new NotSupportedException();

        public Task<FormDefinition?> GetFormDefinitionAsync(
            WorkbenchDefinition definition, string userId, string mode, bool canViewCost, bool canViewSecrecy,
            IReadOnlySet<string> deniedMasterFields, IReadOnlySet<string> deniedDetailFields,
            IReadOnlySet<string> deniedNewMasterFields, IReadOnlySet<string> deniedNewDetailFields,
            IReadOnlySet<string> deniedModiMasterFields, IReadOnlySet<string> deniedModiDetailFields,
            CancellationToken token, bool canAddNew = false, bool canEdit = false, bool canDelete = false,
            bool canApprove = false, bool canDeapprove = false, bool canEndCase = false,
            bool canUnEndCase = false, bool canFileView = false, bool canFileUpda = false,
            bool canFileEdit = false, bool canFileDele = false, bool canSetup = false)
            => Task.FromResult<FormDefinition?>(null);
    }

    private static ReferenceLookup Lookup() => new(
        "客户订单", "客户",
        [new FormChooserColumn("CLIENT_ID", "客户编号", "nvarchar", null),
         new FormChooserColumn("CLIENT_NAME", "客户名称", "nvarchar", null)],
        [
            new Dictionary<string, object?> { ["CLIENT_ID"] = "C001", ["CLIENT_NAME"] = "华东客户" },
            new Dictionary<string, object?> { ["CLIENT_ID"] = "C002", ["CLIENT_NAME"] = "华南客户" },
        ],
        Total: 2,
        ValueColumn: "CLIENT_ID",
        ReturnItems: [new ChooserReturnItem("CLIENT_ID", "CLIENT_ID"), new ChooserReturnItem("CLIENT_NAME", "CLIENT_NAME")]);

    [Fact]
    public async Task Resolve_Reference_Outputs_First_Column_As_Value()
    {
        var tool = new ResolveReferenceTool(new FakeLookup(Lookup()), new FakeWorkbench());
        var result = await tool.ExecuteAsync("u1", JsonSerializer.SerializeToElement(new
        {
            module_title = "客户订单",
            field_key = "CLIENT_ID",
            keyword = "华东",
        }), CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Contains("field=客户", result.ContentForModel);
        Assert.Contains("值=C001", result.ContentForModel);
        Assert.Contains("客户名称=华东客户", result.ContentForModel);
        Assert.Contains("回填映射：CLIENT_ID←CLIENT_ID", result.ContentForModel);
        Assert.Contains("total=2", result.ContentForModel);
    }

    [Fact]
    public async Task Resolve_Reference_Returns_Server_Denial_Instead_Of_Empty_List()
    {
        var denied = new ReferenceLookup(
            "客户订单", "客户", [], [], 0,
            ValueColumn: null,
            ReturnItems: null,
            Code: "CHOOSER_SOURCE_NOT_FOUND", Message: "字段 客户(CLIENT_ID) 没有启用的选择器来源。");
        var tool = new ResolveReferenceTool(new FakeLookup(denied), new FakeWorkbench());

        var result = await tool.ExecuteAsync("u1", JsonSerializer.SerializeToElement(new
        {
            module_id = 1606,
            field_key = "CLIENT_ID",
        }), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("CHOOSER_SOURCE_NOT_FOUND", result.ContentForModel);
        Assert.Contains("没有启用的选择器来源", result.ContentForModel);
    }

    [Fact]
    public async Task Resolve_Reference_Denies_When_Module_Has_No_Form_Access()
    {
        var tool = new ResolveReferenceTool(new FakeLookup(null), new FakeWorkbench());
        var result = await tool.ExecuteAsync("u1", JsonSerializer.SerializeToElement(new
        {
            module_id = 1606,
            field_key = "CLIENT_ID",
        }), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("浏览权限", result.ContentForModel);
    }

    [Fact]
    public async Task Resolve_Reference_Requires_Field_Key()
    {
        var tool = new ResolveReferenceTool(new FakeLookup(Lookup()), new FakeWorkbench());
        var result = await tool.ExecuteAsync("u1", JsonSerializer.SerializeToElement(new
        {
            module_id = 1606,
        }), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("field_key", result.ContentForModel);
    }
}
