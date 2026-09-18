using EOS.API.Data;
using EOS.API.Data.Effects;
using EOS.API.Services;
using EOS.API.Errors;
using EOS.API.Health;
using EOS.API.Middleware;
using EOS.API.Models;
using EOS.API.Security;
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

builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(options =>
{
    options.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffzzz";
    options.UseUtcTimestamp = false;
    options.JsonWriterOptions = new System.Text.Json.JsonWriterOptions { Indented = false };
});
builder.Logging.AddProvider(new JsonFileLoggerProvider(
    builder.Configuration["Logging:File:Path"] ?? Path.Combine(Directory.GetCurrentDirectory(), "logs", "api-json.log"),
    maxBytes: 50L * 1024 * 1024,
    maxFiles: 3,
    minimumLevel: LogLevel.Warning));
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
    .AddCheck<ConfigurationHealthCheck>("configuration", tags: ["ready"]);
builder.Services.AddScoped<AuthenticationRepository>();
builder.Services.AddScoped<UserAdminRepository>();
builder.Services.AddScoped<FieldAdminRepository>();
builder.Services.AddScoped<RestrictedExpressionService>();
builder.Services.AddScoped<RightsAdminRepository>();
builder.Services.AddScoped<ModuleRightsRepository>();
builder.Services.AddScoped<IPermissionService, PermissionService>();
builder.Services.AddSingleton<PermissionCache>();
builder.Services.AddScoped<NavigationRepository>();
builder.Services.AddScoped<NavigationGroupsRepository>();
builder.Services.AddScoped<MenuAdminRepository>();
builder.Services.AddScoped<ModuleBusinessConfigRepository>();
builder.Services.AddScoped<EffectPlanLoader>();
builder.Services.AddScoped<EffectFormulaExecutor>();
builder.Services.AddScoped<EffectValidationExecutor>();
builder.Services.AddScoped<EffectPhysicalColumns>();
builder.Services.AddScoped<EOS.API.Data.Effects.IEffectServiceHandler, EOS.API.Data.Effects.ServiceEffectHandlers.InventoryMoveHandler>();
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
builder.Services.AddScoped<EOS.API.Data.Effects.IEffectServiceHandler, EOS.API.Data.Effects.ServiceEffectHandlers.CopAccountRollupHandler>();
builder.Services.AddScoped<EOS.API.Data.Effects.IEffectServiceHandler, EOS.API.Data.Effects.ServiceEffectHandlers.CopPrepayRollupHandler>();
builder.Services.AddScoped<EOS.API.Data.Effects.IEffectServiceHandler, EOS.API.Data.Effects.ServiceEffectHandlers.PurchaseDueRollupHandler>();
builder.Services.AddScoped<EOS.API.Data.Effects.IEffectServiceHandler, EOS.API.Data.Effects.ServiceEffectHandlers.PurPrepayRollupHandler>();
builder.Services.AddScoped<EOS.API.Data.Effects.IEffectServiceHandler, EOS.API.Data.Effects.ServiceEffectHandlers.PurPayOffsetHandler>();
builder.Services.AddScoped<EOS.API.Data.Effects.IEffectServiceHandler, EOS.API.Data.Effects.ServiceEffectHandlers.CopReceiptOffsetHandler>();
builder.Services.AddScoped<EOS.API.Data.Effects.IEffectServiceHandler, EOS.API.Data.Effects.ServiceEffectHandlers.CopSendMoFlagHandler>();
builder.Services.AddScoped<EOS.API.Data.Effects.IEffectServiceHandler, EOS.API.Data.Effects.ServiceEffectHandlers.PurPurchaseSyncHandler>();
builder.Services.AddSingleton(builder.Configuration.GetSection("EffectEngine").Get<EffectEngineSettings>() ?? new EffectEngineSettings());
builder.Services.AddScoped<EffectEngineInvoker>();
builder.Services.AddScoped<EffectPipeline>();
builder.Services.AddScoped<ChooserRepository>();
builder.Services.AddScoped<ControlledSprocInvoker>();
builder.Services.AddScoped<WorkflowEngine>();
builder.Services.AddScoped<FlowDefinitionService>();
builder.Services.Configure<WorkflowSettings>(builder.Configuration.GetSection("Workflow"));
builder.Services.AddScoped<DomainRuleService>();
builder.Services.AddScoped<WorkbenchScopeFilter>();
builder.Services.AddScoped<WorkbenchChooserService>();
builder.Services.AddScoped<WorkbenchDirtyMarker>();
builder.Services.AddScoped<WorkbenchDefinitionValidator>();
builder.Services.AddScoped<WorkbenchDefinitionSnapshotService>();
builder.Services.AddScoped<WorkbenchAuditWriter>();
builder.Services.AddScoped<WorkbenchIdempotency>();
builder.Services.AddScoped<WorkbenchVirtualColumnResolver>();
builder.Services.AddScoped<WorkbenchApprovalService>();
builder.Services.AddScoped<WorkbenchQueryComposer>();
builder.Services.AddScoped<WorkbenchCommandHandler>();
builder.Services.AddScoped<AttendanceCalcService>();
builder.Services.AddScoped<HumanResourceJobsService>();
builder.Services.AddScoped<WorkbenchDefinitionBuilder>();
builder.Services.AddScoped<WorkbenchFieldMetaMapper>();
builder.Services.AddScoped<DocumentWorkbenchRepository>();
builder.Services.AddScoped<ReportRepository>();
builder.Services.AddScoped<PrintSettingsRepository>();
builder.Services.AddScoped<SettingsRepository>();
builder.Services.AddScoped<ReportAdminRepository>();
builder.Services.AddScoped<ReportPdfService>();
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
builder.Services.Configure<EOS.API.Features.Assistant.ModelAccess.AssistantSettings>(
    builder.Configuration.GetSection(EOS.API.Features.Assistant.ModelAccess.AssistantSettings.SectionName));
