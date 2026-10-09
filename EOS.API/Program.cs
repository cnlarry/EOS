using EOS.API.Data;
using EOS.API.Data.Effects;
using EOS.API.Services;
using EOS.API.Errors;
using EOS.API.Health;
using EOS.API.Middleware;
using EOS.API.Models;
using EOS.API.Security;
using EOS.API.Configuration;
using EOS.API.Telemetry;
using System.Reflection;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.OpenApi;
using QuestPDF;
using QuestPDF.Infrastructure;

// QuestPDF Community 许可（公司年收入 < $1M USD 免费；商用前需复核许可门槛）
QuestPDF.Settings.License = LicenseType.Community;

var builder = WebApplication.CreateBuilder(args);

// 配置值里的环境变量引用（${VAR}）在此统一解析：配置文件只写引用名（可入库），真值只存在于环境变量。
// 未定义的引用按空值处理并记入 referenceIssues，由启动后的 Warning 点名——不把 ${VAR} 字面量当值使用。
var referenceIssues = new List<string>();
builder.Configuration.AddInMemoryCollection(
    EnvironmentReferenceResolver.Resolve(builder.Configuration, referenceIssues));

builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(options =>
{
    options.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffzzz";
    options.UseUtcTimestamp = false;
    options.JsonWriterOptions = new System.Text.Json.JsonWriterOptions { Indented = false };
});
var logFilePath = ResolveLogFilePath(builder.Configuration);
var logFileProvider = new JsonFileLoggerProvider(
    logFilePath,
    maxBytes: builder.Configuration.GetValue<long?>("Logging:File:MaxBytes") ?? 50L * 1024 * 1024,
    maxFiles: builder.Configuration.GetValue<int?>("Logging:File:MaxFiles") ?? 3,
    minimumLevel: LogLevel.Warning,
    retentionDays: builder.Configuration.GetValue<int?>("Logging:File:RetentionDays")
        ?? JsonFileLoggerProvider.DefaultRetentionDays);
builder.Logging.AddProvider(logFileProvider);
if (builder.Environment.IsDevelopment())
{
    builder.Logging.AddDebug();
}

builder.Services.AddControllers(options =>
    {
        options.Filters.Add<ApiExceptionFilter>();
        options.Filters.Add<ProblemDetailsContextFilter>();
    })
    .ConfigureApiBehaviorOptions(options =>
    {
        options.InvalidModelStateResponseFactory = context =>
        {
            var fieldErrors = context.ModelState
                .Where(entry => entry.Value is { Errors.Count: > 0 })
                .ToDictionary(
                    entry => entry.Key,
                    entry => entry.Value!.Errors.Select(error => error.ErrorMessage).ToArray());
            var problem = ApiProblem.Create(
                StatusCodes.Status400BadRequest,
                ApiErrorCodes.InvalidModel,
                "请求参数无效",
                fieldErrors);
            RequestContext.SetErrorCode(context.HttpContext, ApiErrorCodes.InvalidModel);
            ApiProblem.AttachRequestContext(problem, context.HttpContext);
            return new BadRequestObjectResult(problem);
        };
    });
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddOpenApi(options =>
{
    options.AddDocumentTransformer((document, context, cancellationToken) =>
    {
        document.Info.Title = "EOS API";
        document.Info.Version = "v1";
        document.Info.Description =
            "EOS业务 API 契约：EOS.Web / Agent / 外部集成的统一受控入口。"
            + "所有业务接口默认要求登录会话（EOS.Auth Cookie），未登录返回 401，无权返回 403。";

        var cookieScheme = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.ApiKey,
            Name = "EOS.Auth",
            In = ParameterLocation.Cookie,
            Description = "登录会话 Cookie（EOS.Auth）。",
        };
        document.AddComponent("EOS.Auth", cookieScheme);
        document.Security =
        [
            new OpenApiSecurityRequirement
            {
                [new OpenApiSecuritySchemeReference("EOS.Auth", document, null)] = [],
            },
        ];
        return Task.CompletedTask;
    });
});
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<DbConnectionFactory>();
builder.Services.AddSingleton<ApiMetrics>();
builder.Services.AddSingleton(logFileProvider);
builder.Services.AddSingleton<EOS.API.Features.Diagnostics.LogFileReader>();
builder.Services.AddSingleton<DbTimingCollector>();
builder.Services.AddSingleton<WorkbenchDefinitionProvider>();
builder.Services.AddScoped<ApiExceptionFilter>();
builder.Services.AddScoped<ProblemDetailsContextFilter>();
builder.Services.Configure<LoginThrottleOptions>(builder.Configuration.GetSection("Security:LoginThrottle"));
builder.Services.AddSingleton<LoginThrottleService>();
builder.Services.AddDataProtection()
    .SetApplicationName(builder.Configuration["DataProtection:ApplicationName"] ?? "EOS.API")
    .PersistKeysToFileSystem(GetDataProtectionKeysDirectory(builder.Configuration));
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "EOS.Auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = builder.Environment.IsProduction()
            ? CookieSecurePolicy.Always
            : CookieSecurePolicy.SameAsRequest;
        options.SlidingExpiration = true;
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.Events.OnRedirectToLogin = context =>
        {
            if (context.Request.Path.StartsWithSegments("/api"))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return Task.CompletedTask;
            }
            context.Response.Redirect("/login.html");
            return Task.CompletedTask;
        };
        options.Events.OnRedirectToAccessDenied = context =>
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        };
    });
builder.Services.AddAuthorization(options => options.FallbackPolicy =
    new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser().Build());
builder.Services.AddHealthChecks()
    .AddCheck<ErpDatabaseHealthCheck>("erp_database", tags: ["ready"])
    .AddCheck<MigrationsHealthCheck>("erp_migrations", tags: ["ready", "startup"])
    .AddCheck<AttachmentStorageHealthCheck>("attachment_storage", tags: ["ready"])
    .AddCheck<LogFileHealthCheck>("log_file", tags: ["ready"])
    .AddCheck<ConfigurationHealthCheck>("configuration", tags: ["ready"]);
