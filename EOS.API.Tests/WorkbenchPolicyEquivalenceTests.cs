using EOS.API.Data;
using EOS.API.Data.Workbench;
using EOS.API.Models;
using EOS.API.Security;
using Microsoft.Extensions.Options;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 统一工作台策略层下沉的等价性证据。
///
/// 把重构前的判据（HEAD 的 <c>DocumentWorkbenchController.cs</c>）原样内联成基准
/// <see cref="Retired"/>，逐格断言新策略服务的决策与基准一致——包含失败响应形态
/// （404 / 403 / 400）与响应体里的错误码与文案。
/// 这些退役判据只存在于本用例，不在生产代码里保留。
///
/// 矩阵维度：写名单 ∈/∉ × 只读名单 ∈/∉ × NEW_URL / MODI_URL ∈{无值, 自定义页, 统一表单动作路由}
/// × 权限位组合 × 表单模式 × 流程动作 × 幂等键；并按"定义可装配 / 装配不出表单"两档各跑一遍。
/// </summary>
public class WorkbenchPolicyEquivalenceTests
{
    private const int ModuleId = 1001;

    private static readonly string?[] ActionUrls =
        [null, "/admin/fields", $"/workbench/{ModuleId}/new"];

    private static readonly WorkbenchPolicyEquivalenceTests.Profile[] Profiles =
    [
        new("none", Browse: false, AddNew: false, Edit: false, Delete: false, Setup: false, Approve: false, Deapprove: false, EndCase: false, UnEndCase: false),
        new("browse", true, false, false, false, false, false, false, false, false),
        new("browse+add", true, true, false, false, false, false, false, false, false),
        new("browse+edit", true, false, true, false, false, false, false, false, false),
        new("browse+delete", true, false, false, true, false, false, false, false, false),
        new("browse+setup", true, false, false, false, true, false, false, false, false),
        new("browse+approve", true, false, false, false, false, true, false, false, false),
        new("browse+deapprove", true, false, false, false, false, false, true, false, false),
        new("browse+endcase", true, false, false, false, false, false, false, true, false),
        new("browse+unendcase", true, false, false, false, false, false, false, false, true),
        new("browse+addeditdelete", true, true, true, true, false, false, false, false, false),
        new("all", true, true, true, true, true, true, true, true, true),
    ];

    // ── #1 定义装配（覆盖 definition / records / query / details / export / export-selected / columns）──

    [Fact]
    public async Task Definition_access_matches_retired_predicates()
    {
        var mismatches = new List<string>();
        var outcomes = new HashSet<string>();

        foreach (var cell in Cells())
        {
            var policy = BuildPolicy(cell);
            var actual = Describe(await policy.AuthorizeDefinitionAsync("u1", ModuleId, default));
            var expected = Retired.AuthorizedDefinition("u1", cell.Rights, cell.Definition, cell.FormEnabled, cell.FormReadOnly, ModuleId);
            Compare(mismatches, cell, "GET definition/records/query/details/export/columns (#1)", expected, actual);
            outcomes.Add(actual.Outcome);
        }

        AssertMatrixSound(mismatches, outcomes, "ALLOW", "NOT_FOUND");
    }

    // ── #2 表单访问（覆盖 GET record 的 view/edit 回退、POST record、PUT record、form-chooser）──

    [Fact]
    public async Task Form_access_matches_retired_predicates()
    {
        var mismatches = new List<string>();
        var outcomes = new HashSet<string>();

        foreach (var cell in Cells())
        {
            var policy = BuildPolicy(cell);
            foreach (var mode in new[] { "view", "new", "edit" })
            {
                var actual = Describe(await policy.AuthorizeFormAsync("u1", ModuleId, mode, default));
                var expected = Retired.FormAccess("u1", cell.Rights, cell.Definition, cell.FormEnabled, cell.FormReadOnly, ModuleId, mode, cell.Form, cell.FormAvailable);
                Compare(mismatches, cell, $"FormAccess mode={mode} (#2)", expected, actual);
                outcomes.Add(actual.Outcome);
            }
        }

        AssertMatrixSound(mismatches, outcomes, "ALLOW", "NOT_FOUND");
    }

    // ── #2 删除路径（DELETE record：编辑访问 + 删除动作位）──

