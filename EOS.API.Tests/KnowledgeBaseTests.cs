using System.Text.Json;
using EOS.API.Data;
using EOS.API.Features.Assistant.Kb;
using EOS.API.Features.Assistant.ModelAccess;
using EOS.API.Features.Assistant.Tools;
using EOS.API.Models;
using EOS.API.Security;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace EOS.API.Tests;

public sealed class KnowledgeBaseTests
{
    [Fact]
    public void Chunker_SplitsByParagraph_AndKeepsOverlap()
    {
        var content = string.Join("\n\n",
            "第一段：说明总览。" + new string('说', 800),
            "第二段：操作步骤。第一步开单；第二步送审；第三步归档。",
            "第三段：注意事项。");
        var chunks = KbChunker.Split(content);

        Assert.True(chunks.Count >= 2);
        Assert.All(chunks, chunk => Assert.True(chunk.Length <= KbChunker.DefaultMaxChars + KbChunker.DefaultOverlapChars));
        Assert.Contains(chunks, chunk => chunk.Contains("第二段"));
    }

    [Fact]
    public void Chunker_Empty_ReturnsNone()
    {
        Assert.Empty(KbChunker.Split("  \n\n  "));
        Assert.Single(KbChunker.Split("一句话。"));
    }

    [Fact]
    public void Visibility_Normalizes_AndRejectsUnknown()
    {
        Assert.Equal("ALL", KbVisibility.Normalize(null, "ALL"));
        Assert.Equal("CONSULTANT", KbVisibility.Normalize("consultant", "ALL"));
        Assert.Throws<ArgumentException>(() => KbVisibility.Normalize("SECRET", "ALL"));
        Assert.Equal(["ALL"], KbVisibility.AllowedFor(false, false));
        Assert.Equal(["ALL", "CONSULTANT", "OPS"], KbVisibility.AllowedFor(true, true));
    }

    [Fact]
    public void EmbeddingJson_SerializesInvariantFloats()
    {
        var json = EmbeddingJson.ToJson([1f, 2.5f]);
        Assert.Equal("[1,2.5]", json);
    }

    private sealed class FakeEmbedding : IEmbeddingModel
    {
        public string ModelId => "test@1";
        public int Dimension => 4;
        public Task<float[]> EmbedAsync(string text, CancellationToken token) =>
            Task.FromResult(new float[] { 1, 0, 0, 0 });
    }

    private sealed class FakePermissions(bool consultant, bool ops, IReadOnlySet<int>? deniedModules = null) : IPermissionService
    {
        private ModulePermission Permission(int moduleId, bool canSetup) =>
            new(Rights(canSetup, deniedModules?.Contains(moduleId) != true));

        private static ModuleRights Rights(bool canSetup, bool canBrowse = true) => new(
            CanBrowse: canBrowse, CanViewCost: false, CanViewSecrecy: false, CanSetup: canSetup,
            DeniedMasterFields: new HashSet<string>(), DeniedDetailFields: new HashSet<string>(),
            CanAddNew: false, CanEdit: false, CanDelete: false, CanApprove: false, CanDeapprove: false,
            CanEndCase: false, CanUnEndCase: false, CanFileView: false, CanFileUpda: false,
            CanFileEdit: false, CanFileDele: false, DenyNewMasterFields: new HashSet<string>(),
            DenyNewDetailFields: new HashSet<string>(), DenyModiMasterFields: new HashSet<string>(),
            DenyModiDetailFields: new HashSet<string>(), DataFilter: string.Empty, ExecuteTag: "A");

        public Task<ModulePermission> GetAsync(string userId, int moduleId, CancellationToken cancellationToken) =>
            Task.FromResult(Permission(moduleId, moduleId == 2302 ? consultant : ops));