builder.Services.AddScoped<AuthenticationRepository>();
builder.Services.AddScoped<DepotStockPolicyService>();
builder.Services.AddScoped<UserAdminRepository>();
builder.Services.AddScoped<FieldAdminRepository>();
builder.Services.AddScoped<EOS.API.Features.BusinessFlow.BusinessFlowRepository>();
builder.Services.AddScoped<RestrictedExpressionService>();
builder.Services.AddScoped<RightsAdminRepository>();
builder.Services.AddScoped<ModuleRightsRepository>();
builder.Services.AddScoped<IPermissionService, PermissionService>();
builder.Services.AddSingleton<PermissionCache>();
builder.Services.AddScoped<NavigationRepository>();
builder.Services.AddScoped<NavigationGroupsRepository>();
builder.Services.AddScoped<ModuleGroupAdminRepository>();
builder.Services.AddScoped<MenuAdminRepository>();
builder.Services.AddScoped<ModuleBusinessConfigRepository>();
builder.Services.AddScoped<EffectPlanLoader>();
builder.Services.AddScoped<EffectFormulaExecutor>();
builder.Services.AddScoped<EffectValidationExecutor>();
builder.Services.AddScoped<EffectPhysicalColumns>();
builder.Services.AddScoped<EOS.API.Data.Effects.IEffectServiceHandler, EOS.API.Data.Effects.ServiceEffectHandlers.InventoryMoveHandler>();
builder.Services.AddScoped<EOS.API.Data.Effects.IEffectServiceHandler, EOS.API.Data.Effects.ServiceEffectHandlers.LocationPathRecalcHandler>();
builder.Services.AddScoped<EOS.API.Data.Effects.IEffectServiceHandler, EOS.API.Data.Effects.ServiceEffectHandlers.DepotSentinelLocationHandler>();
builder.Services.AddScoped<EOS.API.Data.Effects.IEffectServiceHandler, EOS.API.Data.Effects.ServiceEffectHandlers.SetStateHandler>();
builder.Services.AddScoped<EOS.API.Data.Effects.IEffectServiceHandler, EOS.API.Data.Effects.ServiceEffectHandlers.StampLastActivityHandler>();
builder.Services.AddScoped<EOS.API.Data.Effects.IEffectServiceHandler, EOS.API.Data.Effects.ServiceEffectHandlers.BalanceAdjustHandler>();
builder.Services.AddScoped<EOS.API.Data.Effects.IEffectServiceHandler, EOS.API.Data.Effects.ServiceEffectHandlers.LinkStampHandler>();
builder.Services.AddScoped<EOS.API.Data.Effects.IEffectServiceHandler, EOS.API.Data.Effects.ServiceEffectHandlers.FieldCopyHandler>();
builder.Services.AddScoped<EOS.API.Data.Effects.IEffectServiceHandler, EOS.API.Data.Effects.ServiceEffectHandlers.CallbackRepriceHandler>();
builder.Services.AddScoped<EOS.API.Data.Effects.IEffectServiceHandler, EOS.API.Data.Effects.ServiceEffectHandlers.ClientPriceSyncHandler>();
builder.Services.AddScoped<EOS.API.Data.Effects.IEffectServiceHandler, EOS.API.Data.Effects.ServiceEffectHandlers.SupplierPriceSyncHandler>();
builder.Services.AddScoped<EOS.API.Data.Effects.IEffectServiceHandler, EOS.API.Data.Effects.ServiceEffectHandlers.QuoteParameterRecalcHandler>();
builder.Services.AddScoped<EOS.API.Data.Effects.IEffectServiceHandler, EOS.API.Data.Effects.ServiceEffectHandlers.OrderChangeApplyHandler>();
builder.Services.AddScoped<EOS.API.Data.Effects.IEffectServiceHandler, EOS.API.Data.Effects.ServiceEffectHandlers.PurchaseChangeApplyHandler>();
builder.Services.AddScoped<EOS.API.Data.Effects.IEffectServiceHandler, EOS.API.Data.Effects.ServiceEffectHandlers.ProduceChangeApplyHandler>();
builder.Services.AddScoped<EOS.API.Data.Effects.IEffectServiceHandler, EOS.API.Data.Effects.ServiceEffectHandlers.PaymentDateCalcHandler>();
builder.Services.AddScoped<EOS.API.Data.Effects.IEffectServiceHandler, EOS.API.Data.Effects.ServiceEffectHandlers.MrpPlanAllocHandler>();
builder.Services.AddScoped<EOS.API.Data.Effects.IEffectServiceHandler, EOS.API.Data.Effects.ServiceEffectHandlers.ContractSyncHandler>();
builder.Services.AddScoped<EOS.API.Data.Effects.IEffectServiceHandler, EOS.API.Data.Effects.ServiceEffectHandlers.HrUsageSyncHandler>();
builder.Services.AddScoped<EOS.API.Data.Effects.IEffectServiceHandler, EOS.API.Data.Effects.ServiceEffectHandlers.MouldBatchApplyHandler>();
builder.Services.AddScoped<EOS.API.Data.Effects.IEffectServiceHandler, EOS.API.Data.Effects.ServiceEffectHandlers.DimissionSyncHandler>();
builder.Services.AddScoped<EOS.API.Data.Effects.IEffectServiceHandler, EOS.API.Data.Effects.ServiceEffectHandlers.HalfStockMoveHandler>();
builder.Services.AddScoped<EOS.API.Data.Effects.IEffectServiceHandler, EOS.API.Data.Effects.ServiceEffectHandlers.CarFilloilSyncHandler>();
builder.Services.AddScoped<EOS.API.Data.Effects.IEffectServiceHandler, EOS.API.Data.Effects.ServiceEffectHandlers.DetailFieldSyncHandler>();
builder.Services.AddScoped<EOS.API.Data.Effects.IEffectServiceHandler, EOS.API.Data.Effects.ServiceEffectHandlers.SampleEditionBumpHandler>();
builder.Services.AddScoped<EOS.API.Data.Effects.IEffectServiceHandler, EOS.API.Data.Effects.ServiceEffectHandlers.MouldIdsSyncHandler>();
builder.Services.AddScoped<EOS.API.Data.Effects.IEffectServiceHandler, EOS.API.Data.Effects.ServiceEffectHandlers.CardSiblingCloseHandler>();
builder.Services.AddScoped<EOS.API.Data.Effects.IEffectServiceHandler, EOS.API.Data.Effects.ServiceEffectHandlers.FieldsMetadataSyncHandler>();
builder.Services.AddScoped<EOS.API.Data.Effects.IEffectServiceHandler, EOS.API.Data.Effects.ServiceEffectHandlers.DetailFlagAndRollupHandler>();
builder.Services.AddScoped<EOS.API.Data.Effects.IEffectServiceHandler, EOS.API.Data.Effects.ServiceEffectHandlers.WageMonthDocPruneHandler>();
builder.Services.AddScoped<EOS.API.Data.Effects.IEffectServiceHandler, EOS.API.Data.Effects.ServiceEffectHandlers.DocOrphanPruneHandler>();
builder.Services.AddScoped<EOS.API.Data.Effects.IEffectServiceHandler, EOS.API.Data.Effects.ServiceEffectHandlers.SfcPlanSyncHandler>();
builder.Services.AddScoped<EOS.API.Data.Effects.IEffectServiceHandler, EOS.API.Data.Effects.ServiceEffectHandlers.CusAccountSyncHandler>();
builder.Services.AddScoped<EOS.API.Data.Effects.IEffectServiceHandler, EOS.API.Data.Effects.ServiceEffectHandlers.PurApplySyncHandler>();
builder.Services.AddScoped<EOS.API.Data.Effects.IEffectServiceHandler, EOS.API.Data.Effects.ServiceEffectHandlers.BomSizeBackfillHandler>();
builder.Services.AddScoped<EOS.API.Data.Effects.IEffectServiceHandler, EOS.API.Data.Effects.ServiceEffectHandlers.DetailRollupHandler>();
builder.Services.AddScoped<EOS.API.Data.Effects.IEffectServiceHandler, EOS.API.Data.Effects.ServiceEffectHandlers.PurPayOffsetHandler>();
builder.Services.AddScoped<EOS.API.Data.Effects.IEffectServiceHandler, EOS.API.Data.Effects.ServiceEffectHandlers.CopReceiptOffsetHandler>();
builder.Services.AddScoped<EOS.API.Data.Effects.IEffectServiceHandler, EOS.API.Data.Effects.ServiceEffectHandlers.CopSendMoFlagHandler>();
builder.Services.AddScoped<EOS.API.Data.Effects.IEffectServiceHandler, EOS.API.Data.Effects.ServiceEffectHandlers.PurPurchaseSyncHandler>();
builder.Services.AddScoped<EOS.API.Data.Effects.IEffectServiceHandler, EOS.API.Data.Effects.ServiceEffectHandlers.StocktakeScopeGenerateHandler>();
builder.Services.AddSingleton(builder.Configuration.GetSection("EffectEngine").Get<EffectEngineSettings>() ?? new EffectEngineSettings());
builder.Services.AddScoped<EffectEngineInvoker>();
builder.Services.AddScoped<EffectPipeline>();
// 单据操作（自定义按钮）：键集来自这里的处理器注册，发布校验与端点执行读的是同一份。
builder.Services.AddScoped<EOS.API.Data.DocumentActions.IDocumentUserAction, EOS.API.Data.DocumentActions.Handlers.DocumentActionProbeHandler>();
builder.Services.AddScoped<EOS.API.Data.DocumentActions.IDocumentUserAction, EOS.API.Data.DocumentActions.Handlers.RecalcAccountHandler>();
builder.Services.AddScoped<EOS.API.Data.DocumentActions.IDocumentUserAction, EOS.API.Data.DocumentActions.Handlers.GenerateAdjustmentHandler>();
builder.Services.AddScoped<EOS.API.Data.DocumentActions.IDocumentUserAction, EOS.API.Data.DocumentActions.Handlers.RestockScopeHandler>();
builder.Services.AddScoped<EOS.API.Data.DocumentActions.IDocumentUserAction, EOS.API.Data.DocumentActions.Handlers.PurchaseRepriceHandler>();
builder.Services.AddScoped<EOS.API.Data.DocumentActions.IDocumentUserAction, EOS.API.Data.DocumentActions.Handlers.MaterialIssueAllocateHandler>();
builder.Services.AddScoped<EOS.API.Data.DocumentActions.IDocumentUserAction, EOS.API.Data.DocumentActions.Handlers.ProduceCalcMaterialsHandler>();
builder.Services.AddScoped<EOS.API.Data.DocumentActions.IDocumentUserAction, EOS.API.Data.DocumentActions.Handlers.ProduceGenSubHandler>();
builder.Services.AddScoped<EOS.API.Data.DocumentActions.IDocumentUserAction, EOS.API.Data.DocumentActions.Handlers.MonthCloseSnapshotHandler>();
builder.Services.AddScoped<EOS.API.Data.DocumentActions.IDocumentUserAction, EOS.API.Data.DocumentActions.Handlers.MasterFieldWriteHandler>();
builder.Services.AddScoped<EOS.API.Data.DocumentActions.IDocumentUserAction, EOS.API.Data.DocumentActions.Handlers.InventoryFreezeHandler>();
builder.Services.AddScoped<EOS.API.Data.DocumentActions.IDocumentUserAction, EOS.API.Data.DocumentActions.Handlers.InventoryUnfreezeHandler>();
builder.Services.AddScoped<EOS.API.Data.DocumentActions.IDocumentUserAction, EOS.API.Data.DocumentActions.Handlers.InventoryReserveHandler>();
builder.Services.AddScoped<EOS.API.Data.DocumentActions.IDocumentUserAction, EOS.API.Data.DocumentActions.Handlers.InventoryReleaseHandler>();
builder.Services.AddScoped<EOS.API.Data.Effects.IEffectServiceHandler, EOS.API.Data.Effects.ServiceEffectHandlers.InventoryReleaseBySourceHandler>();
builder.Services.AddScoped<EOS.API.Features.Inventory.MonthCloseSnapshotService>();
builder.Services.AddScoped<EOS.API.Data.DocumentActions.DocumentActionRegistry>();
builder.Services.AddScoped<EOS.API.Data.DocumentActions.DocumentActionAuthorization>();
builder.Services.AddScoped<EOS.API.Data.DocumentActions.DocumentActionExecutor>();
builder.Services.AddScoped<ChooserRepository>();
builder.Services.AddScoped<WorkflowEngine>();
builder.Services.AddScoped<FlowDefinitionService>();
builder.Services.Configure<WorkflowSettings>(builder.Configuration.GetSection("Workflow"));
builder.Services.AddScoped<WorkbenchScopeFilter>();
builder.Services.AddScoped<WorkbenchChooserService>();
builder.Services.AddScoped<WorkbenchDirtyMarker>();
builder.Services.AddScoped<WorkbenchDefinitionValidator>();
builder.Services.AddScoped<WorkbenchDefinitionSnapshotService>();
builder.Services.AddScoped<WorkbenchAuditWriter>();
builder.Services.AddScoped<WorkbenchIdempotency>();
builder.Services.AddScoped<EOS.API.Data.Forms.FormLayoutRepository>();
builder.Services.AddScoped<WorkbenchVirtualColumnResolver>();
builder.Services.AddScoped<WorkbenchApprovalService>();
builder.Services.AddScoped<EOS.API.Data.Effects.EffectSimulationService>();
builder.Services.AddScoped<WorkbenchQueryComposer>();
builder.Services.AddScoped<WorkbenchCommandHandler>();
builder.Services.AddScoped<AttendanceCalcService>();
builder.Services.AddScoped<HumanResourceJobsService>();
builder.Services.AddScoped<WorkbenchDefinitionBuilder>();
builder.Services.AddScoped<WorkbenchFieldMetaMapper>();
builder.Services.AddScoped<DocumentWorkbenchRepository>();
builder.Services.AddScoped<EOS.API.Data.Workbench.IWorkbenchDefinitionSource>(serviceProvider => serviceProvider.GetRequiredService<DocumentWorkbenchRepository>());
builder.Services.AddScoped<EOS.API.Data.Workbench.WorkbenchAccessPolicy>();
builder.Services.AddScoped<EOS.API.Data.Workbench.AgentWriteContext>();
builder.Services.AddScoped<ReportRepository>();
builder.Services.AddScoped<PrintSettingsRepository>();
builder.Services.AddScoped<SystemParameterService>();
builder.Services.AddScoped<ReportAdminRepository>();
builder.Services.AddScoped<ReportInboxRepository>();
builder.Services.AddScoped<SearchCenterRepository>();
builder.Services.AddScoped<ImportService>();
builder.Services.AddScoped<PrintService>();
builder.Services.AddScoped<ReportFormatRepository>();
builder.Services.AddScoped<ReportFormatValidator>();
builder.Services.AddScoped<ReportFormLayoutRepository>();
builder.Services.AddScoped<ILayoutRenderer, QuestPdfLayoutRenderer>();
builder.Services.AddScoped<CurrentUserContext>();
builder.Services.AddScoped<AttachmentRepository>();
builder.Services.AddHttpClient("AssistantModel");
// 助手配置**一律来自数据库**（ADR-030 §8）：模型与供应商读 dbo.ASSISTANT_MODEL / ASSISTANT_PROVIDER
// （菜单组 31 / 3102），全局策略读 dbo.SYSSS 的 OWNER_MODULE = 3105（3105 助手设置）。
// appsettings 里**没有** Assistant 段——它是运行期要能改的业务配置，不是部署参数，
// 混在配置清单里会让"改个模型名要改文件加重启"。密钥仍只走环境变量，库里存的是变量名（§3）。
// 参数目录（AssistantParameterCatalog）是代码里的唯一真源，解析器读库产出策略对象；
// 目录 / 密钥存取 / 参数解析 / 运行期快照都是单例；真正被消费的 IChatModel 是 **Scoped**：
// 一次请求内配置固定（一条回答不会跨两个模型），跨请求读得到新快照（改配置与切模型都不必重启）。
builder.Services.AddSingleton<EOS.API.Data.Assistant.IAssistantModelCatalog, EOS.API.Data.Assistant.AssistantModelCatalog>();
// 型号拉取：向厂商要可用模型清单（型号不硬编码，ADR-030 §12.3），密钥只从既有密钥存储读
builder.Services.AddScoped<EOS.API.Features.Assistant.ModelAccess.IAssistantModelDiscovery,
    EOS.API.Features.Assistant.ModelAccess.AssistantModelDiscovery>();