    [Fact]
    public async Task Delete_access_matches_retired_predicates()
    {
        var mismatches = new List<string>();
        var outcomes = new HashSet<string>();

        foreach (var cell in Cells())
        {
            var policy = BuildPolicy(cell);
            var actual = await PolicyDelete(policy);
            var expected = Retired.DeleteAccess("u1", cell.Rights, cell.Definition, cell.FormEnabled, cell.FormReadOnly, ModuleId, cell.Form, cell.FormAvailable);
            Compare(mismatches, cell, "DELETE record (#2 + Delete 动作位)", expected, actual);
            outcomes.Add(actual.Outcome);
        }

        AssertMatrixSound(mismatches, outcomes, "ALLOW", "NOT_FOUND", "FORBIDDEN");
    }

    // ── #10 form-definition（403 与 404 并存的那条路径）──

    [Fact]
    public async Task Form_definition_matches_retired_predicates()
    {
        var mismatches = new List<string>();
        var outcomes = new HashSet<string>();

        foreach (var cell in Cells())
        {
            var policy = BuildPolicy(cell);
            foreach (var mode in new[] { "new", "edit", "view", "NEW", "bogus" })
            {
                var actual = Describe(await policy.AuthorizeFormDefinitionAsync("u1", ModuleId, mode, default));
                var expected = Retired.FormDefinition("u1", cell.Rights, cell.Definition, cell.FormEnabled, cell.FormReadOnly, ModuleId, mode, cell.Form, cell.FormAvailable);
                Compare(mismatches, cell, $"FormDefinition mode={mode} (#10)", expected, actual);
                outcomes.Add(actual.Outcome);
            }
        }

        AssertMatrixSound(mismatches, outcomes, "ALLOW", "NOT_FOUND", "FORBIDDEN", "BAD_REQUEST");
    }

    // ── #3 自定义动作入口（POST action/{actionKey}、GET actions）──

    [Fact]
    public async Task Action_access_matches_retired_predicates()
    {
        var mismatches = new List<string>();
        var outcomes = new HashSet<string>();

        foreach (var cell in Cells())
        {
            var policy = BuildPolicy(cell);
            var actual = Describe(await policy.AuthorizeActionAsync("u1", ModuleId, default));
            var expected = Retired.ActionAccess("u1", cell.Rights, cell.Definition, cell.FormEnabled, cell.FormReadOnly, ModuleId, cell.Form, cell.FormAvailable);
            Compare(mismatches, cell, "POST action/{actionKey} (#3)", expected, actual);
            outcomes.Add(actual.Outcome);

            var actionsActual = Describe(await policy.AuthorizeUserActionsAsync("u1", ModuleId, default));
            var actionsExpected = Retired.UserActions("u1", cell.Rights, cell.Definition, cell.FormEnabled, cell.FormReadOnly, ModuleId);
            Compare(mismatches, cell, "GET actions (#1 + 能力位)", actionsExpected, actionsActual);
            outcomes.Add(actionsActual.Outcome);
        }

        AssertMatrixSound(mismatches, outcomes, "ALLOW", "NOT_FOUND");
    }

    // ── #4 流程闸门（approve / deapprove / endcase / unendcase）──

    [Fact]
    public async Task Workflow_access_matches_retired_predicates()
    {
        var mismatches = new List<string>();
        var outcomes = new HashSet<string>();

        foreach (var cell in Cells())
        {
            foreach (var action in new[] { PermissionAction.Approve, PermissionAction.Deapprove, PermissionAction.EndCase, PermissionAction.UnEndCase })
            {
                var policy = BuildPolicy(cell);
                var actual = await PolicyWorkflow(policy, action);
                var expected = Retired.Workflow("u1", cell.Rights, cell.Definition, cell.FormEnabled, cell.FormReadOnly, ModuleId, action);
                Compare(mismatches, cell, $"POST {action.ToString().ToLowerInvariant()} (#4)", expected, actual);
                outcomes.Add(actual.Outcome);
            }
        }

        AssertMatrixSound(mismatches, outcomes, "ALLOW", "NOT_FOUND", "FORBIDDEN");
    }

    // ── #7 字段维护面（field-settings / column-widths，403 口径）──

    [Fact]
    public async Task Setup_access_matches_retired_predicates()
    {
        var mismatches = new List<string>();
        var outcomes = new HashSet<string>();

        foreach (var cell in Cells())
        {
            foreach (var canSetupOnFieldAdmin in new[] { true, false })
            {
                var policy = BuildPolicy(cell, canSetupOnFieldAdmin);
                var actual = Describe(await policy.AuthorizeSetupAsync("u1", ModuleId, default));
                var expected = Retired.SetupDefinition("u1", cell.Rights, cell.Definition);
                Compare(mismatches, cell, $"field-settings (#7, canSetup2302={canSetupOnFieldAdmin})", expected, actual);
                outcomes.Add(actual.Outcome);
            }
        }

        AssertMatrixSound(mismatches, outcomes, "ALLOW", "FORBIDDEN");
    }

