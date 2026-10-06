using EOS.API.Models;
using EOS.API.Security;
using Microsoft.Extensions.Options;

namespace EOS.API.Data.Workbench;

/// <summary>策略判定被拒时的响应语义，与既有端点口径一一对应。</summary>
public enum WorkbenchDenialKind
{
    /// <summary>404：防探测口径——模块或记录不在当前用户的可访问范围内。</summary>
    NotFound,

    /// <summary>403：动作级权限位不足。</summary>
    Forbidden,

    /// <summary>400：请求本身不合法（表单模式、幂等键）。</summary>
    InvalidRequest,
}

/// <summary>
/// 拒绝原因。<see cref="Code"/> 是稳定标识（不是给人看的文案），
/// 供调用方产出响应、也供 AI 侧门禁解释"这件事为什么做不了"。
/// </summary>
public sealed record WorkbenchDenial(WorkbenchDenialKind Kind, string Code, string? Message = null);

/// <summary>判定结果：允许时携带载荷，拒绝时携带原因。</summary>
public sealed record WorkbenchDecision<T>(T? Value, WorkbenchDenial? Denial)
{
    public bool Allowed => Denial is null;

    public static WorkbenchDecision<T> Allow(T value) => new(value, null);

    public static WorkbenchDecision<T> Deny(WorkbenchDenial denial) => new(default, denial);
}

/// <summary>模块定义 + 该用户在该模块上的权限（字段级权限与行级数据范围随之下发）。</summary>
public sealed record WorkbenchDefinitionAccess(WorkbenchDefinition Definition, ModuleRights Rights);

/// <summary>统一表单访问载荷：模块定义 + 按模式装配的表单（只读名单下已折叠写动作）+ 权限。</summary>
public sealed record WorkbenchFormAccess(WorkbenchDefinition Definition, FormDefinition Form, ModuleRights Rights);

/// <summary>拒绝原因码：稳定标识，供响应体、日志与 AI 侧门禁解释使用。</summary>
public static class WorkbenchDenialCodes
{
    public const string NotAuthenticated = "NOT_AUTHENTICATED";
    public const string NotBrowsable = "NOT_BROWSABLE";
    public const string DefinitionUnavailable = "DEFINITION_UNAVAILABLE";
    public const string FormNotEnabled = "FORM_NOT_ENABLED";
    public const string ModeNotPermitted = "MODE_NOT_PERMITTED";
    public const string AddCapabilityMissing = "ADD_CAPABILITY_MISSING";
    public const string EditCapabilityMissing = "EDIT_CAPABILITY_MISSING";
    public const string FormUnavailable = "FORM_UNAVAILABLE";
    public const string NoWritableSurface = "NO_WRITABLE_SURFACE";
    public const string NoWritablePage = "NO_WRITABLE_PAGE";
    public const string SetupNotAllowed = "SETUP_NOT_ALLOWED";
    public const string InvalidFormMode = "INVALID_FORM_MODE";
    public const string IdempotencyKeyRequired = "IDEMPOTENCY_KEY_REQUIRED";

    /// <summary>删除动作位不足：<see cref="WorkbenchAccessPolicy.AuthorizeDeleteAsync"/> 会原样抛出，
    /// 故该码由消费方在捕获后补记，而不是判定结果自带。</summary>
    public const string DeleteNotPermitted = "DELETE_NOT_PERMITTED";
}