builder.Services.AddSingleton<EOS.API.Features.Assistant.Parameters.AssistantParameterResolver>();
// 作用域覆盖：读写在请求作用域内（要写审计），生效参数按当事人叠加，故两者都是 Scoped
builder.Services.AddScoped<EOS.API.Features.Assistant.Parameters.AssistantParameterScopeStore>();
builder.Services.AddScoped<EOS.API.Features.Assistant.Parameters.IAssistantEffectiveParameters,
    EOS.API.Features.Assistant.Parameters.AssistantEffectiveParameters>();
builder.Services.AddSingleton<EOS.API.Features.Assistant.ModelAccess.IAssistantSecretStore,
    EOS.API.Features.Assistant.ModelAccess.EnvironmentSecretStore>();
builder.Services.AddSingleton<EOS.API.Features.Assistant.ModelAccess.AssistantRuntimeRegistry>();
builder.Services.AddSingleton<EOS.API.Features.Assistant.ModelAccess.IAssistantRuntimeConfig>(sp =>
    sp.GetRequiredService<EOS.API.Features.Assistant.ModelAccess.AssistantRuntimeRegistry>());
builder.Services.AddScoped<EOS.API.Features.Assistant.ModelAccess.IChatModel,
    EOS.API.Features.Assistant.ModelAccess.ResolvingChatModel>();
