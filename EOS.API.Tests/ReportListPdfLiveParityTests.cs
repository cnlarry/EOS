using System.Reflection;
using EOS.API.Data;
using EOS.API.Models;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using QuestPDF;
using QuestPDF.Infrastructure;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 真实报表的**分页对拍**：同一份真实数据分别走命令式模板与版式解释层，比页数。
///
/// <para>
/// 与 <see cref="ReportListPdfPageParityTests"/> 的分工：那边用合成数据看几何与杠杆（快、无依赖），
/// 这边用真库的真实宽表与长文本看**折行行为**——合成数据造不出真实报表那种"某列全是很长的中文"。
/// 两者都要，缺一不能说明"换渲染实现不掉分页"。
/// </para>
/// <para>
/// 只读：不写任何业务数据，不启停服务，直接连库取数并经生产同一条取数路径（QueryPdfAsync）渲染。
/// </para>
/// </summary>
[Trait("Category", "Integration")]
[Collection("live-database")]
public sealed class ReportListPdfLiveParityTests
{
    /// <summary>覆盖四种形态：宽表 / 汇总数据源 / 分组方案 / 普通明细。</summary>
    private static readonly string[] Targets =
    [
        "Product_List", "PUR_Apply_List", "MOC_Produce_List",
        "HR_EMPLOYEE_LIST", "INV_Pro_Depot_1", "INV_Batch_Expiry_1",
    ];

    /// <summary>解释层不得比命令式实现多印出可观的纸（2% 是分页边界余量）。</summary>
    private const double PageToleranceRatio = 1.02;

