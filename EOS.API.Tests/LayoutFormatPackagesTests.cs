using System.Text;
using System.Text.Json;
using EOS.API.Data;
using EOS.API.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 24 张内置版式格式包冒烟测试（ADR-010 §5 S1 迁移）：
/// 逐目录加载 format.json / layout.json / sample.json，用 sample 数据渲染 PDF，
/// 断言字节有效（%PDF 头）。内容级对拍由迁移对拍脚本（scripts/check-layout-migration.py）负责。
/// </summary>
public class LayoutFormatPackagesTests
{
    private static readonly QuestPdfLayoutRenderer Renderer =
        new(NullLogger<QuestPdfLayoutRenderer>.Instance);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    internal static readonly string[] ModuleIds =
    [
        "1404", "1604", "1405", "1406", "170101", "170102", "170201", "170202",
        "1607", "1615", "1606", "1408", "170103", "170203", "1407", "1409",
        "1608", "1612", "1610", "1503", "1514", "1504", "1515", "1616",
    ];

    [Fact]
    public void All_24_BuiltinPackages_Render()
    {
        var root = FindReportFormatsRoot();
        Assert.NotNull(root);
        Assert.True(Directory.Exists(root), $"ReportFormats 目录不存在：{root}");

        var rendered = new List<string>();
        foreach (var moduleId in ModuleIds)
        {
            var dir = Path.Combine(root, moduleId);
            Assert.True(Directory.Exists(dir), $"缺少格式包目录：{moduleId}");
            var formatPath = Path.Combine(dir, "format.json");
            var layoutPath = Path.Combine(dir, "layout.json");
            var samplePath = Path.Combine(dir, "sample.json");
            Assert.True(File.Exists(formatPath), $"{moduleId}/format.json 缺失");
            Assert.True(File.Exists(layoutPath), $"{moduleId}/layout.json 缺失");
            Assert.True(File.Exists(samplePath), $"{moduleId}/sample.json 缺失");

            var format = JsonSerializer.Deserialize<ReportFormatDefinition>(
                File.ReadAllText(formatPath), JsonOptions);
            Assert.NotNull(format);
            Assert.Equal(moduleId, format.FormatId);
            Assert.Equal("document", format.Kind);

            var data = BuildPrintData(moduleId, File.ReadAllText(samplePath));
            var pdf = Renderer.Render(data, File.ReadAllText(layoutPath), new LayoutRenderContext("admin"));
            Assert.True(pdf.Length > 1000, $"{moduleId} PDF 字节过小：{pdf.Length}");
            Assert.StartsWith("%PDF", Encoding.ASCII.GetString(pdf[..4]));
            rendered.Add(moduleId);
        }

        Assert.Equal(ModuleIds.Length, rendered.Count);
    }

    [Fact]
    public void FormatContract_ColumnWhitelist_MatchesLayoutReferences()
    {
        var root = FindReportFormatsRoot();
        Assert.NotNull(root);

        foreach (var moduleId in ModuleIds)
        {
            var dir = Path.Combine(root, moduleId);
            var format = JsonSerializer.Deserialize<ReportFormatDefinition>(
                File.ReadAllText(Path.Combine(dir, "format.json")), JsonOptions)!;
            var layout = JsonSerializer.Deserialize<LayoutDocument>(
                File.ReadAllText(Path.Combine(dir, "layout.json")), JsonOptions)!;

            var masterKeys = format.DataContract.Columns.Select(c => c.Key).ToHashSet(
                StringComparer.OrdinalIgnoreCase);
            var detailKeys = format.DataContract.DetailColumns.Select(c => c.Key).ToHashSet(
                StringComparer.OrdinalIgnoreCase);

            foreach (var element in layout.Sections.Header.Elements.Concat(
                         layout.Sections.Content.Elements).Concat(layout.Sections.Footer.Elements))
            {
                AssertAllReferencesAllowed(moduleId, element, masterKeys, detailKeys);
                if (element.Columns is null) continue;
                foreach (var column in element.Columns)
                    AssertReferenceIn(moduleId, column.Field, masterKeys, detailKeys);
            }
        }
    }

    [Fact]
    public void FallbackPackages_CardAndGeneric_Render()
    {
        var root = FindReportFormatsRoot();
        Assert.NotNull(root);
        var sample = File.ReadAllText(Path.Combine(root, "1405", "sample.json"));
        var data = BuildPrintData("1405", sample);

        foreach (var (formatId, expectedModule) in new[] { ("_card", 1401), ("_generic", 0) })
        {
            var dir = Path.Combine(root, formatId);
            var format = JsonSerializer.Deserialize<ReportFormatDefinition>(
                File.ReadAllText(Path.Combine(dir, "format.json")), JsonOptions)!;
            Assert.Equal("document", format.Kind);
            // 回退包必须带 sample.json（预览/渲染预览的样例数据源）
            var sampleJson = File.ReadAllText(Path.Combine(dir, "sample.json"));
            var parsed = SamplePrintDataFactory.Build(
                expectedModule.ToString(System.Globalization.CultureInfo.InvariantCulture), sampleJson);
            Assert.True(parsed.Master.Count > 0, $"{formatId} sample 主表数据缺失");
            var layoutJson = File.ReadAllText(Path.Combine(dir, "layout.json"));
            var errors = new ReportFormatValidator().Validate(format, layoutJson);
            Assert.True(errors.Count == 0, $"{formatId} 应通过校验：{string.Join("; ", errors)}");

            var pdf = Renderer.Render(data, layoutJson, new LayoutRenderContext("admin"));
            Assert.True(pdf.Length > 500, $"{formatId} PDF 字节过小");
            Assert.StartsWith("%PDF", Encoding.ASCII.GetString(pdf[..4]));
        }
    }

