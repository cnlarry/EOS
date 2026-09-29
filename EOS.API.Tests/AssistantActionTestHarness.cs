using EOS.API.Data;
using EOS.API.Data.Effects;
using EOS.API.Data.Workbench;
using EOS.API.Features.Assistant.Actions;
using EOS.API.Features.Assistant.Governance;
using EOS.API.Models;
using EOS.API.Security;
using EOS.API.Telemetry;
using EOS.API.Tests.Tools;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace EOS.API.Tests;

/// <summary>
/// 助手动作层用例的装配：**真实的写管线 + 真实的策略层**，只把"定义来源"与"权限"换成固定实现。
///
/// <para>
/// 定义来源是真库装配链里唯一与数据无关的一环（策略层只按它取定义），替换它就能在真库上
/// 精确控制"这个模块在不在写名单里、这个用户有哪些动作位"，而不必为了造这些状态去动元数据。
/// </para>
/// </summary>
internal static class AssistantActionTestHarness
{
    public static DbConnectionFactory Connections(string connectionString) =>
        new(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:ErpDatabase"] = connectionString })
            .Build());

    public static AgentWriteContext AgentContext() => new();

    public static WorkbenchAuditWriter AuditWriter(
        DbConnectionFactory connections, AgentWriteContext? agentContext = null) =>
        new(connections, new HttpContextAccessor(),
            new WorkbenchDefinitionProvider(connections, NullLogger<WorkbenchDefinitionProvider>.Instance),
            Options.Create(new AuditSettings()), agentContext);

    public static WorkbenchCommandHandler CommandHandler(
        DbConnectionFactory connections, string connectionString, WorkbenchAuditWriter auditWriter)
    {
        var engine = new EffectEngineInvoker(
            new EffectEngineSettings { Enabled = true },
            new EffectPlanLoader(),
            EffectShadowRunner.BuildPipelineFor(connectionString),
            NullLogger<EffectEngineInvoker>.Instance);
        var provider = new WorkbenchDefinitionProvider(connections, NullLogger<WorkbenchDefinitionProvider>.Instance);
        var workflow = new WorkflowEngine(connections, auditWriter, provider, engine, NullLogger<WorkflowEngine>.Instance);
        var idempotency = new WorkbenchIdempotency();
        var approval = new WorkbenchApprovalService(connections, auditWriter, workflow, engine, idempotency,
            NullLogger<WorkbenchApprovalService>.Instance);
        return new WorkbenchCommandHandler(connections, auditWriter, approval,
            new WorkbenchScopeFilter(new ApiMetrics()), engine, idempotency,
            new DepotStockPolicyService(connections, auditWriter), new WorkbenchVirtualColumnResolver(),
            NullLogger<WorkbenchCommandHandler>.Instance);
    }

    /// <summary>
    /// 助手动作层。仓储只被当作写管线的门面使用（动作层只调它的新增/修改/删除），
    /// 因此其余协作者以占位符传入——它们不会被触达，构造真实的那些对用例没有信息量。
    /// </summary>
    public static AssistantRecordActionService ActionService(
        AssistantActionGate gate,
        WorkbenchCommandHandler handler,
        DbConnectionFactory connections,
        AgentWriteContext agentContext,
        WorkbenchAuditWriter auditWriter,
        AssistantActionLimitsOptions? limits = null)
        => new(gate, new DocumentWorkbenchRepository(
                connections, null!, null!, null!, handler, null!, null!, null!,
                NullLogger<DocumentWorkbenchRepository>.Instance),
            new EffectPlanLoader(), agentContext, auditWriter,
            Options.Create(limits ?? new AssistantActionLimitsOptions()),
            NullLogger<AssistantRecordActionService>.Instance);

    public static WorkbenchAccessPolicy Policy(
        IWorkbenchDefinitionSource definitions, IPermissionService permissions, params int[] writableModules)
        => new(definitions, permissions, Options.Create(new UnifiedFormEditorSettings
        {
            EnabledModuleIds = writableModules.Length == 0 ? [] : [.. writableModules],
        }));

    /// <summary>固定定义来源：无论请求什么权限上下文都返回同一份定义与表单。</summary>
    public sealed class FixedDefinitionSource(WorkbenchDefinition? definition, FormDefinition? form)
        : IWorkbenchDefinitionSource
    {
        public List<string> RequestedModes { get; } = [];

        public Task<WorkbenchDefinition?> GetDefinitionAsync(
            int moduleId, string userId, string? execTag, bool canViewCost, bool canViewSecrecy,
            IReadOnlySet<string> deniedMasterFields, IReadOnlySet<string> deniedDetailFields,
            CancellationToken token)
            => Task.FromResult(definition);

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
        {
            RequestedModes.Add(mode);
            return Task.FromResult(form);
        }
    }

    /// <summary>固定权限：该用户在所有模块上拥有同一组动作位（含删除动作位是否满足）。</summary>
    public sealed class FixedPermissions(ModuleRights rights) : IPermissionService
    {
        public Task<ModulePermission> GetAsync(string userId, int moduleId, CancellationToken cancellationToken)
            => Task.FromResult(new ModulePermission(rights));

        public Task<ModulePermission> RequireAsync(
            string userId, int moduleId, PermissionAction action, CancellationToken cancellationToken)
        {
            var permission = new ModulePermission(rights);
            if (!permission.Can(action))
            {
                throw new PermissionDeniedException(userId, moduleId, action);
            }
            return Task.FromResult(permission);
        }
    }

    /// <summary>权限档位：默认"能浏览 + 能增改删"，用例按需取子集。</summary>
    public static ModuleRights Rights(
        bool browse = true, bool add = true, bool edit = true, bool delete = true, string dataFilter = "",
        bool approve = false, bool deapprove = false, bool endCase = false, bool unEndCase = false)
        => new(
            CanBrowse: browse, CanViewCost: true, CanViewSecrecy: true, CanSetup: false,
            DeniedMasterFields: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            DeniedDetailFields: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            CanAddNew: add, CanEdit: edit, CanDelete: delete,
            CanApprove: approve, CanDeapprove: deapprove, CanEndCase: endCase, CanUnEndCase: unEndCase,
            CanFileView: true, CanFileUpda: true, CanFileEdit: true, CanFileDele: true,
            DenyNewMasterFields: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            DenyNewDetailFields: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            DenyModiMasterFields: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            DenyModiDetailFields: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            DataFilter: dataFilter,
            ExecuteTag: "A");
}
