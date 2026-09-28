using System.Reflection;
using System.Text.Json;
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
/// 报表清单 PDF 冒烟：验证"报表打印走版式解释层"这条链路在目标框架可用、中文字体已注册、
/// 输出为合法 PDF 字节；并验证**内置的列表型版式资产本身**过得了字段白名单校验。
///
/// <para>
/// 用真资产而不是测试里现编一份假版式：假版式能渲染，不代表线上那份能渲染；
/// 而"资产过不了白名单"这种事如果在保存链路之外没人校验，就会一路等到用户点打印才炸。
/// </para>
/// </summary>
public class ReportListPdfSmokeTests
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

    static ReportListPdfSmokeTests()
    {
        QuestPDF.Settings.License = LicenseType.Community;
        var fontPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..",
            "EOS.API", "Fonts", "NotoSansCJKsc-Regular.otf"));
        if (File.Exists(fontPath))
            QuestPDF.Drawing.FontManager.RegisterFontWithCustomName(PdfLayout.FontFamily, File.OpenRead(fontPath));
    }

    /// <summary>
    /// 内置版式资产所在目录。仓库根取自构建期写入的程序集元数据（csproj 的 RepoRoot），
    /// 不靠"从输出目录往上找标记文件"——测试输出目录一旦放到仓外（-o %TEMP%），那种找法就会静默落空。
    /// </summary>
    private static string GenericAssetDirectory()
    {
        var repoRoot = typeof(ReportListPdfSmokeTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(item => item.Key == "RepoRoot")?.Value;
        Assert.False(string.IsNullOrWhiteSpace(repoRoot), "缺少构建期元数据 RepoRoot。");
        return Path.Combine(repoRoot!, "EOS.API", "ReportFormats", "_generic");
    }

    private static string GenericListLayoutJson()
    {
        var path = Path.Combine(GenericAssetDirectory(), "layout.list.json");
        Assert.True(File.Exists(path), $"缺少列表型版式资产：{path}");
        return File.ReadAllText(path);
    }

    [Fact]
    public void 内置列表型版式通过字段白名单校验()
    {
        // R6 的退出条件之一：字段白名单校验器覆盖 list 型。
        // 校验器不覆盖 list，等于"报表版式可以随便写引用"——写错了只有打印时才看得见。
        var formatPath = Path.Combine(GenericAssetDirectory(), "format.json");
        Assert.True(File.Exists(formatPath), $"缺少格式定义：{formatPath}");
        var format = JsonSerializer.Deserialize<ReportFormatDefinition>(
            File.ReadAllText(formatPath), AssetJsonOptions);
        Assert.NotNull(format);

        var result = new ReportFormatValidator().Validate(format!, GenericListLayoutJson(), maxElements: 400);
        Assert.True(result.Ok, "内置列表型版式未通过校验：" + string.Join("；", result.Messages));
    }

    [Fact]
    public void 流动分区与多页模板并存时被校验拒绝()
    {
        // 多页模板走动态部件，动态部件要求内容单页装得下；流动分区的高度由内容决定。
        // 两者并存会在渲染时抛 DocumentLayoutException——拦在保存校验，别等人点打印才炸。
        var formatPath = Path.Combine(GenericAssetDirectory(), "format.json");
        var format = JsonSerializer.Deserialize<ReportFormatDefinition>(
            File.ReadAllText(formatPath), AssetJsonOptions);
        Assert.NotNull(format);

        var node = System.Text.Json.Nodes.JsonNode.Parse(GenericListLayoutJson())!;
        node["pageTemplates"] = System.Text.Json.Nodes.JsonNode.Parse(
            """{"first":{"header":{"flow":true,"elements":[{"id":"t","type":"text","x":0,"y":0,"w":50,"h":5,"content":"{{REPORT.TITLE}}"}]}}}""");

        var result = new ReportFormatValidator().Validate(format!, node.ToJsonString(), maxElements: 400);

        Assert.False(result.Ok, "流动分区与多页模板并存必须被拒绝。");
        Assert.Contains(result.Messages, message => message.Contains("流动分区"));
    }

    [Fact]
    public void 报表清单渲染_产出合法PDF字节()
    {
        var meta = new ReportPdfMeta(
            "R1", "测试报表", null, null, "页脚文字", "ISO9001",
            null,
            new ReportHeaderOption("H1", "默认页头", "某某公司", null, "页头文字", null, null),
            "表尾文字", []);
        var definition = new ReportDefinition(
            129801, "产品资料明细", "PRODUCT", null,
            [],
            [new ReportColumn("PRO_NO", "产品编号", "nvarchar", null, IsCost: false, IsSecrecy: false),
             new ReportColumn("PRO_NAME", "品名", "nvarchar", null, IsCost: false, IsSecrecy: false)],
            ["PRO_NO"], []);
        var data = new ReportQueryResult(
            [new Dictionary<string, object?> { ["PRO_NO"] = "A1", ["PRO_NAME"] = "测试产品" }],
            1, 1, 1);

        var composed = ReportListPdfComposer.Compose(
            new ReportPdfRenderInput(meta, definition, data, "条件：产品编号 = A1", "user01",
                [], true, true, meta.Header, meta.TailText),
            new StubEnvironment());
        var renderer = new QuestPdfLayoutRenderer(NullLogger<QuestPdfLayoutRenderer>.Instance);
        var pdf = renderer.RenderReportList(composed.Data, GenericListLayoutJson(), composed.Context);

        Assert.NotNull(pdf);
        Assert.True(pdf.Length > 100, "PDF 字节数应大于 100");
        Assert.Equal(0x25, pdf[0]); // '%'（PDF 头）
    }

    [Fact]
    public void 分组小计渲染_产出合法PDF字节()
    {
        var meta = new ReportPdfMeta("R1", "分组测试报表", null, null, null, null, null, null, null, []);
        var definition = new ReportDefinition(
            129801, "分组测试", "PRODUCT", null,
            [],
            [new ReportColumn("PRO_NO", "料号", "nvarchar", null, IsCost: false, IsSecrecy: false),
             new ReportColumn("QTY", "数量", "float", null, IsCost: false, IsSecrecy: false)],
            ["PRO_NO"], []);
        var rows = new List<Dictionary<string, object?>>
        {
            new() { ["PRO_NO"] = "A", ["QTY"] = 1m },
            new() { ["PRO_NO"] = "A", ["QTY"] = 2m },
            new() { ["PRO_NO"] = "B", ["QTY"] = 5m },
        };
        var data = new ReportQueryResult(rows, 3, 1, 3);

        var composed = ReportListPdfComposer.Compose(
            new ReportPdfRenderInput(meta, definition, data, "", "user01",
                ["PRODUCT.PRO_NO"], true, true, null, null),
            new StubEnvironment());
        var renderer = new QuestPdfLayoutRenderer(NullLogger<QuestPdfLayoutRenderer>.Instance);
        var pdf = renderer.RenderReportList(composed.Data, GenericListLayoutJson(), composed.Context);

        Assert.True(pdf.Length > 100);
        File.WriteAllBytes(Path.Combine(Path.GetTempPath(), "eos-report-list-group.pdf"), pdf);
    }
}