builder.Services.AddScoped<EOS.API.Data.IAssistantRepository, EOS.API.Data.AssistantRepository>();
// 管理侧会话仓储（跨用户，菜单组 31 / 3101，见 ADR-030）：与个人侧并存，语义互不影响
builder.Services.AddScoped<EOS.API.Data.Assistant.IAssistantAdminRepository, EOS.API.Data.Assistant.AssistantAdminRepository>();
builder.Services.AddScoped<EOS.API.Features.Assistant.Tools.IWorkbenchSearchGateway>(sp =>
    sp.GetRequiredService<EOS.API.Data.DocumentWorkbenchRepository>());
builder.Services.AddScoped<EOS.API.Features.Assistant.Tools.SearchRecordsTool>();
builder.Services.AddScoped<EOS.API.Features.Assistant.Tools.GetRecordDetailTool>();
builder.Services.AddScoped<EOS.API.Features.Assistant.Tools.GetFormSchemaTool>();
builder.Services.AddScoped<EOS.API.Features.Assistant.Tools.DraftRecordTool>();
builder.Services.AddScoped<EOS.API.Features.Assistant.Tools.EnumMetricsTool>();
builder.Services.AddScoped<EOS.API.Features.Assistant.Tools.ListModulesTool>();
builder.Services.AddScoped<EOS.API.Features.Assistant.Tools.DescribeModuleTool>();
builder.Services.AddScoped<EOS.API.Features.Assistant.Tools.AssistantSchemaGateway>();
builder.Services.AddScoped<EOS.API.Features.Assistant.Tools.IAssistantSchemaGateway>(sp =>
    sp.GetRequiredService<EOS.API.Features.Assistant.Tools.AssistantSchemaGateway>());