/// <summary>
/// 统一工作台的授权策略入口：模块可见性、统一表单名单、动作权限位、行级数据范围与写路径幂等键
/// 的判定全部收敛在这里，控制器只消费判定结果。
/// 判定结果是结构化的（允许/拒绝 + 稳定原因码 + 载荷），AI 侧门禁复用同一入口产出
/// 「哪些单可做、哪些不可做及原因」，不另写一套口径。
/// 权限真源仍是 <see cref="IPermissionService"/>；本类不做第二套权限读取或缓存。
/// </summary>
public sealed class WorkbenchAccessPolicy(
    IWorkbenchDefinitionSource definitions,
    IPermissionService permissions,
    IOptions<UnifiedFormEditorSettings> formSettings)
{
    /// <summary>字段维护（数据表/字段设置）模块 ID：表单标签右键进入字段设置页的权限门。</summary>
    public const int FieldAdminModuleId = 2302;

    /// <summary>幂等键不合法时的文案（与既有写路径逐字相同）。</summary>
    public const string IdempotencyKeyMessage = "写操作缺少有效幂等键（请求体 idempotencyKey 或 X-Idempotency-Key 请求头，≤128 字符）。";

    /// <summary>表单模式非法时的文案（与既有 form-definition 逐字相同）。</summary>
    public const string InvalidFormModeMessage = "mode 仅支持 new、edit 或 view。";

    /// <summary>模块在统一表单<b>写</b>名单内（可新增/修改/删除）。</summary>
    public bool IsFormWritable(int moduleId) => formSettings.Value.EnabledModuleIds.Contains(moduleId);

    /// <summary>模块在统一表单<b>只读</b>名单内（可浏览，写路径仍封）。</summary>
    public bool IsFormReadOnly(int moduleId) => formSettings.Value.ReadOnlyModuleIds.Contains(moduleId);

    /// <summary>
    /// 模块是否有<b>可写</b>的页面入口：**统一表单写名单**（迁移 321 之后这是唯一真源）。
    /// 批核/解批/结案/取消结案与新增/修改/删除共用这条判据——只读名单只放浏览，不放任何写动作；
    /// 自定义承载页（M_URL 是精确路径）不装配工作台定义，动作入口由页面自己负责。
    /// </summary>
    public bool HasWritablePage(int moduleId) => IsFormWritable(moduleId);

    /// <summary>写路径幂等键判定：空、全空白或超 128 字符即不合法（trim 后计长）。</summary>
    public static WorkbenchDenial? CheckIdempotencyKey(string? idempotencyKey)
        => string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Trim().Length > 128
            ? new WorkbenchDenial(WorkbenchDenialKind.InvalidRequest, WorkbenchDenialCodes.IdempotencyKeyRequired, IdempotencyKeyMessage)
            : null;

    /// <summary>
    /// 列表/读路径的定义装配：要求登录、模块可浏览，并把"能不能新增/编辑"折叠进
    /// <c>HasAdd</c>/<c>HasEdit</c>（真源是统一表单名单，见 <see cref="FoldRoutes"/>）。
    /// </summary>
    public async Task<WorkbenchDecision<WorkbenchDefinitionAccess>> AuthorizeDefinitionAsync(
        string? userId, int moduleId, CancellationToken token)
    {
        if (userId is null) return DenyDefinition(WorkbenchDenialCodes.NotAuthenticated);
        var rights = (await permissions.GetAsync(userId, moduleId, token)).Rights;
        if (!rights.CanBrowse) return DenyDefinition(WorkbenchDenialCodes.NotBrowsable);
        var definition = await definitions.GetDefinitionAsync(moduleId, userId, rights.ExecuteTag,
            rights.CanViewCost, rights.CanViewSecrecy, rights.DeniedMasterFields, rights.DeniedDetailFields, token);
        if (definition is null) return DenyDefinition(WorkbenchDenialCodes.DefinitionUnavailable);
        return WorkbenchDecision<WorkbenchDefinitionAccess>.Allow(
            new WorkbenchDefinitionAccess(FoldRoutes(definition, moduleId, rights), rights));
    }

    /// <summary>
    /// 统一表单访问：名单（写/只读按模式）+ 动作权限位 + 定义的能力位，任一条不满足即拒绝。
    /// 拒绝一律按防探测口径（404），调用方不得改口为 403。
    /// </summary>
    public async Task<WorkbenchDecision<WorkbenchFormAccess>> AuthorizeFormAsync(
        string? userId, int moduleId, string mode, CancellationToken token)
    {
        var definition = await AuthorizeDefinitionAsync(userId, moduleId, token);
        if (definition.Denial is { } denied) return WorkbenchDecision<WorkbenchFormAccess>.Deny(denied);
        var access = definition.Value!;
        var formEnabled = IsFormWritable(moduleId);
        // 浏览走写名单或只读名单；新增/修改/删除只认写名单（只读名单不开写路径）。
        if (mode == "view" ? !(formEnabled || IsFormReadOnly(moduleId)) : !formEnabled)
            return DenyForm(WorkbenchDenialCodes.FormNotEnabled);
        var rights = access.Rights;
        if (mode == "new" && !rights.CanAddNew) return DenyForm(WorkbenchDenialCodes.ModeNotPermitted);
        if (mode == "edit" && !rights.CanEdit) return DenyForm(WorkbenchDenialCodes.ModeNotPermitted);
        if (mode == "view" && !rights.CanBrowse) return DenyForm(WorkbenchDenialCodes.ModeNotPermitted);
        if (mode == "new" && !access.Definition.HasAdd) return DenyForm(WorkbenchDenialCodes.AddCapabilityMissing);
        if (mode == "edit" && !access.Definition.HasEdit) return DenyForm(WorkbenchDenialCodes.EditCapabilityMissing);
        var form = await BuildFormAsync(access.Definition, userId!, mode, rights, canSetup: false, token);
        if (form is null) return DenyForm(WorkbenchDenialCodes.FormUnavailable);
        return WorkbenchDecision<WorkbenchFormAccess>.Allow(
            new WorkbenchFormAccess(access.Definition, formEnabled ? form : WithoutWriteActions(form), rights));
    }

    /// <summary>
    /// 单据动作（自定义按钮）的入口闸门：权限口径与 <see cref="AuthorizeFormAsync"/> 相同，
    /// 但不要求模块在统一表单白名单内——按钮同样出现在主表模块的自定义承载页上。
    /// </summary>
    public async Task<WorkbenchDecision<WorkbenchFormAccess>> AuthorizeActionAsync(
        string? userId, int moduleId, CancellationToken token)
    {
        var definition = await AuthorizeDefinitionAsync(userId, moduleId, token);
        if (definition.Denial is { } denied) return WorkbenchDecision<WorkbenchFormAccess>.Deny(denied);
        var access = definition.Value!;
        if (!access.Definition.HasAdd && !access.Definition.HasEdit) return DenyForm(WorkbenchDenialCodes.NoWritableSurface);
        var form = await BuildFormAsync(access.Definition, userId!, "view", access.Rights, canSetup: false, token);
        if (form is null) return DenyForm(WorkbenchDenialCodes.FormUnavailable);
        return WorkbenchDecision<WorkbenchFormAccess>.Allow(new WorkbenchFormAccess(access.Definition, form, access.Rights));
    }

    /// <summary>删除路径：编辑访问可开后，再用删除动作位把关（不满足即抛异常 → 403）。</summary>
    public async Task<WorkbenchDecision<WorkbenchFormAccess>> AuthorizeDeleteAsync(
        string? userId, int moduleId, CancellationToken token)
    {
        var decision = await AuthorizeFormAsync(userId, moduleId, "edit", token);
        if (decision.Denial is not null) return decision;
        await RequireAsync(userId!, moduleId, PermissionAction.Delete, token);
        return decision;
    }

    /// <summary>
    /// 批核/解批/结案/取消结案的闸门：定义装配 + 可写页面入口，再用动作位把关。
    /// 动作位不满足时由 <see cref="RequireAsync"/> 抛出，调用方不捕获（与既有 403 口径一致）。
    /// </summary>
    public async Task<WorkbenchDecision<WorkbenchDefinitionAccess>> AuthorizeWorkflowAsync(
        string? userId, int moduleId, PermissionAction action, CancellationToken token)
    {
        var definition = await AuthorizeDefinitionAsync(userId, moduleId, token);
        if (definition.Denial is not null) return definition;
        if (!HasWritablePage(moduleId)) return DenyDefinition(WorkbenchDenialCodes.NoWritablePage);
        await RequireAsync(userId!, moduleId, action, token);
        return definition;
    }

    /// <summary>
    /// 该用户在本模块上有授权的单据动作名单的入口判定：没有统一表单、但仍有单据级动作的模块
    /// （自定义承载页）也走这条，因此不要求模块在统一表单白名单内。
    /// </summary>
    public async Task<WorkbenchDecision<WorkbenchDefinitionAccess>> AuthorizeUserActionsAsync(
        string? userId, int moduleId, CancellationToken token)
    {
        var definition = await AuthorizeDefinitionAsync(userId, moduleId, token);
        if (definition.Denial is not null) return definition;
        if (!definition.Value!.Definition.HasAdd && !definition.Value.Definition.HasEdit)
            return DenyDefinition(WorkbenchDenialCodes.NoWritableSurface);
        return definition;
    }

    /// <summary>
    /// 字段维护面（数据表/字段设置）的定义装配：需同时具备浏览位与维护位。
    /// 拒绝按 403 口径（与既有 field-settings 端点一致），不做防探测隐藏。
    /// </summary>
    public async Task<WorkbenchDecision<WorkbenchDefinitionAccess>> AuthorizeSetupAsync(
        string? userId, int moduleId, CancellationToken token)
    {
        if (userId is null) return DenySetup(WorkbenchDenialCodes.NotAuthenticated);
        var rights = (await permissions.GetAsync(userId, moduleId, token)).Rights;
        if (!(rights.CanBrowse && rights.CanSetup)) return DenySetup(WorkbenchDenialCodes.SetupNotAllowed);
        var definition = await definitions.GetDefinitionAsync(moduleId, userId, rights.ExecuteTag,
            rights.CanViewCost, rights.CanViewSecrecy, rights.DeniedMasterFields, rights.DeniedDetailFields, token);
        if (definition is null) return DenySetup(WorkbenchDenialCodes.DefinitionUnavailable);
        return WorkbenchDecision<WorkbenchDefinitionAccess>.Allow(new WorkbenchDefinitionAccess(definition, rights));
    }

    /// <summary>当前用户对该模块生效的数据范围（个人覆盖组、组 OR 已组合）。</summary>
    public async Task<string?> GetDataFilterAsync(string? userId, int moduleId, CancellationToken token)
    {
        if (userId is null) return null;
        return (await permissions.GetAsync(userId, moduleId, token)).Rights.DataFilter;
    }

    /// <summary>取某模块的权限（供需要按另一模块权限装配数据的调用方，例如选择器来源模块）。</summary>
    public async Task<ModuleRights?> GetRightsAsync(string? userId, int moduleId, CancellationToken token)
    {
        if (userId is null) return null;
        return (await permissions.GetAsync(userId, moduleId, token)).Rights;
    }

    /// <summary>
    /// 表单单据入口（form-definition）：与 <see cref="AuthorizeFormAsync"/> 共用同一套判据，
    /// 但表单模式非法返回 400，动作位不足返回 <b>403</b>（该端点不是防探测口径）。
    /// </summary>
    public async Task<WorkbenchDecision<WorkbenchFormAccess>> AuthorizeFormDefinitionAsync(
        string? userId, int moduleId, string mode, CancellationToken token)
    {
        var definition = await AuthorizeDefinitionAsync(userId, moduleId, token);
        if (definition.Denial is { } denied) return WorkbenchDecision<WorkbenchFormAccess>.Deny(denied);
        var access = definition.Value!;
        var formEnabled = IsFormWritable(moduleId);
        if (!formEnabled && !IsFormReadOnly(moduleId)) return DenyForm(WorkbenchDenialCodes.FormNotEnabled);
        var normalized = mode.Trim().ToLowerInvariant();
        if (normalized is not ("new" or "edit" or "view"))
            return new WorkbenchDecision<WorkbenchFormAccess>(default,
                new WorkbenchDenial(WorkbenchDenialKind.InvalidRequest, WorkbenchDenialCodes.InvalidFormMode, InvalidFormModeMessage));
        // 只读名单只放行浏览：新增/修改仍以写名单为唯一入口
        if (normalized != "view" && !formEnabled) return DenyForm(WorkbenchDenialCodes.FormNotEnabled);
        var rights = access.Rights;
        if (normalized == "new" && !rights.CanAddNew) return ForbidForm(WorkbenchDenialCodes.ModeNotPermitted);
        if (normalized == "edit" && !rights.CanEdit) return ForbidForm(WorkbenchDenialCodes.ModeNotPermitted);
        // view 模式仅需浏览权限
        if (normalized == "view" && !rights.CanBrowse) return ForbidForm(WorkbenchDenialCodes.ModeNotPermitted);
        if (normalized == "new" && !access.Definition.HasAdd) return DenyForm(WorkbenchDenialCodes.AddCapabilityMissing);
        if (normalized == "edit" && !access.Definition.HasEdit) return DenyForm(WorkbenchDenialCodes.EditCapabilityMissing);
        var canSetup = (await permissions.GetAsync(userId!, FieldAdminModuleId, token)).CanSetup;
        var form = await BuildFormAsync(access.Definition, userId!, normalized, rights, canSetup, token);
        if (form is null) return DenyForm(WorkbenchDenialCodes.FormUnavailable);
        return WorkbenchDecision<WorkbenchFormAccess>.Allow(
            new WorkbenchFormAccess(access.Definition, formEnabled ? form : WithoutWriteActions(form), rights));
    }

    /// <summary>动作位授权：原样走 <see cref="IPermissionService.RequireAsync"/>，不满足即抛。</summary>
    public Task RequireAsync(string userId, int moduleId, PermissionAction action, CancellationToken token)
        => permissions.RequireAsync(userId, moduleId, action, token);

    /// <summary>
    /// 能力位折叠：**新增/编辑只看统一表单名单**——写名单 = 可新增可编辑，只读名单 = 只能浏览。
    /// 迁移 321 删掉了 NEW_URL / MODI_URL，原先"自定义页路由也算有入口"那两条分支随之退场：
    /// 自定义承载页（M_URL 是精确路径）本就不装配工作台定义，动作入口由页面自己负责。
    /// </summary>
    private WorkbenchDefinition FoldRoutes(WorkbenchDefinition definition, int moduleId, ModuleRights rights)
    {
        var formEnabled = IsFormWritable(moduleId);
        return definition with
        {
            HasAdd = formEnabled,
            // HasEdit 表达"本模块有可打开的表单界面"（写名单可编辑、只读名单只浏览）；
            // 是否真能进编辑态由写名单与下发的表单定义分别把关。
            HasEdit = formEnabled || IsFormReadOnly(moduleId),
            CanDelete = rights.CanDelete,
        };
    }

    private async Task<FormDefinition?> BuildFormAsync(
        WorkbenchDefinition definition, string userId, string mode, ModuleRights rights, bool canSetup, CancellationToken token)
        => await definitions.GetFormDefinitionAsync(definition, userId, mode,
            rights.CanViewCost, rights.CanViewSecrecy,
            rights.DeniedMasterFields, rights.DeniedDetailFields,
            rights.DenyNewMasterFields, rights.DenyNewDetailFields,
            rights.DenyModiMasterFields, rights.DenyModiDetailFields, token,
            rights.CanAddNew, rights.CanEdit, rights.CanDelete, rights.CanApprove, rights.CanDeapprove, rights.CanEndCase, rights.CanUnEndCase,
            rights.CanFileView, rights.CanFileUpda, rights.CanFileEdit, rights.CanFileDele, canSetup);

    private static WorkbenchDecision<WorkbenchDefinitionAccess> DenyDefinition(string code)
        => WorkbenchDecision<WorkbenchDefinitionAccess>.Deny(new WorkbenchDenial(WorkbenchDenialKind.NotFound, code));

    private static WorkbenchDecision<WorkbenchDefinitionAccess> DenySetup(string code)
        => WorkbenchDecision<WorkbenchDefinitionAccess>.Deny(new WorkbenchDenial(WorkbenchDenialKind.Forbidden, code));

    private static WorkbenchDecision<WorkbenchFormAccess> DenyForm(string code)
        => WorkbenchDecision<WorkbenchFormAccess>.Deny(new WorkbenchDenial(WorkbenchDenialKind.NotFound, code));

    private static WorkbenchDecision<WorkbenchFormAccess> ForbidForm(string code)
        => WorkbenchDecision<WorkbenchFormAccess>.Deny(new WorkbenchDenial(WorkbenchDenialKind.Forbidden, code));

    /// <summary>
    /// 只读模块下发的表单：保留浏览与自定义按钮，关掉全部写动作。
    /// 只读模块的新增/修改/删除端点本就 404（写名单之外），写动作留着只会开出一次失败。
    /// </summary>
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
}