    private static void AssertAllReferencesAllowed(
        string moduleId, LayoutElement element, HashSet<string> masterKeys, HashSet<string> detailKeys)
    {
        if (element.Field is not null)
            AssertReferenceIn(moduleId, element.Field, masterKeys, detailKeys);
        if (element.Content is null) return;
        foreach (System.Text.RegularExpressions.Match match in
                 System.Text.RegularExpressions.Regex.Matches(
                     element.Content, @"\{\{([A-Za-z][A-Za-z0-9_.]*)\}\}"))
            AssertReferenceIn(moduleId, match.Groups[1].Value, masterKeys, detailKeys);
    }

    private static void AssertReferenceIn(
        string moduleId, string reference, HashSet<string> masterKeys, HashSet<string> detailKeys)
    {
        if (reference.StartsWith("MASTER.", StringComparison.OrdinalIgnoreCase))
            Assert.True(masterKeys.Contains(reference["MASTER.".Length..]),
                $"{moduleId} layout 引用 MASTER.{reference["MASTER.".Length..]} 不在 dataContract");
        else if (reference.StartsWith("DETAILS.", StringComparison.OrdinalIgnoreCase))
        {
            var key = reference["DETAILS.".Length..];
            Assert.True(detailKeys.Contains(key) || key is "ROW_INDEX" or "PRODUCT_TEXT",
                $"{moduleId} layout 引用 DETAILS.{key} 不在 dataContract");
        }
        else if (!reference.StartsWith("SYS.", StringComparison.OrdinalIgnoreCase))
            Assert.Fail($"{moduleId} layout 引用命名空间非法：{reference}");
    }

    internal static PrintData BuildPrintData(string moduleId, string sampleJson)
    {
        using var doc = JsonDocument.Parse(sampleJson);
        var root = doc.RootElement;

        var master = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var prop in root.GetProperty("master").EnumerateObject())
            master[prop.Name] = ReadJsonValue(prop.Value);

        var details = new List<IReadOnlyDictionary<string, object?>>();
        foreach (var row in root.GetProperty("details").EnumerateArray())
        {
            var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var prop in row.EnumerateObject())
                dict[prop.Name] = ReadJsonValue(prop.Value);
            details.Add(dict);
        }

        var masterFields = root.TryGetProperty("masterFields", out var mf)
            ? mf.EnumerateArray().Select(e => new PrintField(
                e.GetProperty("key").GetString()!, e.GetProperty("label").GetString()!,
                e.TryGetProperty("displayFormat", out var df) ? df.GetString() : null)).ToList()
            : new List<PrintField>();
        var detailFields = root.TryGetProperty("detailFields", out var df2)
            ? df2.EnumerateArray().Select(e => new PrintField(
                e.GetProperty("key").GetString()!, e.GetProperty("label").GetString()!,
                e.TryGetProperty("displayFormat", out var fmt) ? fmt.GetString() : null)).ToList()
            : new List<PrintField>();

        ClientPrintProfile? profile = null;
        if (root.TryGetProperty("clientProfile", out var cp) && cp.ValueKind == JsonValueKind.Object)
        {
            static string? Str(JsonElement obj, string name)
            {
                if (!obj.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
                    return null;
                return value.GetString();
            }
            profile = new ClientPrintProfile(
                Str(cp, "clientName"), Str(cp, "clientNameCn"),
                Str(cp, "clientNameEn"), Str(cp, "deliAddrCn"),
                Str(cp, "deliAddrEn"), Str(cp, "tel"),
                Str(cp, "fax"), Str(cp, "linkman"),
                Str(cp, "headerId"), cp.TryGetProperty("printPrice", out var pp)
                    && pp.ValueKind == JsonValueKind.True);
        }

        return new PrintData(
            int.Parse(moduleId, System.Globalization.CultureInfo.InvariantCulture),
            root.GetProperty("title").GetString()!,
            root.GetProperty("headerCompany").GetString(),
            root.GetProperty("headerCompanyEn").GetString(),
            root.GetProperty("headerText").GetString(),
            root.GetProperty("footerText").GetString(),
            root.GetProperty("logoPath").ValueKind == JsonValueKind.Null
                ? null : root.GetProperty("logoPath").GetString(),
            root.GetProperty("tailText").GetString(),
            masterFields, detailFields, master, details, profile);
    }

    private static object? ReadJsonValue(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.TryGetDecimal(out var d) ? d : value.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            _ => value.GetRawText(),
        };
    }

    private static string? FindReportFormatsRoot()
    {
        var metadata = typeof(LayoutFormatPackagesTests).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
            .OfType<System.Reflection.AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == "RepoRoot");
        if (metadata?.Value is { Length: > 0 } repoRoot)
        {
            var candidate = Path.Combine(repoRoot, "EOS.API", "ReportFormats");
            if (Directory.Exists(candidate)) return candidate;
        }
        var dir = new DirectoryInfo(Environment.CurrentDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "EOS.API", "ReportFormats");
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }
}