    // ── #8 数据范围 ──

    [Fact]
    public async Task Data_filter_matches_retired_predicates()
    {
        foreach (var cell in Cells())
        {
            var policy = BuildPolicy(cell);
            Assert.Equal(cell.Rights.DataFilter, await policy.GetDataFilterAsync("u1", ModuleId, default));
            Assert.Null(await policy.GetDataFilterAsync(null, ModuleId, default));
        }
    }

    // ── #9 幂等键 ──

    [Fact]
    public void Idempotency_key_matches_retired_predicates()
    {
        string?[] keys =
        [
            null,
            string.Empty,
            "   ",
            new string('a', 128),
            new string('b', 129),
            " abc ",
            "1",
            new string(' ', 129),
        ];

        foreach (var key in keys)
        {
            var expected = Retired.IdempotencyProblem(key);
            var actual = WorkbenchAccessPolicy.CheckIdempotencyKey(key);
            var actualDecision = actual is null
                ? new Decision("ALLOW", null, null, null, null)
                : new Decision("BAD_REQUEST", actual.Code, actual.Message, null, null);
            Assert.Equal(expected, actualDecision);
        }
    }

    /// <summary>
    /// 表单装配的入参必须与退役调用点逐字相同：参数错位不会改变"允许/拒绝"，
    /// 却会悄悄改变下发到界面的字段级权限与写动作位。
    /// </summary>
    [Fact]
    public async Task Form_build_arguments_match_retired_call_sites()
    {
        var cell = AllAllowedCell();

        // 退役调用点：FormAccess / ActionAccess 不传 canSetup（默认 false）；
        // form-definition 传 2302 模块的 CanSetup。
        var (policyForForm, sourceForForm) = Build(cell, canSetupOnFieldAdmin: false);
        await policyForForm.AuthorizeFormAsync("u1", ModuleId, "edit", default);
        Assert.Equal(ExpectedFormCall(cell, "edit", canSetup: false), sourceForForm.LastFormCall);

        await policyForForm.AuthorizeActionAsync("u1", ModuleId, default);
        Assert.Equal(ExpectedFormCall(cell, "view", canSetup: false), sourceForForm.LastFormCall);

        var (policyForDefinition, sourceForDefinition) = Build(cell, canSetupOnFieldAdmin: true);
        await policyForDefinition.AuthorizeFormDefinitionAsync("u1", ModuleId, "view", default);
        Assert.Equal(ExpectedFormCall(cell, "view", canSetup: true), sourceForDefinition.LastFormCall);
    }

    /// <summary>拒绝路径不得装配表单：老代码在权限位不足时不会走到表单装配。</summary>
    [Fact]
    public async Task Denied_access_does_not_build_a_form()
    {
        var denied = Cells().First(c => !c.Rights.CanBrowse);
        var (policy, source) = Build(denied);
        Assert.False((await policy.AuthorizeFormAsync("u1", ModuleId, "view", default)).Allowed);
        Assert.False((await policy.AuthorizeActionAsync("u1", ModuleId, default)).Allowed);
        Assert.Empty(source.FormModes);
    }

    private static void AssertMatrixSound(List<string> mismatches, HashSet<string> outcomes, params string[] requiredOutcomes)
    {
        Assert.True(mismatches.Count == 0,
            $"{mismatches.Count} 个判定格与退役判据不一致：\n{string.Join("\n", mismatches.Take(20))}");
        // 矩阵本身要有判别力：该路径可能出现的每一类结果都必须真的出现，
        // 否则"全绿"可能只是因为判定恒为同一种。
        foreach (var outcome in requiredOutcomes)
            Assert.Contains(outcome, outcomes);
    }

    private static void Compare(List<string> mismatches, Cell cell, string label, Decision expected, Decision actual)
    {
        if (expected != actual)
            mismatches.Add($"  [{label}] {cell}\n    基准: {expected}\n    策略: {actual}");
    }

    private static async Task<Decision> PolicyDelete(WorkbenchAccessPolicy policy)
    {
        try
        {
            return Describe(await policy.AuthorizeDeleteAsync("u1", ModuleId, default));
        }
        catch (PermissionDeniedException)
        {
            // 原样语义：删除动作位不满足即抛，调用方不捕获 → 403。
            return new Decision("FORBIDDEN", null, null, null, null);
        }
    }

