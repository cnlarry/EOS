using System.Text.Json;
using EOS.API.Data;
using EOS.API.Features.Assistant.Admin;
using EOS.API.Features.Assistant.Memory;
using EOS.API.Features.Assistant.ModelAccess;
using EOS.API.Features.Assistant.Tools;
using EOS.API.Models;
using EOS.API.Security;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// M7 确定性回归：authz 越权集（泄露=0 硬门槛）+ inference 推断集格式与必要来源门。
/// 正确率/拒答率需 LLM-as-judge，现阶段人工抽样，不进自动断言（见 AssistantEval/README）。
/// </summary>
public sealed class AssistantEvalRunnerTests
{
    private static string EvalPath(string name)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "AssistantEval", name);
            if (File.Exists(candidate)) return candidate;
            var nested = Path.Combine(directory.FullName, "EOS.API.Tests", "AssistantEval", name);
            if (File.Exists(nested)) return nested;
            directory = directory.Parent;
        }

        throw new FileNotFoundException($"评估集缺失：{name}");
    }

    private sealed class DenyPermissions : IPermissionService
    {
        private static ModulePermission Denied() => new(new ModuleRights(
            CanBrowse: false, CanViewCost: false, CanViewSecrecy: false, CanSetup: false,
            DeniedMasterFields: new HashSet<string>(), DeniedDetailFields: new HashSet<string>(),
            CanAddNew: false, CanEdit: false, CanDelete: false, CanApprove: false, CanDeapprove: false,
            CanEndCase: false, CanUnEndCase: false, CanFileView: false, CanFileUpda: false,
            CanFileEdit: false, CanFileDele: false, DenyNewMasterFields: new HashSet<string>(),
            DenyNewDetailFields: new HashSet<string>(), DenyModiMasterFields: new HashSet<string>(),
            DenyModiDetailFields: new HashSet<string>(), DataFilter: string.Empty, ExecuteTag: "A"));

        public Task<ModulePermission> GetAsync(string userId, int moduleId, CancellationToken cancellationToken) =>
            Task.FromResult(Denied());

        public Task<ModulePermission> RequireAsync(string userId, int moduleId, PermissionAction action, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private static WorkbenchDefinition Definition() => new(
        2306, "用户权限", "SYSDL", null,
        MasterFields: [], DetailFields: [], DefaultSort: null, HasAdd: false, HasEdit: false,
        DetailNoSave: false, MasterPkOrder: ["USER_ID"], DetailNoFields: string.Empty,
        HasWorkflow: false);

    private sealed class DataGateway : IWorkbenchSearchGateway
    {
        public Task<IReadOnlyList<SystemKnowledgeModule>> ListAssistantModulesAsync(string? keyword, CancellationToken token) =>
            Task.FromResult<IReadOnlyList<SystemKnowledgeModule>>([new(2306, "用户权限设定")]);

        public Task<int?> FindGenericModuleIdByTitleAsync(string titleKeyword, CancellationToken token) =>
            Task.FromResult<int?>(2306);

        public Task<WorkbenchDefinition?> GetDefinitionAsync(int moduleId, string userId, string? execTag,
            bool canViewCost, bool canViewSecrecy, IReadOnlySet<string> deniedMasterFields,
            IReadOnlySet<string> deniedDetailFields, CancellationToken token) =>
            Task.FromResult<WorkbenchDefinition?>(Definition());

        public Task<WorkbenchData> GetRowsAsync(WorkbenchDefinition definition, bool detail,
            IReadOnlyDictionary<string, string> keys, int page, int pageSize, CancellationToken token,
            WorkbenchQuery? query = null, string? keyword = null, string? sortField = null,
            string? sortDirection = null, int? groupIndex = null, string? groupValue = null, string? dataFilter = null) =>
            Task.FromResult(new WorkbenchData([new Dictionary<string, object?> { ["USER_ID"] = "admin" }], 1, 1, 5));

        public Task<IReadOnlyList<Dictionary<string, object?>>> GetExportRowsByKeysAsync(
            WorkbenchDefinition definition, IReadOnlyList<IReadOnlyList<string>> keys, CancellationToken token,
            int? groupIndex = null, string? groupValue = null, IReadOnlyList<WorkbenchField>? exportFields = null,
            string? dataFilter = null) =>
            Task.FromResult<IReadOnlyList<Dictionary<string, object?>>>(
                [new Dictionary<string, object?> { ["USER_ID"] = "admin" }]);

        public Task<FormDefinition?> GetFormDefinitionAsync(WorkbenchDefinition definition, string userId, string mode,
            bool canViewCost, bool canViewSecrecy, IReadOnlySet<string> deniedMasterFields,
            IReadOnlySet<string> deniedDetailFields, IReadOnlySet<string> deniedNewMasterFields,
            IReadOnlySet<string> deniedNewDetailFields, IReadOnlySet<string> deniedModiMasterFields,
            IReadOnlySet<string> deniedModiDetailFields, CancellationToken token, bool canAddNew = false,
            bool canEdit = false, bool canDelete = false, bool canApprove = false, bool canDeapprove = false,
            bool canEndCase = false, bool canUnEndCase = false, bool canFileView = false, bool canFileUpda = false,
            bool canFileEdit = false, bool canFileDele = false, bool canSetup = false) =>
            Task.FromResult<FormDefinition?>(null);
    }

    private sealed class SchemaGatewayStub : IAssistantSchemaGateway
    {
        public Task<IReadOnlyList<FieldAdminTable>> ListTablesAsync(string? kind, CancellationToken token) =>
            Task.FromResult<IReadOnlyList<FieldAdminTable>>([]);

        public Task<(FieldAdminTableDetail? Table, IReadOnlyList<FieldAdminFieldSummary> Fields, IReadOnlyList<FieldAdminUnmanagedField> Unmanaged)> DescribeTableAsync(
            string tableId, CancellationToken token) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<AssistantSchemaView>> ListViewsAsync(string? keyword, CancellationToken token) =>
            Task.FromResult<IReadOnlyList<AssistantSchemaView>>([]);

        public Task<IReadOnlyList<AssistantSchemaProcedure>> ListProceduresAsync(string? keyword, CancellationToken token) =>
            Task.FromResult<IReadOnlyList<AssistantSchemaProcedure>>([]);
    }

    private sealed class FlowGatewayStub : IModuleFlowGateway
    {
        public Task<FlowDefinitionInfo?> GetFlowDefinitionAsync(int moduleId, CancellationToken token) =>
            Task.FromResult<FlowDefinitionInfo?>(null);

        public Task<FlowInstanceInfo?> GetInstanceAsync(int moduleId, string keyCondition, CancellationToken token) =>
            Task.FromResult<FlowInstanceInfo?>(null);

        public Task<ApprovalConfirmInfo?> GetApprovalConfirmAsync(int moduleId, string keyCondition, CancellationToken token) =>
            Task.FromResult<ApprovalConfirmInfo?>(null);

        public Task<RecordStateInfo> GetRecordStateAsync(string masterTable, IReadOnlyList<string> pkColumns,
            IReadOnlyList<string> keyValues, CancellationToken token) =>
            Task.FromResult(new RecordStateInfo(true, false, false));
    }

    private sealed class FakeEmbedding : IEmbeddingModel
    {
        public string ModelId => "test@1";
        public int Dimension => 4;
        public Task<float[]> EmbedAsync(string text, CancellationToken token) =>
            Task.FromResult(new float[] { 1, 0, 0, 0 });
    }

    private sealed class FilteringKnowledge : IKnowledgeRepository
    {
        public IReadOnlyList<string>? SeenVisibilities { get; private set; }

        public Task EnsureCollectionAsync(string collectionId, string title, string embeddingModel, int dimension,
            string defaultVisibility, CancellationToken token) => Task.CompletedTask;

        public Task<KbCollectionInfo?> GetCollectionAsync(string collectionId, CancellationToken token) =>
            Task.FromResult<KbCollectionInfo?>(null);

        public Task<(long DocId, bool Reused)> IngestDocumentAsync(string collectionId, string title, string? sourceUri,
            string content, string visibility, IReadOnlyList<(string Content, float[] Vector)> chunks,
            string updatedBy, CancellationToken token) => throw new NotSupportedException();

        public Task<bool> DeleteDocumentAsync(long docId, CancellationToken token) => Task.FromResult(true);

        public Task<(KbDocumentInfo? Document, IReadOnlyList<(int SerialNo, string Content)> Chunks)> GetDocumentAsync(
            long docId, CancellationToken token) => throw new NotSupportedException();

        public Task<IReadOnlyList<KbHit>> SearchAsync(string queryVectorJson, int dimension, int topK,
            IReadOnlyList<string> visibilities, CancellationToken token)
        {
            SeenVisibilities = visibilities;
            // 模拟服务端 WHERE 可见性过滤：只返回允许档的命中。
            var hits = new List<KbHit>();
            if (visibilities.Contains("CONSULTANT")) hits.Add(new(1, "顾问手册", null, 1, "内容", 0.1));
            return Task.FromResult<IReadOnlyList<KbHit>>(hits);
        }
    }

    private sealed class RecheckGatewayStub : IWorkbenchSearchGateway
    {
        public Task<IReadOnlyList<SystemKnowledgeModule>> ListAssistantModulesAsync(string? keyword, CancellationToken token) =>
            Task.FromResult<IReadOnlyList<SystemKnowledgeModule>>([]);

        public Task<int?> FindGenericModuleIdByTitleAsync(string titleKeyword, CancellationToken token) =>
            Task.FromResult<int?>(null);

        public Task<WorkbenchDefinition?> GetDefinitionAsync(int moduleId, string userId, string? execTag,
            bool canViewCost, bool canViewSecrecy, IReadOnlySet<string> deniedMasterFields,
            IReadOnlySet<string> deniedDetailFields, CancellationToken token) =>
            Task.FromResult<WorkbenchDefinition?>(null);

        public Task<WorkbenchData> GetRowsAsync(WorkbenchDefinition definition, bool detail,
            IReadOnlyDictionary<string, string> keys, int page, int pageSize, CancellationToken token,
            WorkbenchQuery? query = null, string? keyword = null, string? sortField = null,
            string? sortDirection = null, int? groupIndex = null, string? groupValue = null, string? dataFilter = null) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<Dictionary<string, object?>>> GetExportRowsByKeysAsync(
            WorkbenchDefinition definition, IReadOnlyList<IReadOnlyList<string>> keys, CancellationToken token,
            int? groupIndex = null, string? groupValue = null, IReadOnlyList<WorkbenchField>? exportFields = null,
            string? dataFilter = null) =>
            Task.FromResult<IReadOnlyList<Dictionary<string, object?>>>([]);

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

    private sealed class PlanCatalogStub : IModulePlanCatalog
    {
        public Task<IReadOnlyList<FieldAdminTable>> ListTablesAsync(CancellationToken token) =>
            Task.FromResult<IReadOnlyList<FieldAdminTable>>([]);

        public Task<IReadOnlyList<FieldAdminFieldSummary>> ListFieldsAsync(string tableId, CancellationToken token) =>
            Task.FromResult<IReadOnlyList<FieldAdminFieldSummary>>([]);

        public Task<IReadOnlyList<FieldAdminUnmanagedField>> ListUnmanagedAsync(string tableId, CancellationToken token) =>
            Task.FromResult<IReadOnlyList<FieldAdminUnmanagedField>>([]);
    }

    private sealed class WriterStub : IChangeSetWriter
    {
        public Task RegisterTableAsync(string tableId, string description, string? kind, string updatedBy, CancellationToken token) =>
            Task.CompletedTask;

        public Task<(int Created, int Skipped, IReadOnlyList<string> Reasons)> AddFieldsAsync(string tableId,
            IReadOnlyList<string> fieldIds, string updatedBy, CancellationToken token) =>
            Task.FromResult<(int, int, IReadOnlyList<string>)>((0, 0, []));
    }

    private sealed class DigestStoreStub : IAssistantMemoryStore
    {
        public Task<string?> GetPreferencesAsync(string userId, CancellationToken token) =>
            Task.FromResult<string?>(null);

        public Task SetPreferencesAsync(string userId, string? preferencesJson, CancellationToken token) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<AssistantMemoryItem>> ListMemoriesAsync(string userId, CancellationToken token) =>
            Task.FromResult<IReadOnlyList<AssistantMemoryItem>>([]);

        public Task<AssistantMemoryItem> AddMemoryAsync(string userId, string memoryType, string memoryKey,
            string memoryValue, long? sourceMessageId, CancellationToken token) => throw new NotSupportedException();

        public Task<AssistantMemoryItem> AddPendingAsync(string userId, string memoryType, string memoryKey,
            string memoryValue, long? sourceMessageId, int confidence, CancellationToken token) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<AssistantMemoryItem>> ListPendingAsync(string userId, CancellationToken token) =>
            Task.FromResult<IReadOnlyList<AssistantMemoryItem>>([]);

        public Task<string> ResolvePendingAsync(string userId, long memoryId, bool confirm, CancellationToken token) =>
            Task.FromResult("confirmed");

        public Task<bool> DeleteMemoryAsync(string userId, long memoryId, CancellationToken token) =>
            Task.FromResult(true);

        public Task ForgetMeAsync(string userId, CancellationToken token) => Task.CompletedTask;

        public Task<string> BuildMemoryPrefixAsync(string userId, string? keyword, CancellationToken token) =>
            Task.FromResult(string.Empty);
    }

    [Fact]
    public async Task Authz_Corpus_HasZeroLeak()
    {
        var deny = new DenyPermissions();
        var searchGateway = new DataGateway();
        var knowledge = new FilteringKnowledge();
        var tools = new Dictionary<string, IAssistantTool>
        {
            ["search_records"] = new SearchRecordsTool(searchGateway, deny),
            ["get_record_detail"] = new GetRecordDetailTool(searchGateway, deny),
            ["get_form_schema"] = new GetFormSchemaTool(searchGateway, deny),
            ["draft_record"] = new DraftRecordTool(searchGateway, deny),
            ["list_modules"] = new ListModulesTool(searchGateway, deny),
            ["describe_module"] = new DescribeModuleTool(searchGateway, deny),
            ["list_tables"] = new ListTablesTool(new SchemaGatewayStub(), deny),
            ["describe_table"] = new DescribeTableTool(new SchemaGatewayStub(), deny),
            ["list_views"] = new ListViewsTool(new SchemaGatewayStub(), deny),
            ["list_procedures"] = new ListProceduresTool(new SchemaGatewayStub(), deny),
            ["get_module_flow"] = new GetModuleFlowTool(searchGateway, new FlowGatewayStub(), deny),
            ["kb_search"] = new KbSearchTool(knowledge, new FakeEmbedding(), deny, new RecheckGatewayStub()),
            ["diagnose_module"] = new DiagnoseModuleTool(new PlanCatalogStub(), deny),
            ["apply_changeset"] = new ApplyChangeSetTool(new ChangeSetService(new PlanCatalogStub(), new WriterStub()), deny),
            ["get_my_digest"] = new GetMyDigestTool(new DigestStoreStub()),
        };

        var lines = await File.ReadAllLinesAsync(EvalPath(Path.Combine("authz", "seed-01.jsonl")));
        Assert.Equal(20, lines.Length);
        foreach (var line in lines)
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            var toolName = root.GetProperty("tool").GetString()!;
            var args = root.GetProperty("args");
            var expect = root.GetProperty("expect").GetString()!;
            var result = await tools[toolName].ExecuteAsync("attacker", args, CancellationToken.None);
            switch (expect)
            {
                case "deny":
                    Assert.False(result.Ok, $"工具 {toolName} 越权未拒绝：{line}");
                    break;
                case "empty":
                    Assert.True(result.Ok);
                    Assert.Contains("共 0 个", result.ContentForModel);
                    break;
                case "all_only":
                    Assert.True(result.Ok);
                    Assert.Equal(["ALL"], knowledge.SeenVisibilities);
                    Assert.DoesNotContain("顾问手册", result.ContentForModel);
                    break;
                case "ok":
                    Assert.True(result.Ok);
                    break;
                default:
                    Assert.Fail($"未知期望：{expect}");
                    break;
            }
        }
    }

    [Fact]
    public async Task Inference_Corpus_DeclaresCompleteEvidenceChains()
    {
        var lines = await File.ReadAllLinesAsync(EvalPath(Path.Combine("inference", "seed-01.jsonl")));
        Assert.Equal(20, lines.Length);
        foreach (var line in lines)
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("question").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("expected").GetString()));
            var required = root.GetProperty("required_sources").EnumerateArray()
                .Select(element => element.GetString()!).ToArray();
            var expected = root.GetProperty("expected").GetString()!;
            // 拒答题（指标尚无定义）只要求声明 enum_metrics 查证来源，豁免多源推断的 ≥3 规则。
            var isRefusal = expected.Contains("尚无定义", StringComparison.Ordinal);
            Assert.True(isRefusal || required.Length >= 3, $"必要来源不足 3 个：{line}");
            // 涉及金额/数量/比率的推断必须引用口径（决策 14 硬规则 2）；纯流程/状态/拒答题除外。
            var quantitative = new[] { "口径", "计算", "金额", "数量", "Top", "汇总", "对照", "税", "毛利",
                "周转", "应收", "应付", "付款", "收款", "完工", "折扣", "信用", "齐套", "匹配", "配比" }
                .Any(keyword => expected.Contains(keyword, StringComparison.Ordinal));
            if (quantitative)
            {
                Assert.Contains(required, source =>
                    source.StartsWith("resolve_metric", StringComparison.Ordinal)
                    || source.StartsWith("enum_metrics", StringComparison.Ordinal));
            }
        }
    }
}
