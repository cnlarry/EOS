using EOS.API.Data;
using EOS.API.Errors;
using EOS.API.Hubs;
using EOS.API.Middleware;
using EOS.API.Models;
using EOS.API.Security;
using EOS.API.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.OpenApi;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(options =>
{
    options.TimestampFormat = "yyyy-MM-dd HH:mm:ss.fff ";
});
if (builder.Environment.IsDevelopment())
{
    builder.Logging.AddDebug();
}

builder.Services.AddControllers(options => options.Filters.Add<ApiExceptionFilter>())
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
            ApiProblem.AttachTraceId(problem, context.HttpContext);
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
            "EOS（原 ERP）业务 API 契约：EOS.Web / EOS.Client / Agent 的统一受控入口。"
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
builder.Services.AddScoped<ApiExceptionFilter>();
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
builder.Services.AddScoped<AdminFieldRepository>();
builder.Services.AddScoped<AuthenticationRepository>();
builder.Services.AddScoped<UserAdminRepository>();
builder.Services.AddScoped<FieldAdminRepository>();
builder.Services.AddScoped<BomRepository>();
builder.Services.AddScoped<DynamicBomRepository>();
builder.Services.AddScoped<FieldConfigurationRepository>();
builder.Services.AddScoped<LegacyRightsRepository>();
builder.Services.AddScoped<NavigationRepository>();
builder.Services.AddScoped<ControlledSprocInvoker>();
builder.Services.AddScoped<DocumentWorkbenchRepository>();
builder.Services.AddScoped<ReportRepository>();
builder.Services.AddScoped<SearchCenterRepository>();
builder.Services.AddScoped<ImportService>();
builder.Services.AddScoped<PrintService>();
builder.Services.AddScoped<CurrentUserContext>();
builder.Services.AddSingleton<HubUserTracker>();
builder.Services.AddSingleton<ImRateLimiter>();
builder.Services.AddScoped<IImConversationRepository, ImConversationRepository>();
builder.Services.AddScoped<IImMessageRepository, ImMessageRepository>();
builder.Services.AddScoped<IImAttachmentRepository, ImAttachmentRepository>();
builder.Services.AddScoped<IImCardService, ImCardService>();
builder.Services.AddScoped<ImHubService>();
builder.Services.AddHostedService<ImCleanupHostedService>();
builder.Services.AddSignalR();
builder.Services.Configure<UnifiedFormEditorSettings>(builder.Configuration.GetSection("UnifiedFormEditor"));

var app = builder.Build();

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
app.Use(async (context, next) =>
{
    if (context.Request.Path == "/" && context.User.Identity?.IsAuthenticated != true)
    {
        context.Response.Redirect("/login.html");
        return;
    }
    await next();
});
app.UseAuthorization();
app.MapControllers();
app.MapHub<ImHub>("/api/hubs/im");
app.MapOpenApi().AllowAnonymous();
app.MapFallbackToFile("index.html").RequireAuthorization();

ImDatabaseInitializer.RunIfConfigured(builder.Configuration, app.Logger);

app.Run();

static DirectoryInfo GetDataProtectionKeysDirectory(IConfiguration configuration)
{
    var configured = configuration["DataProtection:KeysDirectory"];
    var path = string.IsNullOrWhiteSpace(configured)
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EOS", "DataProtection-Keys")
        : Environment.ExpandEnvironmentVariables(configured);
    return new DirectoryInfo(path);
}