    private sealed class StubEnvironment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "EOS.API";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = string.Empty;
        public string EnvironmentName { get; set; } = "Test";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    static ReportListPdfLiveParityTests()
    {
        QuestPDF.Settings.License = LicenseType.Community;
        var fontPath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..",
            "EOS.API", "Fonts", "NotoSansCJKsc-Regular.otf");
        fontPath = Path.GetFullPath(fontPath);
        if (File.Exists(fontPath))
            QuestPDF.Drawing.FontManager.RegisterFontWithCustomName(PdfLayout.FontFamily, File.OpenRead(fontPath));
    }

    private static int CountPages(byte[] pdf)
    {
        var text = System.Text.Encoding.Latin1.GetString(pdf);
        var pages = System.Text.RegularExpressions.Regex.Matches(text, @"/Type\s*/Page(?![s])").Count;
        var counts = System.Text.RegularExpressions.Regex.Matches(text, @"/Count\s+(\d+)")
            .Select(match => int.Parse(match.Groups[1].Value)).DefaultIfEmpty(0).Max();
        return Math.Max(pages, counts);
    }

    [Fact]
    public async Task 真实报表_命令式与解释层的分页对拍()
    {
        var connectionString = Environment.GetEnvironmentVariable("MSSQL_ERP_CONN");
        Assert.False(string.IsNullOrWhiteSpace(connectionString),
            "真库集成测试需要 MSSQL_ERP_CONN；未配置即失败（无连接跳过≠已验证）。");

        var factory = PolicyServiceFactory.Connections(connectionString!);
        var reportRepository = new ReportRepository(factory, NullLogger<ReportRepository>.Instance);
        var printSettings = new PrintSettingsRepository(factory, NullLogger<PrintSettingsRepository>.Instance);
        var legacy = new ReportPdfService(new StubEnvironment(), NullLogger<ReportPdfService>.Instance);
        var renderer = new QuestPdfLayoutRenderer(NullLogger<QuestPdfLayoutRenderer>.Instance);
        var layoutJson = File.ReadAllText(Path.Combine(
            RepoRoot(), "EOS.API", "ReportFormats", "_generic", "layout.list.json"));

        var lines = new List<string>
        {
            "报表,模块,行数,旧页数,新页数,页数增幅,两侧置零页数,仅页头置零,仅页脚置零",
        };
        var violations = new List<string>();
        var token = CancellationToken.None;

        foreach (var reportId in Targets)
        {
            var moduleId = await ResolveModuleAsync(connectionString!, reportId);
            if (moduleId is null)
            {
                lines.Add($"{reportId},缺失,0,0,0,跳过,0,跳过");
                continue;
            }

            var definition = await reportRepository.GetDefinitionAsync(
                moduleId.Value, "admin", true, true, new HashSet<string>(), reportId, token);
            if (definition is null)
            {
                lines.Add($"{reportId},{moduleId},definition 为空,0,0,跳过,0,跳过");
                continue;
            }

            var meta = await printSettings.GetPdfMetaAsync(moduleId.Value, reportId, token)
                       ?? new ReportPdfMeta(reportId, definition.Title, null, null, null, null, null, null, null, []);

            var rows = await reportRepository.QueryPdfAsync(
                definition,
                new ReportQueryRequest(new Dictionary<int, string?>(), new Dictionary<int, string?>()),
                definition.ModuleFilter, meta.ReportFilter, null,
                definition.SortFields, [], token);

            var input = new ReportPdfRenderInput(
                meta, definition, rows, string.Empty, "admin", [], false, true, meta.Header, meta.TailText);

            var legacyPdf = legacy.Generate(input);
            var composed = ReportListPdfComposer.Compose(input, new StubEnvironment());
            var newPdf = renderer.RenderReportList(composed.Data, layoutJson, composed.Context);
            // 诊断变体：把页头/页脚压到近零，分辨"解释层每页少掉的表体高度"到底是
            // 页面家具（页头页脚块）吃掉的，还是表体自己排得更胖。
            var barePdf = renderer.RenderReportList(composed.Data, BareLayout(layoutJson), composed.Context);
            var noHeaderPdf = renderer.RenderReportList(composed.Data, Variant(layoutJson, 0.1, null), composed.Context);
            var noFooterPdf = renderer.RenderReportList(composed.Data, Variant(layoutJson, null, 0.1), composed.Context);

            var pdfDir = Path.Combine(Path.GetTempPath(), "opencode", "r6live");
            Directory.CreateDirectory(pdfDir);
            File.WriteAllBytes(Path.Combine(pdfDir, $"{reportId}-legacy.pdf"), legacyPdf);
            File.WriteAllBytes(Path.Combine(pdfDir, $"{reportId}-layout.pdf"), newPdf);

            var legacyPages = CountPages(legacyPdf);
            var newPages = CountPages(newPdf);
            var barePages = CountPages(barePdf);
            var noHeaderPages = CountPages(noHeaderPdf);
            var noFooterPages = CountPages(noFooterPdf);
            var delta = legacyPages == 0 ? 0 : (newPages - legacyPages) * 100.0 / legacyPages;
            lines.Add($"{reportId},{moduleId},{rows.Total},{legacyPages},{newPages},{delta:F1}%," +
                      $"{barePages},{noHeaderPages},{noFooterPages}");

            if (legacyPages > 0 && newPages > legacyPages * PageToleranceRatio)
                violations.Add($"{reportId}（{moduleId}）：旧 {legacyPages} 页 → 新 {newPages} 页（{delta:+0.0;-0.0}%）");
        }

        var outDir = Path.Combine(Path.GetTempPath(), "opencode");
        Directory.CreateDirectory(outDir);
        File.WriteAllLines(Path.Combine(outDir, "r6-live-page-parity.txt"), lines);

        Assert.True(violations.Count == 0,
            "真实报表上解释层的分页超出命令式实现 2% 以上：" + string.Join("；", violations));
    }

    /// <summary>
    /// 把页头/页脚块压到近零的版式副本。只用于诊断："解释层每页少掉的表体高度"是页面家具
    /// 吃掉的，还是表体自己排得更胖——两者的修法完全不同，先分开再动手。
    /// </summary>
    private static string BareLayout(string layoutJson) => Variant(layoutJson, 0.1, 0.1);

    /// <summary>只改页头或只改页脚高度的诊断副本（null = 该侧保持原值），用来分清两端各占多少。</summary>
    private static string Variant(string layoutJson, double? headerMm, double? footerMm)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(layoutJson)!;
        if (headerMm is { } header) node["sections"]!["header"]!["height"] = header;
        if (footerMm is { } footer) node["sections"]!["footer"]!["height"] = footer;
        return node.ToJsonString();
    }

    private static string RepoRoot()
    {
        var root = typeof(ReportListPdfLiveParityTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(item => item.Key == "RepoRoot")?.Value;
        Assert.False(string.IsNullOrWhiteSpace(root), "缺少构建期元数据 RepoRoot。");
        return root!;
    }

    private static async Task<int?> ResolveModuleAsync(string connectionString, string reportId)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            "SELECT M_IDX FROM dbo.REPORT WITH (NOLOCK) WHERE LTRIM(RTRIM(REPORT_ID)) = @ReportId;", connection);
        command.Parameters.AddWithValue("@ReportId", reportId);
        var value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? null : Convert.ToInt32(value);
    }
}