        public Task<ModulePermission> RequireAsync(string userId, int moduleId, PermissionAction action, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FakeKnowledge(IReadOnlyList<KbHit> hits, IReadOnlyList<string>? seenVisibilities = null)
        : IKnowledgeRepository
    {
        public IReadOnlyList<string>? SeenVisibilities { get; private set; } = seenVisibilities;

        public Task EnsureCollectionAsync(string collectionId, string title, string embeddingModel, int dimension, string defaultVisibility, CancellationToken token) =>
            Task.CompletedTask;

        public Task<KbCollectionInfo?> GetCollectionAsync(string collectionId, CancellationToken token) =>
            Task.FromResult<KbCollectionInfo?>(null);

        public Task<(long DocId, bool Reused)> IngestDocumentAsync(string collectionId, string title, string? sourceUri, string content, string visibility, IReadOnlyList<(string Content, float[] Vector)> chunks, string updatedBy, CancellationToken token) =>
            throw new NotSupportedException();

        public Task<bool> DeleteDocumentAsync(long docId, CancellationToken token) =>
            Task.FromResult(true);

        public Task<(KbDocumentInfo? Document, IReadOnlyList<(int SerialNo, string Content)> Chunks)> GetDocumentAsync(long docId, CancellationToken token) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<KbHit>> SearchAsync(string queryVectorJson, int dimension, int topK, IReadOnlyList<string> visibilities, CancellationToken token)
        {
            SeenVisibilities = visibilities;
            return Task.FromResult(hits);
        }
    }

    private sealed class RecheckGatewayStub(IReadOnlyList<Dictionary<string, object?>> rows) : IWorkbenchSearchGateway
    {
        public Task<IReadOnlyList<SystemKnowledgeModule>> ListAssistantModulesAsync(string? keyword, CancellationToken token) =>
            Task.FromResult<IReadOnlyList<SystemKnowledgeModule>>([]);

        public Task<int?> FindGenericModuleIdByTitleAsync(string titleKeyword, CancellationToken token) =>
            Task.FromResult<int?>(null);

        public Task<WorkbenchDefinition?> GetDefinitionAsync(int moduleId, string userId, string? execTag,
            bool canViewCost, bool canViewSecrecy, IReadOnlySet<string> deniedMasterFields,
            IReadOnlySet<string> deniedDetailFields, CancellationToken token) =>
            Task.FromResult<WorkbenchDefinition?>(new(
                moduleId, "客户订单", "COP_ORDER_M", null,
                MasterFields: [], DetailFields: [], DefaultSort: null, HasAdd: true, HasEdit: true,
                DetailNoSave: false, MasterPkOrder: ["ORDER_TYPE", "ORDER_NO"], DetailNoFields: string.Empty,
                HasWorkflow: false));

        public Task<WorkbenchData> GetRowsAsync(WorkbenchDefinition definition, bool detail,
            IReadOnlyDictionary<string, string> keys, int page, int pageSize, CancellationToken token,
            WorkbenchQuery? query = null, string? keyword = null, string? sortField = null,
            string? sortDirection = null, int? groupIndex = null, string? groupValue = null, string? dataFilter = null) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<Dictionary<string, object?>>> GetExportRowsByKeysAsync(
            WorkbenchDefinition definition, IReadOnlyList<IReadOnlyList<string>> keys, CancellationToken token,
            int? groupIndex = null, string? groupValue = null, IReadOnlyList<WorkbenchField>? exportFields = null,
            string? dataFilter = null) => Task.FromResult(rows);

        public Task<FormDefinition?> GetFormDefinitionAsync(WorkbenchDefinition definition, string userId, string mode,
            bool canViewCost, bool canViewSecrecy, IReadOnlySet<string> deniedMasterFields,
            IReadOnlySet<string> deniedDetailFields, IReadOnlySet<string> deniedNewMasterFields,
            IReadOnlySet<string> deniedNewDetailFields, IReadOnlySet<string> deniedModiMasterFields,
            IReadOnlySet<string> deniedModiDetailFields, CancellationToken token, bool canAddNew = false,
            bool canEdit = false, bool canDelete = false, bool canApprove = false, bool canDeapprove = false,
            bool canEndCase = false, bool canUnEndCase = false, bool canFileView = false, bool canFileUpda = false,
            bool canFileEdit = false, bool canFileDele = false, bool canSetup = false) =>
            throw new NotSupportedException();
    }

    [Fact]
    public async Task KbSearch_PassesVisibilityTiers_AndMarksSources()
    {
        var hits = new[] { new KbHit(9, "业务规则", "docs/07", 2, "送审后不得重复送审。", 0.1) };
        var knowledge = new FakeKnowledge(hits);
        var tool = new KbSearchTool(knowledge, new FakeEmbedding(),
            new FakePermissions(consultant: true, ops: false),
            new RecheckGatewayStub([new Dictionary<string, object?>()]));

        var result = await tool.ExecuteAsync("u1",
            JsonSerializer.SerializeToElement(new { query = "送审" }), CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Contains("[来源：业务规则#2]", result.ContentForModel);
        Assert.Contains("kb://doc/9#c2", result.ContentForModel);
        Assert.Equal(["ALL", "CONSULTANT"], knowledge.SeenVisibilities);
    }

    [Fact]
    public async Task KbSearch_DropsFragment_WithUnbrowsableReference()
    {
        var hits = new[] { new KbHit(9, "业务规则", "docs/07", 2,
            "参见 module=1405 的单据 _keys=[\"DD\",\"26080001\"]。", 0.1) };
        var tool = new KbSearchTool(new FakeKnowledge(hits), new FakeEmbedding(),
            new FakePermissions(false, false, deniedModules: new HashSet<int> { 1405 }),
            new RecheckGatewayStub([new Dictionary<string, object?>()]));

        var result = await tool.ExecuteAsync("u1",
            JsonSerializer.SerializeToElement(new { query = "单据" }), CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Contains("没有相关内容", result.ContentForModel);
    }

    [Fact]
    public async Task KbSearch_DropsFragment_WithOutOfScopeReference()
    {
        var hits = new[] { new KbHit(9, "业务规则", "docs/07", 2,
            "参见 module=1405 的单据 _keys=[\"DD\",\"26080001\"]。", 0.1) };
        var tool = new KbSearchTool(new FakeKnowledge(hits), new FakeEmbedding(),
            new FakePermissions(false, false),
            new RecheckGatewayStub([]));

        var result = await tool.ExecuteAsync("u1",
            JsonSerializer.SerializeToElement(new { query = "单据" }), CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Contains("没有相关内容", result.ContentForModel);
    }

    [Fact]
    public async Task KbSearch_KeepsFragment_WithVerifiedReference()
    {
        var hits = new[] { new KbHit(9, "业务规则", "docs/07", 2,
            "参见 module=1405 的单据 _keys=[\"DD\",\"26080001\"]。", 0.1) };
        var tool = new KbSearchTool(new FakeKnowledge(hits), new FakeEmbedding(),
            new FakePermissions(false, false),
            new RecheckGatewayStub([new Dictionary<string, object?> { ["ORDER_NO"] = "26080001" }]));

        var result = await tool.ExecuteAsync("u1",
            JsonSerializer.SerializeToElement(new { query = "单据" }), CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Contains("[来源：业务规则#2]", result.ContentForModel);
    }

    [Fact]
    public async Task KbSearch_EmptyKnowledge_AdmitsNoContent()
    {
        var tool = new KbSearchTool(new FakeKnowledge([]), new FakeEmbedding(), new FakePermissions(false, false),
            new RecheckGatewayStub([]));

        var result = await tool.ExecuteAsync("u1",
            JsonSerializer.SerializeToElement(new { query = "送审" }), CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Contains("没有相关内容", result.ContentForModel);
    }

    [Fact]
    public async Task FilterHits_DropsFragment_WithDeniedReferences()
    {
        var hits = new[]
        {
            new KbHit(1, "制度", null, 1, "无业务引用的通用文本。", 0.1),
            new KbHit(2, "业务规则", null, 1, "参见 module=1405 的单据 _keys=[\"DD\",\"26080001\"]。", 0.2),
        };

        var kept = await KbReferenceVerifier.FilterHitsAsync("u1", hits,
            (_, references, _) => Task.FromResult<string?>(references.Count > 0 ? "denied" : null),
            CancellationToken.None);

        var only = Assert.Single(kept);
        Assert.Equal(1, only.DocId);
    }

    [Fact]
    public async Task FilterHits_KeepsFragment_WhenReferencesPass()
    {
        var hits = new[]
        {
            new KbHit(2, "业务规则", null, 1, "参见 module=1405 的单据 _keys=[\"DD\",\"26080001\"]。", 0.2),
            new KbHit(3, "制度", null, 2, "无业务引用的通用文本。", 0.3),
        };

        var kept = await KbReferenceVerifier.FilterHitsAsync("u1", hits,
            (_, _, _) => Task.FromResult<string?>(null),
            CancellationToken.None);

        Assert.Equal(2, kept.Count);
    }

    private static string? TestConnection() =>
        Environment.GetEnvironmentVariable("EOS_ERP_TEST_CONNECTION");

    private static async Task<bool> KbTablesReadyAsync(string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            "SELECT 1 WHERE OBJECT_ID(N'dbo.KB_DOCUMENT', N'U') IS NOT NULL AND OBJECT_ID(N'dbo.KB_CHUNK', N'U') IS NOT NULL;",
            connection);
        return await command.ExecuteScalarAsync() is not null;
    }

    private static async Task<string> RequireReadyConnectionAsync()
    {
        var connectionString = TestConnection()
            ?? throw new InvalidOperationException(
                "真库集成测试需要 EOS_ERP_TEST_CONNECTION；未配置即失败（无连接跳过≠已验证）。");
        if (!await KbTablesReadyAsync(connectionString))
            throw new InvalidOperationException(
                "dbo.KB_DOCUMENT / dbo.KB_CHUNK 不存在（迁移 044 未执行）；未就绪即失败（跳过≠已验证）。");
        return connectionString;
    }

    [Fact]
    public async Task Repository_IngestIsIdempotent_VisibilityFilters_AndDeleteSyncs()
    {
        var connectionString = await RequireReadyConnectionAsync();

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:ErpDatabase"] = connectionString,
            })
            .Build();
        var repository = new KnowledgeRepository(new DbConnectionFactory(config));
        var token = CancellationToken.None;
        const string collection = "kb_test_m5";
        await repository.EnsureCollectionAsync(collection, "M5 测试集合", "test@1", 4, "ALL", token);

        var vector = new float[] { 1, 0, 0, 0 };
        var content = "M5 集成测试文档。" + Guid.NewGuid();
        var first = await repository.IngestDocumentAsync(collection, "M5 测试", "test/m5",
            content, "CONSULTANT", [("M5 集成测试文档。", vector)], "test", token);
        var second = await repository.IngestDocumentAsync(collection, "M5 测试", "test/m5",
            content, "CONSULTANT", [("M5 集成测试文档。", vector)], "test", token);

        Assert.False(first.Reused);
        Assert.True(second.Reused);
        Assert.Equal(first.DocId, second.DocId);

        var hidden = await repository.SearchAsync("[1,0,0,0]", 4, 5, ["ALL"], token);
        Assert.DoesNotContain(hidden, hit => hit.DocId == first.DocId);
        var visible = await repository.SearchAsync("[1,0,0,0]", 4, 5, ["ALL", "CONSULTANT"], token);
        Assert.Contains(visible, hit => hit.DocId == first.DocId);

        Assert.True(await repository.DeleteDocumentAsync(first.DocId, token));
        var afterDelete = await repository.GetDocumentAsync(first.DocId, token);
        Assert.Equal("deleted", afterDelete.Document!.Status);
        Assert.Empty(afterDelete.Chunks);
        var searchAfterDelete = await repository.SearchAsync("[1,0,0,0]", 4, 5, ["ALL", "CONSULTANT"], token);
        Assert.DoesNotContain(searchAfterDelete, hit => hit.DocId == first.DocId);
    }
}