    private static async Task<Decision> PolicyWorkflow(WorkbenchAccessPolicy policy, PermissionAction action)
    {
        try
        {
            return Describe(await policy.AuthorizeWorkflowAsync("u1", ModuleId, action, default));
        }
        catch (PermissionDeniedException)
        {
            // IPermissionService.RequireAsync 的原样语义：不满足即抛，调用方不捕获 → 403。
            return new Decision("FORBIDDEN", null, null, null, null);
        }
    }

    private static Decision Describe(WorkbenchDecision<WorkbenchDefinitionAccess> decision)
        => decision.Allowed
            ? new Decision("ALLOW", null, null, Signature(decision.Value!.Definition), null)
            : DescribeDenial(decision.Denial!);

    private static Decision Describe(WorkbenchDecision<WorkbenchFormAccess> decision)
        => decision.Allowed
            ? new Decision("ALLOW", null, null, Signature(decision.Value!.Definition), FormSignature(decision.Value.Form))
            : DescribeDenial(decision.Denial!);

    private static Decision DescribeDenial(WorkbenchDenial denial)
        // 只有 400 的响应体会带错误码与文案；404/403 的响应体不承载原因码（原因码供日志与助手侧解释用）。
        => denial.Kind == WorkbenchDenialKind.InvalidRequest
            ? new Decision(OutcomeName(denial.Kind), denial.Code, denial.Message, null, null)
            : new Decision(OutcomeName(denial.Kind), null, null, null, null);

    private static string OutcomeName(WorkbenchDenialKind kind) => kind switch
    {
        WorkbenchDenialKind.NotFound => "NOT_FOUND",
        WorkbenchDenialKind.Forbidden => "FORBIDDEN",
        WorkbenchDenialKind.InvalidRequest => "BAD_REQUEST",
        _ => "UNKNOWN",
    };

    private static string Signature(WorkbenchDefinition definition)
        => $"add={definition.HasAdd};edit={definition.HasEdit};delete={definition.CanDelete}";

    private static string FormSignature(FormDefinition form)
        => $"add={form.HasAdd};edit={form.HasEdit};copy={form.IfCopy};delete={form.CanDelete}" +
           $";approve={form.CanApprove};deapprove={form.CanDeapprove};endcase={form.CanEndCase};unendcase={form.CanUnEndCase}";

    private static IEnumerable<Cell> Cells()
    {
        foreach (var formAvailable in new[] { true, false })
            foreach (var writable in new[] { true, false })
                foreach (var readOnly in new[] { true, false })
                    foreach (var newUrl in ActionUrls)
                        foreach (var modiUrl in ActionUrls)
                            foreach (var profile in Profiles)
                                yield return new Cell(formAvailable, writable, readOnly, newUrl, modiUrl, profile, RawDefinition(newUrl, modiUrl), RawForm(), RightsFor(profile));
    }

    private static WorkbenchDefinition RawDefinition(string? newUrl, string? modiUrl) => new(
        ModuleId, "测试模块", "T1001", null, [], [], null,
        HasAdd: false, HasEdit: false, DetailNoSave: false,
        MasterPkOrder: [], DetailNoFields: string.Empty, HasWorkflow: false,
        NewUrl: newUrl, ModiUrl: modiUrl);

    private static FormDefinition RawForm() => new(
        ModuleId, "测试模块", "T1001", null,
        HasAdd: true, HasEdit: true, Mode: "view",
        MasterFields: [], DetailFields: [], MasterPkOrder: [], DetailNoFields: string.Empty, DetailDfVerify: string.Empty,
        IfCopy: true, CanDelete: true, CanApprove: true, CanDeapprove: true, CanEndCase: true, CanUnEndCase: true);

    private static ModuleRights RightsFor(Profile profile) => new(
        CanBrowse: profile.Browse,
        CanViewCost: true,
        CanViewSecrecy: true,
        CanSetup: profile.Setup,
        DeniedMasterFields: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
        DeniedDetailFields: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
        CanAddNew: profile.AddNew,
        CanEdit: profile.Edit,
        CanDelete: profile.Delete,
        CanApprove: profile.Approve,
        CanDeapprove: profile.Deapprove,
        CanEndCase: profile.EndCase,
        CanUnEndCase: profile.UnEndCase,
        CanFileView: true,
        CanFileUpda: true,
        CanFileEdit: true,
        CanFileDele: true,
        DenyNewMasterFields: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
        DenyNewDetailFields: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
        DenyModiMasterFields: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
        DenyModiDetailFields: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
        DataFilter: "DATA_FILTER",
        ExecuteTag: "A");

