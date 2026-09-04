using EOS.API.Data;
using EOS.API.Services;
using EOS.API.Errors;
using EOS.API.Health;
using EOS.API.Logging;
using EOS.API.Middleware;
using EOS.API.Models;
using EOS.API.Security;
using EOS.API.Telemetry;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Server;
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
    maxFiles: 3));
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
var mcpAccessTokens = builder.Configuration.GetSection("Logging:Mcp:AccessTokens").Get<string[]>() ?? [];
builder.Services.AddAuthorizationBuilder().AddPolicy("LogMcp", policy => policy.RequireAssertion(context =>
{
    if (context.User.Identity?.IsAuthenticated == true)
    {
        return true;
    }
    if (context.Resource is not HttpContext http)
    {
        return false;
    }
    var header = http.Request.Headers.Authorization.ToString();
    if (string.IsNullOrWhiteSpace(header) || !header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
    {
        return false;
    }
    var token = header["Bearer ".Length..].Trim();
    return mcpAccessTokens.Contains(token, StringComparer.Ordinal);
}));
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
builder.Services.AddScoped<LogQueryService>();
builder.Services.AddMcpServer().WithHttpTransport().WithTools<LogMcpTools>();
builder.Services.AddScoped<AttendanceCalcService>();
builder.Services.AddScoped<WorkbenchDefinitionBuilder>();
builder.Services.AddScoped<WorkbenchFieldMetaMapper>();
builder.Services.AddScoped<DocumentWorkbenchRepository>();
builder.Services.AddScoped<ReportRepository>();
builder.Services.AddScoped<PrintSettingsRepository>();
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
builder.Services.AddScoped<EOS.API.Features.Assistant.Tools.AssistantToolRegistry>(sp =>
    new EOS.API.Features.Assistant.Tools.AssistantToolRegistry(
    [
        sp.GetRequiredService<EOS.API.Features.Assistant.Tools.SearchRecordsTool>(),
        sp.GetRequiredService<EOS.API.Features.Assistant.Tools.GetRecordDetailTool>(),
        sp.GetRequiredService<EOS.API.Features.Assistant.Tools.GetFormSchemaTool>(),
         sp.GetRequiredService<EOS.API.Features.Assistant.Tools.DraftRecordTool>(),
         sp.GetRequiredService<EOS.API.Features.Assistant.Tools.EnumMetricsTool>(),
         sp.GetRequiredService<EOS.API.Features.Assistant.Tools.ListModulesTool>(),
         sp.GetRequiredService<EOS.API.Features.Assistant.Tools.DescribeModuleTool>(),
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
app.MapOpenApi().AllowAnonymous();
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
var mcpEndpoint = app.MapMcp("/api/log-mcp");
if (app.Environment.IsDevelopment())
{
    mcpEndpoint.AllowAnonymous();
}
else
{
    mcpEndpoint.RequireAuthorization("LogMcp");
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