builder.Services.AddScoped<EOS.API.Features.Assistant.Tools.ListTablesTool>();
builder.Services.AddScoped<EOS.API.Features.Assistant.Tools.DescribeTableTool>();
builder.Services.AddScoped<EOS.API.Features.Assistant.Tools.ListViewsTool>();
builder.Services.AddScoped<EOS.API.Features.Assistant.Tools.ListProceduresTool>();
builder.Services.AddScoped<EOS.API.Features.Assistant.Tools.ListMyCapabilitiesTool>();
builder.Services.AddScoped<EOS.API.Features.Assistant.Memory.IAssistantMemoryStore,
    EOS.API.Features.Assistant.Memory.AssistantMemoryStore>();
builder.Services.AddScoped<EOS.API.Features.Assistant.Tools.GetMyDigestTool>();
builder.Services.AddScoped<EOS.API.Features.Assistant.Situation.AssistantSituationBudget>();
builder.Services.AddScoped<EOS.API.Features.Assistant.Situation.ISituationFactsReader,
    EOS.API.Features.Assistant.Situation.SituationFactsReader>();
builder.Services.AddScoped<EOS.API.Features.Assistant.Situation.AssistantSituationService>();
builder.Services.AddScoped<EOS.API.Features.Assistant.Situation.SituationContextSanitizer>();
builder.Services.AddScoped<EOS.API.Features.Assistant.Situation.SituationDigestService>();
builder.Services.AddScoped<EOS.API.Features.Assistant.Diagnosis.RecordDiagnosisReader>();
builder.Services.AddScoped<EOS.API.Features.Assistant.Diagnosis.IRecordDiagnosisReader>(sp =>
    sp.GetRequiredService<EOS.API.Features.Assistant.Diagnosis.RecordDiagnosisReader>());
builder.Services.AddScoped<EOS.API.Features.Assistant.Diagnosis.IBlockedRecordProbe>(sp =>
    sp.GetRequiredService<EOS.API.Features.Assistant.Diagnosis.RecordDiagnosisReader>());
builder.Services.AddScoped<EOS.API.Features.Assistant.Diagnosis.RecordDiagnosisService>();
builder.Services.AddScoped<EOS.API.Features.Assistant.Tools.DiagnoseRecordTool>();
builder.Services.AddScoped<EOS.API.Features.Assistant.Actions.AssistantActionGate>();
builder.Services.AddScoped<EOS.API.Features.Assistant.Actions.AssistantRecordActionService>();
// **不 Bind 配置节**（ADR-030 §8）：阈值住在 3105 助手设置（dbo.SYSSS 的 OWNER_MODULE = 3105），
// 配置节里已经没有读取方。留一条 Bind 会造出第二个"看起来配上了、其实没人读"的来源——
// 参数框架要根除的正是这个形态。这条管线现在只承担一件事：启动时跑红线守卫（ValidateOnStart）。
// 选项对象的属性初始值就是代码默认值（引用 AssistantActionLimits 的常量），校验器校验的是它们。
builder.Services.AddOptions<EOS.API.Features.Assistant.Governance.AssistantActionLimitsOptions>()
    .ValidateOnStart();
builder.Services.AddSingleton<Microsoft.Extensions.Options.IValidateOptions<EOS.API.Features.Assistant.Governance.AssistantActionLimitsOptions>,
    EOS.API.Features.Assistant.Governance.AssistantActionLimitsValidator>();
// ↓ 下面四个域改用**运行期参数视图**：取值来自 dbo.SYSSS 的 3105，不再从配置节绑定（ADR-030 §8）。
// 注意注册顺序：这几行在 AddOptions 之后，容器对 IOptions<T> 取**最后一个**注册，
// 于是 AddOptions 那条管线只承担一件事——启动时跑 AssistantActionLimitsValidator（红线守卫）。
builder.Services.AddSingleton<Microsoft.Extensions.Options.IOptions<
        EOS.API.Features.Assistant.Situation.AssistantSituationBudgetOptions>>(sp =>
    new EOS.API.Features.Assistant.Parameters.RuntimeParameterView<
        EOS.API.Features.Assistant.Situation.AssistantSituationBudgetOptions>(
        sp.GetRequiredService<EOS.API.Features.Assistant.ModelAccess.IAssistantRuntimeConfig>(),
        policy => policy.Situation));
builder.Services.AddSingleton<Microsoft.Extensions.Options.IOptions<
        EOS.API.Features.Assistant.Diagnosis.AssistantDiagnosisOptions>>(sp =>
    new EOS.API.Features.Assistant.Parameters.RuntimeParameterView<
        EOS.API.Features.Assistant.Diagnosis.AssistantDiagnosisOptions>(
        sp.GetRequiredService<EOS.API.Features.Assistant.ModelAccess.IAssistantRuntimeConfig>(),
        policy => policy.Diagnosis));
builder.Services.AddSingleton<Microsoft.Extensions.Options.IOptions<
        EOS.API.Features.Assistant.Governance.AssistantActionLimitsOptions>>(sp =>
    new EOS.API.Features.Assistant.Parameters.RuntimeParameterView<
        EOS.API.Features.Assistant.Governance.AssistantActionLimitsOptions>(
        sp.GetRequiredService<EOS.API.Features.Assistant.ModelAccess.IAssistantRuntimeConfig>(),
        policy => policy.ActionLimits));
