using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using EOS.API.Data;
using EOS.API.Models;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using QuestPDF;
using QuestPDF.Infrastructure;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 列表型报表 PDF 的**分页对照**：同一份数据分别走命令式模板与版式解释层，比页数。
///
/// <para>
/// 存在的理由：两者的文本内容已逐行一致（`logs/report-list-pdf/compare.txt`），差异只在
/// 「同样的数据排成几页」。页数差异不是功能错误，但换实现的成本必须看得见——否则
/// "迁移完成"会以每张报表多印 18% 的纸为代价而不被察觉。
/// </para>
/// <para>
/// 诊断用例：结果写文件，不断言两条链路相等（相等与否由人看数后决定怎么调版式），
/// 只断言两边产出合法 PDF。
/// </para>
/// </summary>
public class ReportListPdfPageParityTests
{
    private sealed class StubEnvironment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "EOS.API";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = string.Empty;
        public string EnvironmentName { get; set; } = "Test";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private static readonly JsonSerializerOptions AssetJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    static ReportListPdfPageParityTests()
    {
        QuestPDF.Settings.License = LicenseType.Community;
        var fontPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..",
            "EOS.API", "Fonts", "NotoSansCJKsc-Regular.otf"));
        if (File.Exists(fontPath))
            QuestPDF.Drawing.FontManager.RegisterFontWithCustomName(PdfLayout.FontFamily, File.OpenRead(fontPath));
    }

    private static string GenericAssetDirectory()
    {
        var repoRoot = typeof(ReportListPdfPageParityTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(item => item.Key == "RepoRoot")?.Value;
        Assert.False(string.IsNullOrWhiteSpace(repoRoot), "缺少构建期元数据 RepoRoot。");
        return Path.Combine(repoRoot!, "EOS.API", "ReportFormats", "_generic");
    }

    /// <summary>PDF 页数：数页树里的 `/Type /Page`（排除 `/Pages`）。</summary>
    private static int CountPages(byte[] pdf)
    {
        var text = Encoding.Latin1.GetString(pdf);
        var pages = Regex.Matches(text, @"/Type\s*/Page(?![s])").Count;
        var counts = Regex.Matches(text, @"/Count\s+(\d+)")
            .Select(match => int.Parse(match.Groups[1].Value))
            .DefaultIfEmpty(0)
            .Max();
        return Math.Max(pages, counts);
    }

    private static ReportDefinition BuildDefinition(int columnCount) =>
        new(129801, "产品资料明细", "PRODUCT", null, [],
            Enumerable.Range(0, columnCount)
                .Select(i => new ReportColumn($"C{i:00}", $"字段{i:00}", "nvarchar", null, IsCost: false, IsSecrecy: false))
                .ToList(),
            ["C00"], []);

    private static ReportQueryResult BuildData(int columnCount, int rowCount)
    {
        var rows = new List<Dictionary<string, object?>>(rowCount);
        for (var r = 0; r < rowCount; r++)
        {
            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            for (var c = 0; c < columnCount; c++)
            {
                // 混入中英文与不同长度，逼真反映折行行为
                row[$"C{c:00}"] = ((r + c) % 4) switch
                {
                    0 => $"A{r:D5}-{c:D2}",
                    1 => $"某某产品名称第{r}号",
                    2 => $"{r * 1.25m:0.00}",
                    _ => $"备注说明文字{r}",
                };
            }
            rows.Add(row);
        }
        return new ReportQueryResult(rows, rowCount, 1, rowCount);
    }

    [Fact]
    public void 列表型报表_命令式与解释层的分页对照()
    {
        var outDir = Path.Combine(Path.GetTempPath(), "opencode");
        Directory.CreateDirectory(outDir);

        var baseLayout = File.ReadAllText(Path.Combine(GenericAssetDirectory(), "layout.list.json"));
        var renderer = new QuestPdfLayoutRenderer(NullLogger<QuestPdfLayoutRenderer>.Instance);
        var legacy = new ReportPdfService(new StubEnvironment(), NullLogger<ReportPdfService>.Instance);

        var lines = new List<string> { "列数,行数,headerMm,footerMm,旧页数,新页数,页数增幅" };
        var violations = new List<string>();
        // 同数据、同内容时，解释层不得比命令式实现多印出可观的纸。
        // 2% 是给分页边界的余量；真正的失败长这样：某次版式调整让 368 页变 435 页（+18%）。
        const double PageToleranceRatio = 1.02;

        // 敏感性扫描：页头/页脚各占多少高度，是"每页少装几行"的主要嫌疑。
        // 按 JSON 节点改写（不靠字面量替换——版式资产一改，字面量替换会静默失配）。
        foreach (var (headerMm, footerMm) in new[]
                 {
                     (0.0, 0.0),      // 0 = 不改写，用资产原值
                     (25.0, 5.0), (22.0, 4.0), (18.0, 3.0),
                 })
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(baseLayout)!;
            if (headerMm > 0)
            {
                node["sections"]!["header"]!["height"] = headerMm;
                node["sections"]!["footer"]!["height"] = footerMm;
            }
            var layoutJson = node.ToJsonString();

            foreach (var (columnCount, rowCount) in new[] { (24, 1500), (40, 1500) })
            {
                var meta = new ReportPdfMeta(
                    "R1", "产品资料明细", null, null, "页脚文字", "ISO9001", null,
                    new ReportHeaderOption("H1", "默认页头", "某某公司", "COMPANY EN", "页头文字", null, null),
                    "表尾文字", []);
                var definition = BuildDefinition(columnCount);
                var data = BuildData(columnCount, rowCount);
                var input = new ReportPdfRenderInput(
                    meta, definition, data, "条件：全部", "user01", [], false, true, meta.Header, meta.TailText);

                var legacyPdf = legacy.Generate(input);
                var composed = ReportListPdfComposer.Compose(input, new StubEnvironment());
                var newPdf = renderer.RenderReportList(composed.Data, layoutJson, composed.Context);

                var legacyPages = CountPages(legacyPdf);
                var newPages = CountPages(newPdf);
                var delta = legacyPages == 0 ? 0 : (newPages - legacyPages) * 100.0 / legacyPages;
                lines.Add($"{columnCount},{rowCount},{headerMm},{footerMm},{legacyPages},{newPages},{delta:F1}%");
                // 只对资产原值（headerMm=0 即未改写）设断言：改写过的变体是拿来看杠杆的。
                if (headerMm == 0 && newPages > legacyPages * PageToleranceRatio)
                {
                    violations.Add($"{columnCount} 列：旧 {legacyPages} 页 → 新 {newPages} 页（{delta:+0.0;-0.0}%）");
                }
            }
        }

        File.WriteAllLines(Path.Combine(outDir, "r6-page-parity.txt"), lines, Encoding.UTF8);
        Assert.True(violations.Count == 0,
            "列表型版式解释层的分页比命令式实现超出 2% 以上（同样的数据多印了纸）：" + string.Join("；", violations));
    }
}
