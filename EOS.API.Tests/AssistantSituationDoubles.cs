using System.Security.Claims;
using EOS.API.Features.Assistant.Situation;
using EOS.API.Features.Assistant.Tools;
using EOS.API.Models;
using EOS.API.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace EOS.API.Tests;

/// <summary>处境相关测试的共用替身：不发 SQL、不连库，全部在内存里给事实。</summary>
internal static class AssistantSituationDoubles
{
    internal sealed class FakeFacts : ISituationFactsReader
    {
        public SituationRelationship Relationship { get; set; } = new(null, null, []);
        public SituationPending Pending { get; set; } = new(0, 0);
        public List<SituationFailureFact> Failures { get; } = [];
        public List<int> ActivityModules { get; } = [];
        public SituationTargetProbe Probe { get; set; } = SituationTargetProbe.Empty;
        public int PendingCalls { get; private set; }

        public Task<SituationRelationship> LoadRelationshipAsync(string userId, string departmentId, CancellationToken token)
            => Task.FromResult(Relationship);

        public Task<SituationPending> LoadPendingAsync(string userId, CancellationToken token)
        {
            PendingCalls++;
            return Task.FromResult(Pending);
        }

        public Task<IReadOnlyList<SituationFailureFact>> LoadRecentFailuresAsync(
            string userId, int days, int limit, CancellationToken token)
            => Task.FromResult<IReadOnlyList<SituationFailureFact>>([.. Failures.Take(limit)]);

        public Task<IReadOnlyList<int>> LoadActivityModulesAsync(
            string userId, int days, int limit, CancellationToken token)
            => Task.FromResult<IReadOnlyList<int>>([.. ActivityModules.Take(limit)]);

        public Task<SituationTargetProbe> ProbeConfigTargetAsync(
            string tableId, string fieldId, long? actionId, CancellationToken token)
            => Task.FromResult(Probe);
    }

    internal sealed class FakePermissions : IPermissionService
    {
        /// <summary>允许浏览的模块（其余模块按"无权浏览"拒绝，与 fail-closed 同向）。</summary>
        public HashSet<int> Browsable { get; } = [];
        public HashSet<int> SetupModules { get; } = [];

        public Task<ModulePermission> GetAsync(string userId, int moduleId, CancellationToken cancellationToken)
            => Task.FromResult(new ModulePermission(RightsFor(Browsable.Contains(moduleId), SetupModules.Contains(moduleId))));

        public Task<ModulePermission> RequireAsync(
            string userId, int moduleId, PermissionAction action, CancellationToken cancellationToken)
        {
            var permission = new ModulePermission(RightsFor(Browsable.Contains(moduleId), SetupModules.Contains(moduleId)));
            if (!permission.Can(action)) throw new PermissionDeniedException(userId, moduleId, action);
            return Task.FromResult(permission);
        }

        public static ModuleRights RightsFor(bool canBrowse, bool canSetup = false) => new(
            CanBrowse: canBrowse,
            CanViewCost: true,
            CanViewSecrecy: true,
            CanSetup: canSetup,
            DeniedMasterFields: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            DeniedDetailFields: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            CanAddNew: canBrowse,
            CanEdit: canBrowse,
            CanDelete: canBrowse,
            CanApprove: false,
            CanDeapprove: false,
            CanEndCase: false,
            CanUnEndCase: false,
            CanFileView: false,
            CanFileUpda: false,
            CanFileEdit: false,
            CanFileDele: false,
            DenyNewMasterFields: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            DenyNewDetailFields: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            DenyModiMasterFields: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            DenyModiDetailFields: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            DataFilter: string.Empty,
            ExecuteTag: "A");
    }

    internal sealed class FakeGateway : IWorkbenchSearchGateway
    {
        public Dictionary<int, WorkbenchDefinition> Definitions { get; } = [];
        public List<SystemKnowledgeModule> Modules { get; } = [];
        /// <summary>按模块编号登记的列表查询结果（滞留扫描用）。</summary>
        public Dictionary<int, WorkbenchData> Rows { get; } = [];
        public List<(int ModuleId, WorkbenchQuery? Query, string? DataFilter, int PageSize)> RowQueries { get; } = [];

        public Task<IReadOnlyList<SystemKnowledgeModule>> ListAssistantModulesAsync(string? keyword, CancellationToken token)
            => Task.FromResult<IReadOnlyList<SystemKnowledgeModule>>(Modules);