builder.Services.AddSingleton<Microsoft.Extensions.Options.IOptions<
        EOS.API.Features.Assistant.Config.AssistantConfigWriteOptions>>(sp =>
    new EOS.API.Features.Assistant.Parameters.RuntimeParameterView<
        EOS.API.Features.Assistant.Config.AssistantConfigWriteOptions>(
        sp.GetRequiredService<EOS.API.Features.Assistant.ModelAccess.IAssistantRuntimeConfig>(),
        policy => policy.ConfigWrite));
builder.Services.AddScoped<EOS.API.Features.Assistant.Actions.AssistantApprovalRequestService>();
builder.Services.AddScoped<EOS.API.Features.Assistant.Tools.PreviewRecordActionTool>();
builder.Services.AddScoped<EOS.API.Features.Assistant.Tools.ApplyRecordActionTool>();
builder.Services.AddScoped<EOS.API.Features.Assistant.Tools.PreviewBatchDecisionTool>();
builder.Services.AddScoped<EOS.API.Features.Assistant.Config.IConfigDiagnosisReader,
    EOS.API.Features.Assistant.Config.ConfigDiagnosisReader>();
builder.Services.AddScoped<EOS.API.Features.Assistant.Config.ConfigClonePlanner>();
builder.Services.AddScoped<EOS.API.Features.Assistant.Config.ConfigDryRunner>();
builder.Services.AddScoped<EOS.API.Features.Assistant.Config.ConfigWriteService>();
builder.Services.AddScoped<EOS.API.Features.Assistant.Tools.CloneModuleConfigTool>();
builder.Services.AddScoped<EOS.API.Features.Assistant.Tools.PreviewConfigChangeTool>();
builder.Services.AddScoped<EOS.API.Features.Assistant.Tools.ApplyConfigChangeTool>();
builder.Services.AddScoped<EOS.API.Features.Assistant.Config.ConfigDiagnosisService>();
builder.Services.AddScoped<EOS.API.Features.Assistant.Catalog.SystemCapabilityCatalog>();
builder.Services.AddScoped<EOS.API.Features.Assistant.Catalog.DescribeMechanismTool>();
builder.Services.AddScoped<EOS.API.Data.IKnowledgeRepository, EOS.API.Data.KnowledgeRepository>();
// 嵌入模型：**当前该用哪个**由解析器按库里的"嵌入当前模型"决定（ADR-031 §3.1）。
// 没配时它抛 EmbeddingNotConfiguredException（fail-closed），不退化成空向量、也不说"没有相关内容"
builder.Services.AddScoped<EOS.API.Features.Assistant.ModelAccess.IAssistantEmbeddingResolver,
    EOS.API.Features.Assistant.ModelAccess.AssistantEmbeddingResolver>();
builder.Services.AddScoped<EOS.API.Features.Assistant.Tools.KbSearchTool>();
builder.Services.AddScoped<EOS.API.Features.Assistant.Tools.ModuleFlowGateway>();
builder.Services.AddScoped<EOS.API.Features.Assistant.Tools.IModuleFlowGateway>(sp =>
    sp.GetRequiredService<EOS.API.Features.Assistant.Tools.ModuleFlowGateway>());
builder.Services.AddScoped<EOS.API.Features.Assistant.Tools.GetModuleFlowTool>();
builder.Services.AddScoped<EOS.API.Features.Assistant.Tools.IModulePlanCatalog,
    EOS.API.Features.Assistant.Tools.ModulePlanCatalog>();
builder.Services.AddScoped<EOS.API.Features.Assistant.Tools.DiagnoseModuleTool>();
builder.Services.AddScoped<EOS.API.Features.Assistant.Admin.IChangeSetWriter,
    EOS.API.Features.Assistant.Admin.FieldMetaWriter>();
builder.Services.AddScoped<EOS.API.Features.Assistant.Admin.ChangeSetService>();
builder.Services.AddScoped<EOS.API.Features.Assistant.Tools.ApplyChangeSetTool>();
builder.Services.AddScoped<EOS.API.Features.Assistant.Metrics.IMetricSchemaProbe,
    EOS.API.Features.Assistant.Metrics.SysMetricSchemaProbe>();
builder.Services.AddScoped<EOS.API.Features.Assistant.Metrics.MetricDefinitionValidator>();
builder.Services.AddScoped<EOS.API.Features.Assistant.Metrics.IMetricRepository,
    EOS.API.Features.Assistant.Metrics.MetricRepository>();
builder.Services.AddScoped<EOS.API.Features.Assistant.Metrics.IMetricExecutor,
    EOS.API.Features.Assistant.Metrics.MetricExecutor>();
builder.Services.AddScoped<EOS.API.Features.Assistant.Tools.ResolveMetricTool>();
// 报表只读：列清单复用打印设置的可见性口径（REPORT_TAG），取数复用报表仓储的既有链路
builder.Services.AddScoped<EOS.API.Features.Assistant.Tools.IReportGateway,
    EOS.API.Features.Assistant.Tools.ReportGateway>();
builder.Services.AddScoped<EOS.API.Features.Assistant.Tools.ListReportsTool>();
builder.Services.AddScoped<EOS.API.Features.Assistant.Tools.RunReportTool>();
// 单据历史：定位口径共用 AssistantRecordLocator；审批历史转发工作流引擎，操作历史查审计摘要
builder.Services.AddScoped<EOS.API.Features.Assistant.Tools.AssistantRecordLocator>();
builder.Services.AddScoped<EOS.API.Features.Assistant.Tools.IRecordHistoryGateway,
    EOS.API.Features.Assistant.Tools.WorkflowRecordHistoryGateway>();
builder.Services.AddScoped<EOS.API.Features.Assistant.Tools.RecordHistoryTool>();
// 附件清单：与附件端点同一道权限位（CanBrowse + FILE_VIEW），只取元数据不碰文件二进制
builder.Services.AddScoped<EOS.API.Features.Assistant.Tools.IAttachmentGateway,
    EOS.API.Features.Assistant.Tools.AttachmentGateway>();