    private static WorkbenchAccessPolicy BuildPolicy(Cell cell, bool canSetupOnFieldAdmin = true)
        => Build(cell, canSetupOnFieldAdmin).Policy;

    private static (WorkbenchAccessPolicy Policy, FakeDefinitionSource Source) Build(Cell cell, bool canSetupOnFieldAdmin = true)
    {
        var settings = new UnifiedFormEditorSettings
        {
            EnabledModuleIds = cell.FormEnabled ? [ModuleId] : [],
            ReadOnlyModuleIds = cell.FormReadOnly ? [ModuleId] : [],
        };
        var source = new FakeDefinitionSource
        {
            Definition = cell.Definition,
            Form = cell.Form,
            FormAvailable = cell.FormAvailable,
        };
        var fieldAdminRights = RightsFor(new Profile("field-admin", false, false, false, false, canSetupOnFieldAdmin, false, false, false, false));
        return (new WorkbenchAccessPolicy(source, new FakePermissions(cell.Rights, fieldAdminRights), Options.Create(settings)), source);
    }

    /// <summary>写名单内、全权限、无自定义路由的一格：三条表单装配调用点都可达。</summary>
    private static Cell AllAllowedCell()
    {
        var profile = Profiles[^1];
        return new Cell(true, true, false, null, null, profile, RawDefinition(null, null), RawForm(), RightsFor(profile));
    }

    private static FormCall ExpectedFormCall(Cell cell, string mode, bool canSetup) => new(
        cell.Definition.ModuleId, "u1", mode,
        cell.Rights.CanViewCost, cell.Rights.CanViewSecrecy,
        cell.Rights.DeniedMasterFields, cell.Rights.DeniedDetailFields,
        cell.Rights.DenyNewMasterFields, cell.Rights.DenyNewDetailFields,
        cell.Rights.DenyModiMasterFields, cell.Rights.DenyModiDetailFields,
        cell.Rights.CanAddNew, cell.Rights.CanEdit, cell.Rights.CanDelete, cell.Rights.CanApprove, cell.Rights.CanDeapprove,
        cell.Rights.CanEndCase, cell.Rights.CanUnEndCase,
        cell.Rights.CanFileView, cell.Rights.CanFileUpda, cell.Rights.CanFileEdit, cell.Rights.CanFileDele,
        canSetup);

    private sealed record Profile(string Name, bool Browse, bool AddNew, bool Edit, bool Delete, bool Setup, bool Approve, bool Deapprove, bool EndCase, bool UnEndCase);

    private sealed record Decision(string Outcome, string? Code, string? Message, string? DefinitionSignature, string? FormSignature);

    /// <summary>表单装配调用入参快照：逐字比对退役调用点传了什么。</summary>
    private sealed record FormCall(
        int DefinitionModuleId, string UserId, string Mode,
        bool CanViewCost, bool CanViewSecrecy,
        IReadOnlySet<string> DeniedMasterFields, IReadOnlySet<string> DeniedDetailFields,
        IReadOnlySet<string> DeniedNewMasterFields, IReadOnlySet<string> DeniedNewDetailFields,
        IReadOnlySet<string> DeniedModiMasterFields, IReadOnlySet<string> DeniedModiDetailFields,
        bool CanAddNew, bool CanEdit, bool CanDelete, bool CanApprove, bool CanDeapprove,
        bool CanEndCase, bool CanUnEndCase,
        bool CanFileView, bool CanFileUpda, bool CanFileEdit, bool CanFileDele,
        bool CanSetup)
    {
        // 集合按内容比较（record 的默认相等对 IReadOnlySet 是引用比较）。
        public bool Equals(FormCall? other)
            => other is not null
               && (DefinitionModuleId, UserId, Mode, CanViewCost, CanViewSecrecy, CanAddNew, CanEdit, CanDelete,
                   CanApprove, CanDeapprove, CanEndCase, CanUnEndCase, CanFileView, CanFileUpda, CanFileEdit, CanFileDele, CanSetup)
                  == (other.DefinitionModuleId, other.UserId, other.Mode, other.CanViewCost, other.CanViewSecrecy, other.CanAddNew,
                      other.CanEdit, other.CanDelete, other.CanApprove, other.CanDeapprove, other.CanEndCase, other.CanUnEndCase,
                      other.CanFileView, other.CanFileUpda, other.CanFileEdit, other.CanFileDele, other.CanSetup)
               && DeniedMasterFields.SetEquals(other.DeniedMasterFields)
               && DeniedDetailFields.SetEquals(other.DeniedDetailFields)
               && DeniedNewMasterFields.SetEquals(other.DeniedNewMasterFields)
               && DeniedNewDetailFields.SetEquals(other.DeniedNewDetailFields)
               && DeniedModiMasterFields.SetEquals(other.DeniedModiMasterFields)
               && DeniedModiDetailFields.SetEquals(other.DeniedModiDetailFields);

        public override int GetHashCode() => HashCode.Combine(DefinitionModuleId, UserId, Mode, CanSetup);
    }

