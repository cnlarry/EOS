using System.Text.Json;
using EOS.API.Models;
using Microsoft.Extensions.Options;

namespace EOS.API.Data;

/// <summary>
/// 格式包仓储（ADR-009 §12 / ADR-010 决策 3）：
/// 内置版式为 Git 资产 + 部署复制到 ReportFormats:StorageRoot（只读）；
/// 本仓储只读加载 format.json / layout.json / sample.json，不做任何写操作。
/// formatId 白名单（^[A-Za-z0-9_-]{1,64}$）防止路径穿越；
/// 客户定制布局存库（REPORT_FORM_LAYOUT），由 S2 的布局仓储承载。
/// </summary>
public sealed class ReportFormatRepository(
    IOptions<ReportFormatsSettings> settings,
    IWebHostEnvironment environment,
    ILogger<ReportFormatRepository> logger)
{
    private static readonly System.Text.RegularExpressions.Regex FormatIdPattern =
        new("^[A-Za-z0-9_-]{1,64}$", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>解析资产根目录：配置优先，否则 ContentRoot/ReportFormats（csproj 随部署复制）。</summary>
    public string ResolveRoot()
    {
        var configured = settings.Value.StorageRoot;
        if (!string.IsNullOrWhiteSpace(configured))
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(configured));
        return Path.Combine(environment.ContentRootPath, "ReportFormats");
    }

    /// <summary>按 formatId 加载格式包（format.json + layout.json + sample.json）。找不到或解析失败返回 null。</summary>
    public ReportFormatPackage? GetPackage(string formatId)
    {
        if (!FormatIdPattern.IsMatch(formatId)) return null;
        try
        {
            var directory = Path.Combine(ResolveRoot(), formatId);
            var formatPath = Path.Combine(directory, "format.json");
            var layoutPath = Path.Combine(directory, "layout.json");
            if (!File.Exists(formatPath) || !File.Exists(layoutPath)) return null;

            var format = JsonSerializer.Deserialize<ReportFormatDefinition>(
                File.ReadAllText(formatPath), JsonOptions);
            var layout = JsonSerializer.Deserialize<LayoutDocument>(
                File.ReadAllText(layoutPath), JsonOptions);
            if (format is null || layout is null) return null;

            var samplePath = Path.Combine(directory, "sample.json");
            var sample = File.Exists(samplePath) ? File.ReadAllText(samplePath) : null;
            return new ReportFormatPackage(format, layout, File.ReadAllText(layoutPath), sample);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "格式包加载失败 formatId={FormatId}", formatId);
            return null;
        }
    }

    /// <summary>
    /// 单据打印型格式解析：优先模块专属格式（formatId=模块号），
    /// 1401/1601 资料卡回退 _card，其余回退 _generic。
    /// </summary>
    public ReportFormatPackage? GetDocumentFormat(int moduleId)
    {
        var direct = GetPackage(moduleId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (direct is not null) return direct;
        if (moduleId is 1401 or 1601) return GetPackage("_card");
        return GetPackage("_generic");
    }
}