builder.Services.AddScoped<EOS.API.Features.Assistant.Tools.AttachmentListTool>();
builder.Services.AddScoped<EOS.API.Features.Assistant.Metrics.IFieldRelationRepository,
    EOS.API.Features.Assistant.Metrics.FieldRelationRepository>();
builder.Services.AddScoped<EOS.API.Features.Assistant.Tools.GetFieldRelationsTool>();
builder.Services.AddScoped<EOS.API.Data.IAssistantUsageRepository, EOS.API.Data.AssistantUsageRepository>();
// 熔断阈值同样来自设置表，所以**按需读取**而不是构造期固定：管理员在 3105 改了阈值应当立即生效，
// 否则"配了却要等重启"又会变成一处说不清的坑（这个单例的存活期与进程一样长）
builder.Services.AddSingleton(sp => new EOS.API.Features.Assistant.Governance.FailureBreaker(
    () => DateTimeOffset.UtcNow,
    () => sp.GetRequiredService<EOS.API.Features.Assistant.ModelAccess.IAssistantRuntimeConfig>()
        .Current.Settings.Cost));
builder.Services.AddScoped<EOS.API.Features.Assistant.Tools.AssistantToolRegistry>(sp =>
    new EOS.API.Features.Assistant.Tools.AssistantToolRegistry(
    [
        sp.GetRequiredService<EOS.API.Features.Assistant.Tools.SearchRecordsTool>(),
        sp.GetRequiredService<EOS.API.Features.Assistant.Tools.GetRecordDetailTool>(),
        sp.GetRequiredService<EOS.API.Features.Assistant.Tools.GetFormSchemaTool>(),
         sp.GetRequiredService<EOS.API.Features.Assistant.Tools.DraftRecordTool>(),
         sp.GetRequiredService<EOS.API.Features.Assistant.Tools.EnumMetricsTool>(),
         sp.GetRequiredService<EOS.API.Features.Assistant.Tools.ResolveMetricTool>(),
         sp.GetRequiredService<EOS.API.Features.Assistant.Tools.GetFieldRelationsTool>(),
         sp.GetRequiredService<EOS.API.Features.Assistant.Tools.ListModulesTool>(),
         sp.GetRequiredService<EOS.API.Features.Assistant.Tools.DescribeModuleTool>(),
         sp.GetRequiredService<EOS.API.Features.Assistant.Tools.ListTablesTool>(),
         sp.GetRequiredService<EOS.API.Features.Assistant.Tools.DescribeTableTool>(),
         sp.GetRequiredService<EOS.API.Features.Assistant.Tools.ListViewsTool>(),
         sp.GetRequiredService<EOS.API.Features.Assistant.Tools.ListProceduresTool>(),
         sp.GetRequiredService<EOS.API.Features.Assistant.Tools.ListMyCapabilitiesTool>(),
         sp.GetRequiredService<EOS.API.Features.Assistant.Tools.GetMyDigestTool>(),
         sp.GetRequiredService<EOS.API.Features.Assistant.Tools.KbSearchTool>(),
         sp.GetRequiredService<EOS.API.Features.Assistant.Tools.GetModuleFlowTool>(),
         sp.GetRequiredService<EOS.API.Features.Assistant.Tools.DiagnoseModuleTool>(),
         sp.GetRequiredService<EOS.API.Features.Assistant.Tools.ApplyChangeSetTool>(),
         sp.GetRequiredService<EOS.API.Features.Assistant.Tools.DiagnoseRecordTool>(),
        sp.GetRequiredService<EOS.API.Features.Assistant.Tools.PreviewRecordActionTool>(),
        sp.GetRequiredService<EOS.API.Features.Assistant.Tools.ApplyRecordActionTool>(),
        sp.GetRequiredService<EOS.API.Features.Assistant.Tools.CloneModuleConfigTool>(),
        sp.GetRequiredService<EOS.API.Features.Assistant.Tools.PreviewConfigChangeTool>(),
        sp.GetRequiredService<EOS.API.Features.Assistant.Tools.ApplyConfigChangeTool>(),
        sp.GetRequiredService<EOS.API.Features.Assistant.Tools.PreviewBatchDecisionTool>(),
        sp.GetRequiredService<EOS.API.Features.Assistant.Catalog.DescribeMechanismTool>(),
        sp.GetRequiredService<EOS.API.Features.Assistant.Tools.ListReportsTool>(),
        sp.GetRequiredService<EOS.API.Features.Assistant.Tools.RunReportTool>(),
        sp.GetRequiredService<EOS.API.Features.Assistant.Tools.RecordHistoryTool>(),
        sp.GetRequiredService<EOS.API.Features.Assistant.Tools.AttachmentListTool>(),
        ]));
builder.Services.AddScoped<EOS.API.Features.Assistant.ChatService>();
builder.Services.Configure<UnifiedFormEditorSettings>(builder.Configuration.GetSection("UnifiedFormEditor"));
builder.Services.Configure<AttachmentSettings>(builder.Configuration.GetSection("Attachment"));
builder.Services.Configure<ReportInboxSettings>(builder.Configuration.GetSection("ReportInbox"));
builder.Services.Configure<ReportFormatsSettings>(builder.Configuration.GetSection(ReportFormatsSettings.SectionName));
builder.Services.AddHostedService<ReportInboxScheduler>();
builder.Services.Configure<EOS.API.Models.AuditSettings>(builder.Configuration.GetSection("Audit"));

var app = builder.Build();

foreach (var issue in referenceIssues)
{
    app.Logger.LogWarning("配置引用未解析：{Issue}", issue);
}

if (!app.Configuration.GetValue("Audit:FieldChangesEnabled", true))
{
    app.Logger.LogWarning("字段级审计已关闭（Audit:FieldChangesEnabled=false）：仅写摘要级审计，AUDIT_FIELD_CHANGE 停写。");
}
if (logFileProvider.IsDisabled)
{
    app.Logger.LogError(
        "文件日志不可用（{LogPath}）：目录不可创建或不可写，本次运行只向控制台输出。排障请改读控制台或修复目录权限。",
        logFilePath);
}
else
{
    app.Logger.LogInformation("文件日志已启用：{LogPath}（Warning+，按大小与 {RetentionDays} 天双门保留）。",
        logFilePath, app.Configuration.GetValue<int?>("Logging:File:RetentionDays") ?? JsonFileLoggerProvider.DefaultRetentionDays);
}