        public Task<int?> FindGenericModuleIdByTitleAsync(string titleKeyword, CancellationToken token)
            => Task.FromResult<int?>(null);

        public Task<WorkbenchDefinition?> GetDefinitionAsync(
            int moduleId, string userId, string? execTag, bool canViewCost, bool canViewSecrecy,
            IReadOnlySet<string> deniedMasterFields, IReadOnlySet<string> deniedDetailFields,
            CancellationToken token)
            => Task.FromResult(Definitions.TryGetValue(moduleId, out var definition) ? definition : null);

        public Task<WorkbenchData> GetRowsAsync(
            WorkbenchDefinition definition, bool detail, IReadOnlyDictionary<string, string> keys,
            int page, int pageSize, CancellationToken token, WorkbenchQuery? query = null,
            string? keyword = null, string? sortField = null, string? sortDirection = null,
            int? groupIndex = null, string? groupValue = null, string? dataFilter = null)
        {
            RowQueries.Add((definition.ModuleId, query, dataFilter, pageSize));
            return Task.FromResult(Rows.TryGetValue(definition.ModuleId, out var data)
                ? data
                : new WorkbenchData([], 0, page, pageSize));
        }

        public Task<IReadOnlyList<Dictionary<string, object?>>> GetExportRowsByKeysAsync(
            WorkbenchDefinition definition, IReadOnlyList<IReadOnlyList<string>> keys, CancellationToken token,
            int? groupIndex = null, string? groupValue = null,
            IReadOnlyList<WorkbenchField>? exportFields = null, string? dataFilter = null)
            => Task.FromResult<IReadOnlyList<Dictionary<string, object?>>>([]);

        public Task<FormDefinition?> GetFormDefinitionAsync(
            WorkbenchDefinition definition, string userId, string mode,
            bool canViewCost, bool canViewSecrecy,
            IReadOnlySet<string> deniedMasterFields, IReadOnlySet<string> deniedDetailFields,
            IReadOnlySet<string> deniedNewMasterFields, IReadOnlySet<string> deniedNewDetailFields,
            IReadOnlySet<string> deniedModiMasterFields, IReadOnlySet<string> deniedModiDetailFields,
            CancellationToken token,
            bool canAddNew = false, bool canEdit = false, bool canDelete = false,
            bool canApprove = false, bool canDeapprove = false,
            bool canEndCase = false, bool canUnEndCase = false,
            bool canFileView = false, bool canFileUpda = false,
            bool canFileEdit = false, bool canFileDele = false, bool canSetup = false)
            => Task.FromResult<FormDefinition?>(null);
    }

    internal static WorkbenchDefinition Definition(
        int moduleId,
        string title,
        IReadOnlyList<string> fieldKeys,
        IReadOnlyList<string>? pkOrder = null)
    {
        var fields = fieldKeys
            .Select(key => new WorkbenchField(key, key, "nvarchar", 100, null, (pkOrder ?? []).Contains(key)))
            .ToArray();
        return new WorkbenchDefinition(
            moduleId, title, $"T{moduleId}", null, fields, [], null,
            HasAdd: true, HasEdit: true, DetailNoSave: false,
            MasterPkOrder: pkOrder ?? [], DetailNoFields: string.Empty, HasWorkflow: false);
    }

    internal static CurrentUserContext UserContext(
        string userId = "u1",
        string employeeName = "张三",
        string employeeId = "E001",
        string departmentId = "CG",
        string departmentName = "采购部",
        string companyId = "C1",
        string defaultGroupId = "G1")
    {
        var context = new DefaultHttpContext();
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, userId),
            new Claim(ClaimTypes.Name, employeeName),
            new Claim("employee_id", employeeId),
            new Claim("department_id", departmentId),
            new Claim("department_name", departmentName),
            new Claim("company_id", companyId),
            new Claim("default_group_id", defaultGroupId),
        ], "test"));
        return new CurrentUserContext(new HttpContextAccessor { HttpContext = context });
    }

    internal static AssistantSituationBudget Budget(
        Action<AssistantSituationBudgetOptions>? configure = null)
    {
        var options = new AssistantSituationBudgetOptions();
        configure?.Invoke(options);
        return new AssistantSituationBudget(
            Microsoft.Extensions.Options.Options.Create(options),
            NullLogger<AssistantSituationBudget>.Instance);
    }
}
