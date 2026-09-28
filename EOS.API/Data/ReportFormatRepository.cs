using System.Text.Json;
using EOS.API.Models;
using Microsoft.Extensions.Options;

namespace EOS.API.Data;

/// <summary>
/// 格式包仓储：
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

            // 列表型版式是**可选**的附加资产：只有需要"报表清单"打印的包才带它。
            // 解析失败时按"没有列表版式"处理并记警告——不能把整包判成不可用，
            // 否则一份坏掉的报表版式会连单据打印一起拖下水。
            LayoutDocument? listLayout = null;
            string? rawListLayout = null;
            var listPath = Path.Combine(directory, "layout.list.json");
            if (File.Exists(listPath))
            {
                rawListLayout = File.ReadAllText(listPath);
                try
                {
                    listLayout = JsonSerializer.Deserialize<LayoutDocument>(rawListLayout, JsonOptions);
                }
                catch (JsonException ex)
                {
                    logger.LogWarning(ex, "列表型版式解析失败，按无列表版式处理 formatId={FormatId}", formatId);
                    listLayout = null;
                    rawListLayout = null;
                }
            }

            return new ReportFormatPackage(format, layout, File.ReadAllText(layoutPath), sample, listLayout, rawListLayout);
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

    /// <summary>
    /// 报表打印用的**列表型版式**解析：报表自带格式（`REPORT.FORMAT_ID`）→ 该报表所属模块的格式 → `_generic`，
    /// 取其中第一份带列表版式的包。都取不到返回 null，由调用方决定怎么办（不在这里悄悄退回旧实现）。
    /// </summary>
    public string? GetReportListLayout(string? formatId, int moduleId)
    {
        var candidates = new List<string?>();
        if (!string.IsNullOrWhiteSpace(formatId)) candidates.Add(formatId!.Trim());
        candidates.Add(moduleId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        candidates.Add("_generic");
        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            var package = GetPackage(candidate!);
            if (package?.ListLayout is not null) return package.RawListLayoutJson;
        }
        logger.LogWarning("报表没有可用的列表型版式 formatId={FormatId} module={ModuleId}", formatId, moduleId);
        return null;
    }

    /// <summary>内置格式包模板清单（模板库：报告全部内置包，跳过 _card/_generic 回退包）。</summary>
    public IReadOnlyList<LayoutTemplateInfo> ListTemplates()
    {
        var root = ResolveRoot();
        if (!Directory.Exists(root)) return [];
        var result = new List<LayoutTemplateInfo>();
        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            var formatPath = Path.Combine(directory, "format.json");
            if (!File.Exists(formatPath)) continue;
            try
            {
                var format = JsonSerializer.Deserialize<ReportFormatDefinition>(
                    File.ReadAllText(formatPath), JsonOptions);
                if (format is null) continue;
                result.Add(new LayoutTemplateInfo(
                    format.FormatId, format.Title, format.ModuleId, format.Kind));
            }
            catch (Exception ex) when (ex is IOException or JsonException)
            {
                logger.LogWarning(ex, "模板清单跳过无法解析的格式包：{Directory}", directory);
            }
        }
        return result.OrderBy(template => template.ModuleId).ToList();
    }
}