app.UseMiddleware<RequestLoggingMiddleware>();
app.UseMiddleware<SameOriginGuardMiddleware>();
app.UseExceptionHandler();
if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
var openApiEndpoint = app.MapOpenApi();
if (app.Environment.IsDevelopment())
{
    openApiEndpoint.AllowAnonymous();
}
else
{
    openApiEndpoint.RequireAuthorization();
}
app.MapHealthChecks("/health/live").AllowAnonymous();
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready"),
    AllowCachingResponses = false,
    ResponseWriter = WriteHealthReportAsync,
}).AllowAnonymous();
app.MapHealthChecks("/health/startup", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("startup"),
    AllowCachingResponses = false,
    ResponseWriter = WriteHealthReportAsync,
}).AllowAnonymous();
var metricsEndpoint = app.MapGet("/metrics", (ApiMetrics metrics) =>
    Results.Text(metrics.RenderPrometheus(), "text/plain; version=0.0.4; charset=utf-8"));
if (app.Environment.IsDevelopment())
{
    metricsEndpoint.AllowAnonymous();
}
// 构建/部署漂移判定：进程启动时间早于二进制构建时间 ⇒ 运行中的进程不是当前构建（需重启）。
var versionEndpoint = app.MapGet("/health/version", () =>
{
    var assembly = typeof(Program).Assembly;
    var builtUtc = BuildInfo.BuildTimeUtc(assembly);
    var process = System.Diagnostics.Process.GetCurrentProcess();
    var binaryUtc = File.Exists(assembly.Location)
        ? File.GetLastWriteTimeUtc(assembly.Location).ToString("yyyy-MM-ddTHH:mm:ssZ")
        : null;
    return Results.Json(new
    {
        commit = BuildInfo.Commit(assembly),
        buildTimeUtc = builtUtc,
        binaryWriteTimeUtc = binaryUtc ?? "unknown",
        processStartTimeUtc = process.StartTime.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ"),
        informationalVersion = assembly.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()?.InformationalVersion,
        assemblyVersion = assembly.GetName().Version?.ToString(),
        environment = app.Environment.EnvironmentName,
        stale = BuildInfo.IsStale(builtUtc, process.StartTime.ToUniversalTime()),
    });
});
if (app.Environment.IsDevelopment())
{
    versionEndpoint.AllowAnonymous();
}
app.MapFallbackToFile("index.html").RequireAuthorization();

ErpDatabaseInitializer.Run(builder.Configuration, app.Logger);
await app.Services.GetRequiredService<WorkbenchDefinitionProvider>().RefreshAsync(CancellationToken.None);
// 助手运行期配置同样在这里读一次：迁移刚跑完，ASSISTANT_MODEL / ASSISTANT_PROVIDER（3102）
// 与 dbo.SYSSS 的 OWNER_MODULE = 3105（3105 助手设置）才是可用的
// （照上一行的做法）。读失败不会中断启动——注册表内部会沿用旧值，最终表现为"未配置"，
// 而助手会以一句**可执行的**文案告诉管理员去哪儿配（不再有"退回 appsettings"的兜底）。
await app.Services
    .GetRequiredService<EOS.API.Features.Assistant.ModelAccess.AssistantRuntimeRegistry>()
    .RefreshAsync(CancellationToken.None);

RegisterPdfFont();

app.Run();

static void RegisterPdfFont()
{
    var fontPath = Path.Combine(AppContext.BaseDirectory, "Fonts", "NotoSansCJKsc-Regular.otf");
    if (!File.Exists(fontPath)) return;
    // 进程内只注册一次；QuestPDF 内部持有字体数据，流保持打开由进程回收。
    // QuestPDF 2026.9 起不再支持「自定义名注册」：改用 RegisterFontFromStream，族名以字体文件里的为准，
    // 报表侧依旧按 PdfLayout.FontFamily 引用。下面顺手核对一次族名——对不上时打一条 ERROR，
    // 免得等到出报表 PDF 才发现中文没生效（这类故障在 PDF 里表现为方框，排查成本高）。
    QuestPDF.Drawing.FontManager.RegisterFontFromStream(File.OpenRead(fontPath));

    var families = QuestPDF.Drawing.FontManager.GetRegisteredFonts()
        .Select(font => font.FamilyName)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();
    if (!families.Contains(EOS.API.Data.PdfLayout.FontFamily, StringComparer.OrdinalIgnoreCase))
    {
        Console.Error.WriteLine(
            $"[启动] PDF 中文字体族名核对未通过：字体文件里没有族名「{EOS.API.Data.PdfLayout.FontFamily}」，"
            + $"实际注册到的族名有：{string.Join(" / ", families)}");
    }
}

/// <summary>
/// 日志文件路径：默认落在程序自身目录下的 logs（与附件、字体、版式的取址一致），
/// 不再依赖宿主工作目录——IIS/服务宿主的当前目录不受本仓库控制。
/// 配置值可为相对路径（相对程序目录）、绝对路径或含环境变量的路径。
/// </summary>
static string ResolveLogFilePath(IConfiguration configuration)
{
    var configured = configuration["Logging:File:Path"];
    if (string.IsNullOrWhiteSpace(configured))
    {
        return Path.Combine(AppContext.BaseDirectory, "logs", "api-json.log");
    }
    return Path.GetFullPath(configured.Trim(), AppContext.BaseDirectory);
}

static DirectoryInfo GetDataProtectionKeysDirectory(IConfiguration configuration)
{
    var configured = configuration["DataProtection:KeysDirectory"];
    var path = string.IsNullOrWhiteSpace(configured)
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EOS", "DataProtection-Keys")
        : configured;
    return new DirectoryInfo(path);
}

static async Task WriteHealthReportAsync(HttpContext context, HealthReport report)
{
    context.Response.ContentType = "application/json; charset=utf-8";
    var entries = report.Entries.ToDictionary(
        entry => entry.Key,
        entry => new { status = entry.Value.Status.ToString(), description = entry.Value.Description });
    await context.Response.WriteAsJsonAsync(new { status = report.Status.ToString(), entries });
}