    private sealed record Cell(
        bool FormAvailable, bool FormEnabled, bool FormReadOnly, string? NewUrl, string? ModiUrl,
        Profile RightsProfile, WorkbenchDefinition Definition, FormDefinition Form, ModuleRights Rights)
    {
        public override string ToString()
            => $"formAvailable={FormAvailable} writable={FormEnabled} readOnly={FormReadOnly} newUrl={NewUrl ?? "<null>"} " +
               $"modiUrl={ModiUrl ?? "<null>"} profile={RightsProfile.Name}";
    }

    private sealed class FakeDefinitionSource : IWorkbenchDefinitionSource
    {
        public WorkbenchDefinition? Definition { get; set; }
        public FormDefinition? Form { get; set; }
        public bool FormAvailable { get; set; } = true;
        public List<string> FormModes { get; } = [];
        public FormCall? LastFormCall { get; private set; }

        public Task<WorkbenchDefinition?> GetDefinitionAsync(
            int moduleId, string userId, string? execTag, bool canViewCost, bool canViewSecrecy,
            IReadOnlySet<string> deniedMasterFields, IReadOnlySet<string> deniedDetailFields,
            CancellationToken token)
            => Task.FromResult(Definition);

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
            FormModes.Add(mode);
            LastFormCall = new FormCall(
                definition.ModuleId, userId, mode, canViewCost, canViewSecrecy,
                deniedMasterFields, deniedDetailFields, deniedNewMasterFields, deniedNewDetailFields,
                deniedModiMasterFields, deniedModiDetailFields,
                canAddNew, canEdit, canDelete, canApprove, canDeapprove, canEndCase, canUnEndCase,
                canFileView, canFileUpda, canFileEdit, canFileDele, canSetup);
            return Task.FromResult(FormAvailable ? Form : null);
        }
    }

    private sealed class FakePermissions(ModuleRights rights, ModuleRights fieldAdminRights) : IPermissionService
    {
        public Task<ModulePermission> GetAsync(string userId, int moduleId, CancellationToken cancellationToken)
            => Task.FromResult(new ModulePermission(RightsFor(moduleId)));

        public Task<ModulePermission> RequireAsync(string userId, int moduleId, PermissionAction action, CancellationToken cancellationToken)
        {
            var permission = new ModulePermission(RightsFor(moduleId));
            if (!permission.Can(action)) throw new PermissionDeniedException(userId, moduleId, action);
            return Task.FromResult(permission);
        }

        private ModuleRights RightsFor(int moduleId)
            => moduleId == WorkbenchAccessPolicy.FieldAdminModuleId ? fieldAdminRights : rights;
    }

    /// <summary>
    /// 已退役的判据（原样内联，作为本用例的基准）：抄自重构前的
    /// <c>EOS.API/Controllers/DocumentWorkbenchController.cs</c>。
    /// 不在生产代码里保留。
    /// </summary>
    private static class Retired
    {
        public static Decision AuthorizedDefinition(string? userId, ModuleRights rights, WorkbenchDefinition? raw, bool formEnabled, bool formReadOnly, int moduleId)
        {
            if (userId is null) return Denied("NOT_FOUND");
            if (!rights.CanBrowse) return Denied("NOT_FOUND");
            if (raw is null) return Denied("NOT_FOUND");
            var addRoute = raw.NewUrl is not null && (!ModuleRouteValidator.IsUnifiedFormRoute(raw.NewUrl, moduleId) || formEnabled);
            var editRoute = raw.ModiUrl is not null && (!ModuleRouteValidator.IsUnifiedFormRoute(raw.ModiUrl, moduleId) || formEnabled);
            var definition = raw with
            {
                HasAdd = addRoute || formEnabled,
                HasEdit = editRoute || formEnabled || formReadOnly,
                CanDelete = rights.CanDelete,
            };
            return Allowed(definition);
        }

        public static Decision FormAccess(string? userId, ModuleRights rights, WorkbenchDefinition? raw, bool formEnabled, bool formReadOnly, int moduleId, string mode, FormDefinition form, bool formAvailable)
        {
            var definition = AuthorizedDefinition(userId, rights, raw, formEnabled, formReadOnly, moduleId);
            if (definition.Outcome != "ALLOW") return definition;
            var folded = Folded(raw, formEnabled, formReadOnly, rights, moduleId);
            if (mode == "view" ? !(formEnabled || formReadOnly) : !formEnabled) return Denied("NOT_FOUND");
            if (mode == "new" && !rights.CanAddNew) return Denied("NOT_FOUND");
            if (mode == "edit" && !rights.CanEdit) return Denied("NOT_FOUND");
            if (mode == "view" && !rights.CanBrowse) return Denied("NOT_FOUND");
            if (mode == "new" && !folded.HasAdd) return Denied("NOT_FOUND");
            if (mode == "edit" && !folded.HasEdit) return Denied("NOT_FOUND");
            if (!formAvailable) return Denied("NOT_FOUND");
            return Allowed(folded, formEnabled ? form : WithoutWriteActions(form));
        }

        public static Decision DeleteAccess(string? userId, ModuleRights rights, WorkbenchDefinition? raw, bool formEnabled, bool formReadOnly, int moduleId, FormDefinition form, bool formAvailable)
        {
            var edit = FormAccess(userId, rights, raw, formEnabled, formReadOnly, moduleId, "edit", form, formAvailable);
            if (edit.Outcome != "ALLOW") return edit;
            // 原样语义：RequireAsync(Delete) 不满足即抛 → 403。
            return Can(rights, PermissionAction.Delete) ? edit : Denied("FORBIDDEN");
        }

        public static Decision ActionAccess(string? userId, ModuleRights rights, WorkbenchDefinition? raw, bool formEnabled, bool formReadOnly, int moduleId, FormDefinition form, bool formAvailable)
        {
            var definition = AuthorizedDefinition(userId, rights, raw, formEnabled, formReadOnly, moduleId);
            if (definition.Outcome != "ALLOW") return definition;
            var folded = Folded(raw, formEnabled, formReadOnly, rights, moduleId);
            if (!folded.HasAdd && !folded.HasEdit) return Denied("NOT_FOUND");
            if (!formAvailable) return Denied("NOT_FOUND");
            return Allowed(folded, form);
        }

        public static Decision UserActions(string? userId, ModuleRights rights, WorkbenchDefinition? raw, bool formEnabled, bool formReadOnly, int moduleId)
        {
            var definition = AuthorizedDefinition(userId, rights, raw, formEnabled, formReadOnly, moduleId);
            if (definition.Outcome != "ALLOW") return definition;
            var folded = Folded(raw, formEnabled, formReadOnly, rights, moduleId);
            if (!folded.HasAdd && !folded.HasEdit) return Denied("NOT_FOUND");
            return Allowed(folded);
        }

        public static Decision FormDefinition(string? userId, ModuleRights rights, WorkbenchDefinition? raw, bool formEnabled, bool formReadOnly, int moduleId, string mode, FormDefinition form, bool formAvailable)
        {
            var definition = AuthorizedDefinition(userId, rights, raw, formEnabled, formReadOnly, moduleId);
            if (definition.Outcome != "ALLOW") return definition;
            if (userId is null) return Denied("NOT_FOUND");
            if (!formEnabled && !formReadOnly) return Denied("NOT_FOUND");
            var normalized = mode.Trim().ToLowerInvariant();
            if (normalized is not ("new" or "edit" or "view")) return new Decision("BAD_REQUEST", "INVALID_FORM_MODE", "mode 仅支持 new、edit 或 view。", null, null);
            if (normalized != "view" && !formEnabled) return Denied("NOT_FOUND");
            if (normalized == "new" && !rights.CanAddNew) return Denied("FORBIDDEN");
            if (normalized == "edit" && !rights.CanEdit) return Denied("FORBIDDEN");
            if (normalized == "view" && !rights.CanBrowse) return Denied("FORBIDDEN");
            var folded = Folded(raw, formEnabled, formReadOnly, rights, moduleId);
            if (normalized == "new" && !folded.HasAdd) return Denied("NOT_FOUND");
            if (normalized == "edit" && !folded.HasEdit) return Denied("NOT_FOUND");
            if (!formAvailable) return Denied("NOT_FOUND");
            return Allowed(folded, formEnabled ? form : WithoutWriteActions(form));
        }

        public static Decision Workflow(string? userId, ModuleRights rights, WorkbenchDefinition? raw, bool formEnabled, bool formReadOnly, int moduleId, PermissionAction action)
        {
            var definition = AuthorizedDefinition(userId, rights, raw, formEnabled, formReadOnly, moduleId);
            if (definition.Outcome != "ALLOW") return definition;
            var folded = Folded(raw, formEnabled, formReadOnly, rights, moduleId);
            if (!HasWritablePage(folded, formEnabled, moduleId)) return Denied("NOT_FOUND");
            return Can(rights, action) ? Allowed(folded) : Denied("FORBIDDEN");
        }

        public static Decision SetupDefinition(string? userId, ModuleRights rights, WorkbenchDefinition? raw)
        {
            if (userId is null) return Denied("FORBIDDEN");
            if (!(rights.CanBrowse && rights.CanSetup)) return Denied("FORBIDDEN");
            if (raw is null) return Denied("FORBIDDEN");
            // 字段维护面下发的是**未折叠**的定义。
            return Allowed(raw);
        }

        public static Decision IdempotencyProblem(string? idempotencyKey)
            => string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Trim().Length > 128
                ? new Decision("BAD_REQUEST", "IDEMPOTENCY_KEY_REQUIRED",
                    "写操作缺少有效幂等键（请求体 idempotencyKey 或 X-Idempotency-Key 请求头，≤128 字符）。", null, null)
                : new Decision("ALLOW", null, null, null, null);

        private static bool HasWritablePage(WorkbenchDefinition definition, bool formEnabled, int moduleId)
            => formEnabled
            || (definition.NewUrl is not null && !ModuleRouteValidator.IsUnifiedFormRoute(definition.NewUrl, moduleId))
            || (definition.ModiUrl is not null && !ModuleRouteValidator.IsUnifiedFormRoute(definition.ModiUrl, moduleId));

        private static bool Can(ModuleRights rights, PermissionAction action) => action switch
        {
            PermissionAction.Browse => rights.CanBrowse,
            PermissionAction.AddNew => rights.CanAddNew,
            PermissionAction.Edit => rights.CanEdit,
            PermissionAction.Delete => rights.CanDelete,
            PermissionAction.Approve => rights.CanApprove,
            PermissionAction.Deapprove => rights.CanDeapprove,
            PermissionAction.EndCase => rights.CanEndCase,
            PermissionAction.UnEndCase => rights.CanUnEndCase,
            PermissionAction.Setup => rights.CanSetup,
            PermissionAction.FileView => rights.CanFileView,
            PermissionAction.FileUpload => rights.CanFileUpda,
            PermissionAction.FileEdit => rights.CanFileEdit,
            PermissionAction.FileDelete => rights.CanFileDele,
            _ => false,
        };

        private static WorkbenchDefinition Folded(WorkbenchDefinition? raw, bool formEnabled, bool formReadOnly, ModuleRights rights, int moduleId)
        {
            var addRoute = raw!.NewUrl is not null && (!ModuleRouteValidator.IsUnifiedFormRoute(raw.NewUrl, moduleId) || formEnabled);
            var editRoute = raw.ModiUrl is not null && (!ModuleRouteValidator.IsUnifiedFormRoute(raw.ModiUrl, moduleId) || formEnabled);
            return raw with
            {
                HasAdd = addRoute || formEnabled,
                HasEdit = editRoute || formEnabled || formReadOnly,
                CanDelete = rights.CanDelete,
            };
        }

        private static FormDefinition WithoutWriteActions(FormDefinition form) => form with
        {
            HasAdd = false,
            HasEdit = false,
            IfCopy = false,
            CanDelete = false,
            CanApprove = false,
            CanDeapprove = false,
            CanEndCase = false,
            CanUnEndCase = false,
        };

        private static Decision Allowed(WorkbenchDefinition definition) => Allowed(definition, null);

        private static Decision Allowed(WorkbenchDefinition definition, FormDefinition? form)
            => new("ALLOW", null, null,
                $"add={definition.HasAdd};edit={definition.HasEdit};delete={definition.CanDelete}",
                form is null ? null : $"add={form.HasAdd};edit={form.HasEdit};copy={form.IfCopy};delete={form.CanDelete}" +
                                       $";approve={form.CanApprove};deapprove={form.CanDeapprove};endcase={form.CanEndCase};unendcase={form.CanUnEndCase}");

        private static Decision Denied(string outcome) => new(outcome, null, null, null, null);
    }
}
