using System.IO.Compression;
using System.Text;
using System.Text.Json;
using EOS.API.Data;
using EOS.API.Errors;
using EOS.API.Security;
using EOS.API.Telemetry;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace EOS.API.Features.Diagnostics;

/// <summary>
/// 日志管理（定制页 `/admin/logs` 的后端）：
/// 读运行日志、看系统诊断信息、下载诊断包（zip）。
///
/// 权限（ADR-029 §2.1）：模块 <b>2313</b>（系统管理 → 数据表维护 → 日志管理，即本页自身）
/// 的 `CanBrowse` 可读日志与诊断信息；`CanSetup` 才可**打包下载**
/// （打包会把日志带出服务器，权限更高一档）。页面、菜单与接口锚点同为 2313，
/// 避免"菜单看一个模块、接口看另一个模块"的双真源。
///
/// 打包内容为白名单 6 项（日志 + diagnostics/health/migrations-summary/config-digest/manifest），
/// **不含**配置文件原文、审计明细原文、业务数据；日志在写入时已过 `LogRedactor`。
/// </summary>
[ApiController, Authorize, Route("api/v1/logs")]
public sealed class LogsController(
    LogFileReader reader,
    IPermissionService permissions,
    CurrentUserContext userContext,
    IConfiguration configuration,
    HealthCheckService healthChecks,
    DbConnectionFactory connections,
    WorkbenchAuditWriter auditWriter,
    ILogger<LogsController> logger) : ControllerBase
{
    private const int LogModuleId = 2313;
    private const long MaxBundleBytes = 100L * 1024 * 1024;

    [HttpGet("files")]
    public async Task<IActionResult> Files(CancellationToken token)
    {
        if (!(await permissions.GetAsync(userContext.UserId, LogModuleId, token)).CanBrowse) return Forbid();
        return Ok(new
        {
            files = reader.ListFileInfo(),
            totalBytes = reader.TotalBytes(),
            levels = LogFileReader.KnownLevels,
        });
    }

    [HttpGet]
    public async Task<IActionResult> Query(
        [FromQuery] int take = 200,
        [FromQuery] string? level = null,
        [FromQuery] string? keyword = null,
        [FromQuery] string? correlationId = null,
        [FromQuery] string? @event = null,
        [FromQuery] DateTimeOffset? from = null,
        CancellationToken token = default)
    {
        if (!(await permissions.GetAsync(userContext.UserId, LogModuleId, token)).CanBrowse) return Forbid();
        var (entries, truncated) = reader.Read(new LogQuery(take, level, keyword, correlationId, @event, from));
        return Ok(new { entries, count = entries.Count, truncated });
    }

    /// <summary>系统诊断信息：版本/构建/进程/健康检查/迁移台账摘要（不含业务数据）。</summary>
    [HttpGet("diagnostics")]
    public async Task<IActionResult> Diagnostics(CancellationToken token)
    {
        if (!(await permissions.GetAsync(userContext.UserId, LogModuleId, token)).CanBrowse) return Forbid();
        return Ok(await BuildDiagnosticsAsync(token));
    }

    /// <summary>
    /// 诊断包下载：流式 zip，不落中间文件、不上传任何外部服务。
    /// 记录一条打包日志（含打包人与体积），便于事后核对"谁取走了什么"。
    /// </summary>
    [HttpGet("bundle")]
    public async Task<IActionResult> Bundle(CancellationToken token)
    {
        var permission = await permissions.GetAsync(userContext.UserId, LogModuleId, token);
        if (!permission.CanBrowse) return Forbid();
        if (!permission.CanSetup)
        {
            return StatusCode(StatusCodes.Status403Forbidden,
                ApiProblem.Forbidden("打包下载需要日志管理的设置权限。"));
        }

        var files = reader.ListFiles();
        var totalBytes = files.Sum(file => file.Length);
        if (totalBytes > MaxBundleBytes)
        {
            return StatusCode(StatusCodes.Status413PayloadTooLarge, ApiProblem.Create(
                StatusCodes.Status413PayloadTooLarge,
                "DIAGNOSTICS_BUNDLE_TOO_LARGE",
                $"诊断包超过上限（{MaxBundleBytes / 1024 / 1024}MB），请先按时间或级别筛选。"));
        }

        var diagnostics = await BuildDiagnosticsAsync(token);
        var stamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss");
        var bundleName = $"eos-diagnostics-{stamp}.zip";
        // 体积上限 100MB，内存缓冲即可；上限若放宽应改落临时文件再回传
        var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            var written = reader.WriteToZip(archive);
            AddJson(archive, "diagnostics.json", diagnostics);
            AddText(archive, "manifest.txt", BuildManifest(written, diagnostics, loaderFailed: false));
        }
        buffer.Position = 0;
        // 用 RequestContext 的报障编号（不是 TraceIdentifier）：用户从响应/界面拿到的编号
        // 必须能在文件日志里检索到这条"谁取走了诊断包"。
        var correlationId = RequestContext.GetCorrelationId(HttpContext);
        logger.LogWarning(
            "诊断包已生成 size={SizeBytes} files={FileCount} user={User} correlation={CorrelationId}",
            buffer.Length, files.Count, userContext.UserId, correlationId);
        // 落审计表（best-effort）：文件日志只留 Warning+ 且可被清理，而"谁在何时取走了诊断包"
        // 属运维审计面，要能在 AUDIT_EVENT 里按执行者与时间检索到（打包失败不应因此失败）。
        await auditWriter.WriteBestEffortAsync(
            moduleId: LogModuleId,
            resourceKey: bundleName,
            action: "DIAGNOSTICS_BUNDLE",
            summary: $"下载诊断包 {bundleName}（{files.Count} 个日志文件，{buffer.Length / 1024} KB，含日志与环境元数据）",
            executor: userContext.UserId,
            resourceType: "DIAGNOSTICS",
            result: 1,
            fieldChanges: null,
            token);
        return File(buffer, "application/zip", bundleName);
    }

    private async Task<object> BuildDiagnosticsAsync(CancellationToken token)
    {
        var assembly = typeof(LogsController).Assembly;
        var process = System.Diagnostics.Process.GetCurrentProcess();
        var buildTimeUtc = BuildInfo.BuildTimeUtc(assembly);
        var processStartUtc = process.StartTime.ToUniversalTime();

        var healthReport = await healthChecks.CheckHealthAsync(token);
        var health = healthReport.Entries.ToDictionary(
            entry => entry.Key,
            entry => new
            {
                status = entry.Value.Status.ToString(),
                description = entry.Value.Description,
                elapsedMs = entry.Value.Duration.TotalMilliseconds,
            });

        int? appliedMigrations = null;
        string? lastMigration = null;
        string? journalError = null;
        try
        {
            await using var connection = connections.Create();
            await connection.OpenAsync(token);
            (appliedMigrations, lastMigration) = await LogFileReader.ReadJournalAsync(connection, token);
        }
        catch (Exception exception) when (exception is InvalidOperationException or Microsoft.Data.SqlClient.SqlException)
        {
            // 库不可达不影响取日志：诊断信息如实标注缺失原因，而不是整包失败
            journalError = exception.Message;
        }

        return new
        {
            app = new
            {
                version = BuildInfo.ProductVersion(assembly),
                commit = BuildInfo.Commit(assembly),
                buildTimeUtc,
                assemblyVersion = assembly.GetName().Version?.ToString(),
                environment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Production",
            },
            process = new
            {
                startTimeUtc = processStartUtc.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                binaryWriteTimeUtc = System.IO.File.Exists(assembly.Location)
                    ? System.IO.File.GetLastWriteTimeUtc(assembly.Location).ToString("yyyy-MM-ddTHH:mm:ssZ")
                    : null,
                // 运行中的进程早于二进制构建时间 ⇒ 跑的不是当前构建（部署漂移）
                stale = BuildInfo.IsStale(buildTimeUtc, processStartUtc),
                machineName = Environment.MachineName,
                osVersion = Environment.OSVersion.VersionString,
            },
            logs = new
            {
                path = CurrentLogPath(),
                files = reader.ListFileInfo(),
                totalBytes = reader.TotalBytes(),
            },
            health,
            migrations = new
            {
                appliedCount = appliedMigrations,
                lastScript = lastMigration,
                embeddedCount = EmbeddedMigrationCount,
                error = journalError,
            },
            // 只报"有哪些配置项、是否已设置"，不报任何值（值里可能有凭据）
            configDigest = ConfigDigest(configuration),
            generatedAtUtc = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
        };
    }

    private string CurrentLogPath() => reader.ListFiles().FirstOrDefault()?.FullName ?? "(未落盘)";

    private static string BuildManifest(
        IReadOnlyList<(string Name, long SizeBytes)> written,
        object diagnostics,
        bool loaderFailed)
    {
        var text = new StringBuilder();
        text.AppendLine("EOS 诊断包清单");
        text.AppendLine($"生成时间：{DateTimeOffset.Now:yyyy-MM-dd HH:mm:sszzz}");
        text.AppendLine("脱敏声明：日志在写入时已按 LogRedactor 模式清单脱敏（凭据、Token、Cookie、连接串要素）。");
        text.AppendLine("范围声明：仅含运行日志与环境元数据；不含配置文件原文、审计明细原文与任何业务数据。");
        text.AppendLine(loaderFailed
            ? "注意：打包瞬间有文件不可读，已在下方标注。"
            : "文件读取：全部成功。");
        text.AppendLine();
        text.AppendLine("包含文件：");
        foreach (var (name, size) in written)
        {
            text.AppendLine($"  - {name} ({size / 1024.0:0.0} KB)");
        }
        text.AppendLine("  - diagnostics.json（版本/进程/健康检查/迁移台账摘要/配置项清单）");
        text.AppendLine("  - manifest.txt（本文件）");
        text.AppendLine();
        text.AppendLine("诊断信息摘要：");
        text.AppendLine(JsonSerializer.Serialize(diagnostics, new JsonSerializerOptions { WriteIndented = true }));
        return text.ToString();
    }

    private static void AddJson(ZipArchive archive, string name, object payload)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write(JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void AddText(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write(content);
    }

    /// <summary>
    /// 配置项清单：**只列出应用自身的配置节与键名，以及它是否已设置**，值一律不下发。
    ///
    /// 用**白名单**而不是"排除几个敏感节"：`IConfiguration` 除 appsettings 外还包含
    /// 环境变量与命令行等提供程序，遍历它会顺带把整台机器的环境变量名列进诊断包
    /// （实测曾导出 `DEEPSEEK_API_KEY` / `MSSQL_ERP_CONN` / `WINDOWS_PASSWORD` 这类键名）——
    /// 值虽未泄露，但键名本身就是不该外发的信息，且与"排查这一版应用"毫无关系。
    /// 需要新增可观测的配置节时，在 `ObservableSections` 里显式登记。
    /// </summary>
    private static readonly string[] ObservableSectionList =
    [
        "Logging", "EffectEngine", "Audit", "Workflow", "UnifiedFormEditor",
        "Security", "Attachment", "ReportFormats", "AllowedHosts",
    ];

    /// <summary>可观测的配置节白名单（单测直接引用同一份，避免"测试用一套、生产用另一套"）。</summary>
    internal static IReadOnlyList<string> ObservableSections => ObservableSectionList;
    private static IReadOnlyDictionary<string, bool> ConfigDigest(IConfiguration configuration) =>
        BuildConfigDigest(configuration, ObservableSections);

    /// <summary>纯函数入口：给定配置与白名单节名，产出"键 → 是否已设置"（供单测断言不含环境变量名）。</summary>
    internal static SortedDictionary<string, bool> BuildConfigDigest(IConfiguration configuration, IEnumerable<string> observableSections)
    {
        var digest = new SortedDictionary<string, bool>(StringComparer.Ordinal);
        foreach (var sectionName in observableSections)
        {
            var section = configuration.GetSection(sectionName);
            if (!section.Exists()) continue;
            WriteDigest(section, sectionName, digest);
        }
        return digest;
    }

    /// <summary>
    /// 递归登记"键 → 是否已设置"；不读取、不返回值本身（`section.Value` 只用于判空）。
    /// **数组型配置折叠为下标**：`EnabledModuleIds` 之类有 200+ 项，逐项列出既没信息量又把诊断包撑大，
    /// 只报"有哪些下标/共几项"即可（数量本身仍能看出配置规模）。
    /// </summary>
    private static void WriteDigest(IConfigurationSection section, string path, SortedDictionary<string, bool> digest)
    {
        var children = section.GetChildren().ToList();
        if (children.Count == 0)
        {
            digest[path] = !string.IsNullOrWhiteSpace(section.Value);
            return;
        }
        if (children.All(child => int.TryParse(child.Key, out _)))
        {
            digest[$"{path}（共 {children.Count} 项）"] = children.Any(child => !string.IsNullOrWhiteSpace(child.Value));
            return;
        }
        foreach (var child in children)
        {
            WriteDigest(child, $"{path}:{child.Key}", digest);
        }
    }

    /// <summary>已内嵌的迁移脚本数：与台账条数对照可看出"磁盘上还有未执行的迁移"。</summary>
    private static int EmbeddedMigrationCount =>
        typeof(LogsController).Assembly.GetManifestResourceNames()
            .Count(name => name.Contains(".Data.Migrations.", StringComparison.OrdinalIgnoreCase));
}