builder.Services.AddSingleton<EOS.API.Features.Assistant.ModelAccess.IChatModel,
    EOS.API.Features.Assistant.ModelAccess.DeepSeekChatModel>();
builder.Services.AddScoped<EOS.API.Data.IAssistantRepository, EOS.API.Data.AssistantRepository>();
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
builder.Services.AddScoped<EOS.API.Features.Assistant.Memory.IAssistantMemoryStore,
    EOS.API.Features.Assistant.Memory.AssistantMemoryStore>();
builder.Services.AddScoped<EOS.API.Features.Assistant.Tools.GetMyDigestTool>();
builder.Services.AddScoped<EOS.API.Data.IKnowledgeRepository, EOS.API.Data.KnowledgeRepository>();
builder.Services.AddScoped<EOS.API.Features.Assistant.ModelAccess.IEmbeddingModel,
    EOS.API.Features.Assistant.ModelAccess.PendingEmbeddingModel>();
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
builder.Services.AddScoped<EOS.API.Features.Assistant.Metrics.IFieldRelationRepository,
    EOS.API.Features.Assistant.Metrics.FieldRelationRepository>();
builder.Services.AddScoped<EOS.API.Features.Assistant.Tools.GetFieldRelationsTool>();
builder.Services.AddScoped<EOS.API.Data.IAssistantUsageRepository, EOS.API.Data.AssistantUsageRepository>();
builder.Services.AddSingleton(sp => new EOS.API.Features.Assistant.Governance.FailureBreaker(
    () => DateTimeOffset.UtcNow,
    sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<EOS.API.Features.Assistant.ModelAccess.AssistantSettings>>().Value.Cost.MaxConsecutiveFailures,
    sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<EOS.API.Features.Assistant.ModelAccess.AssistantSettings>>().Value.Cost.CooldownSeconds));
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
         sp.GetRequiredService<EOS.API.Features.Assistant.Tools.GetMyDigestTool>(),
         sp.GetRequiredService<EOS.API.Features.Assistant.Tools.KbSearchTool>(),
         sp.GetRequiredService<EOS.API.Features.Assistant.Tools.GetModuleFlowTool>(),
         sp.GetRequiredService<EOS.API.Features.Assistant.Tools.DiagnoseModuleTool>(),
         sp.GetRequiredService<EOS.API.Features.Assistant.Tools.ApplyChangeSetTool>(),
     ]));
