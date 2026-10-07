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
/// PDF 生成冒烟测试：验证 QuestPDF 渲染链路在目标框架可用、中文字体已注册、
/// 输出为合法 PDF 字节。若运行环境被 WDAC 拦截原生 SkiaSharp，此测试会失败并提示。
/// </summary>
public class ReportPdfServiceSmokeTests
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

    static ReportPdfServiceSmokeTests()
    {
        QuestPDF.Settings.License = LicenseType.Community;
        var fontPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..",
            "EOS.API", "Fonts", "NotoSansCJKsc-Regular.otf"));
        if (File.Exists(fontPath))
            QuestPDF.Drawing.FontManager.RegisterFontFromStream(File.OpenRead(fontPath));
    }

    [Fact]
    public void GenerateReportPdf_ProducesValidPdfBytes()
    {
        var meta = new ReportPdfMeta(
            "R1", "测试报表", null, null, "页脚文字", "ISO9001",
            null,
            new ReportHeaderOption("H1", "默认页头", "某某公司", null, "页头文字", null, null),
            "表尾文字", []);
        var definition = new ReportDefinition(
            129801, "产品资料明细", "PRODUCT", null,
            [],
            [new ReportColumn("PRO_NO", "产品编号", "nvarchar"), new ReportColumn("PRO_NAME", "品名", "nvarchar")],
            ["PRO_NO"], []);
        var data = new ReportQueryResult(
            [new Dictionary<string, object?> { ["PRO_NO"] = "A1", ["PRO_NAME"] = "测试产品" }],
            1, 1, 1);

        var service = new ReportPdfService(new StubEnvironment(), NullLogger<ReportPdfService>.Instance);
        var pdf = service.Generate(new ReportPdfRenderInput(
            meta, definition, data, "条件：产品编号 = A1", "user01",
            [], true, true, meta.Header, meta.TailText));

        Assert.NotNull(pdf);
        Assert.True(pdf.Length > 100, "PDF 字节数应大于 100");
        Assert.Equal(0x25, pdf[0]); // '%' (PDF 头)
    }

    [Fact]
    public void GenerateGroupedReportPdf_WritesTempFileForVerification()
    {
        var meta = new ReportPdfMeta(
            "R1", "分组测试报表", null, null, null, null, null, null, null, []);
        var definition = new ReportDefinition(
            129801, "分组测试", "PRODUCT", null,
            [],
            [new ReportColumn("PRO_NO", "料号", "nvarchar"), new ReportColumn("QTY", "数量", "float")],
            ["PRO_NO"], []);
        var rows = new List<Dictionary<string, object?>>
        {
            new() { ["PRO_NO"] = "A", ["QTY"] = 1m },
            new() { ["PRO_NO"] = "A", ["QTY"] = 2m },
            new() { ["PRO_NO"] = "B", ["QTY"] = 5m },
        };
        var data = new ReportQueryResult(rows, 3, 1, 3);
        var service = new ReportPdfService(new StubEnvironment(), NullLogger<ReportPdfService>.Instance);
        var pdf = service.Generate(new ReportPdfRenderInput(
            meta, definition, data, "", "user01",
            ["PRODUCT.PRO_NO"], true, true, null, null));
        var path = Path.Combine(Path.GetTempPath(), "eos-group-test.pdf");
        File.WriteAllBytes(path, pdf);
        Assert.True(pdf.Length > 100);
    }
}