builder.Services.AddScoped<EOS.API.Features.Assistant.ChatService>();
builder.Services.Configure<UnifiedFormEditorSettings>(builder.Configuration.GetSection("UnifiedFormEditor"));
builder.Services.Configure<AttachmentSettings>(builder.Configuration.GetSection("Attachment"));
builder.Services.Configure<ReportInboxSettings>(builder.Configuration.GetSection("ReportInbox"));
builder.Services.Configure<ReportFormatsSettings>(builder.Configuration.GetSection(ReportFormatsSettings.SectionName));
builder.Services.AddHostedService<ReportInboxScheduler>();
builder.Services.Configure<EOS.API.Models.AuditSettings>(builder.Configuration.GetSection("Audit"));

var app = builder.Build();

if (!app.Configuration.GetValue("Audit:FieldChangesEnabled", true))
{
    app.Logger.LogWarning("字段级审计已关闭（Audit:FieldChangesEnabled=false）：仅写摘要级审计，AUDIT_FIELD_CHANGE 停写。");
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
    var builtUtc = GetAssemblyMetadata(assembly, "BuildTimeUtc");
    var process = System.Diagnostics.Process.GetCurrentProcess();
    var binaryUtc = File.Exists(assembly.Location)
        ? File.GetLastWriteTimeUtc(assembly.Location).ToString("yyyy-MM-ddTHH:mm:ssZ")
        : null;
    return Results.Json(new
    {
        commit = GetAssemblyMetadata(assembly, "GitCommit") ?? "unknown",
        buildTimeUtc = builtUtc ?? "unknown",
        binaryWriteTimeUtc = binaryUtc ?? "unknown",
        processStartTimeUtc = process.StartTime.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ"),
        informationalVersion = assembly.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()?.InformationalVersion,
        assemblyVersion = assembly.GetName().Version?.ToString(),
        environment = app.Environment.EnvironmentName,
        stale = builtUtc is not null
            && DateTime.TryParse(builtUtc, null, System.Globalization.DateTimeStyles.AdjustToUniversal, out var built)
            && process.StartTime.ToUniversalTime() < built,
    });
});
if (app.Environment.IsDevelopment())
{
    versionEndpoint.AllowAnonymous();
}
app.MapFallbackToFile("index.html").RequireAuthorization();

ErpDatabaseInitializer.Run(builder.Configuration, app.Logger);
await app.Services.GetRequiredService<WorkbenchDefinitionProvider>().RefreshAsync(CancellationToken.None);

RegisterPdfFont();

app.Run();

static void RegisterPdfFont()
{
    var fontPath = Path.Combine(AppContext.BaseDirectory, "Fonts", "NotoSansCJKsc-Regular.otf");
    if (!File.Exists(fontPath)) return;
    // 进程内只注册一次；QuestPDF 内部持有字体数据，流保持打开由进程回收
    QuestPDF.Drawing.FontManager.RegisterFontWithCustomName("Noto Sans CJK SC", File.OpenRead(fontPath));
}

static string? GetAssemblyMetadata(System.Reflection.Assembly assembly, string key) =>
    assembly.GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>()
        .FirstOrDefault(attribute => string.Equals(attribute.Key, key, StringComparison.Ordinal))?.Value;

static DirectoryInfo GetDataProtectionKeysDirectory(IConfiguration configuration)
{
    var configured = configuration["DataProtection:KeysDirectory"];
    var path = string.IsNullOrWhiteSpace(configured)
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EOS", "DataProtection-Keys")
        : Environment.ExpandEnvironmentVariables(configured);
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
