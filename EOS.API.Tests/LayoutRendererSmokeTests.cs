using System.Text;
using System.Runtime.CompilerServices;
using EOS.API.Data;
using EOS.API.Models;
using Microsoft.Extensions.Logging.Abstractions;
using QuestPDF.Infrastructure;
using Xunit;

namespace EOS.API.Tests;

internal static class QuestPdfTestBootstrap
{
    [ModuleInitializer]
    public static void Initialize()
    {
        QuestPDF.Settings.License = LicenseType.Community;
        // 与 Program.cs RegisterPdfFont 保持一致：测试进程也注册中文字体，
        // 否则 Layers 测量文本时使用未注册字体可能导致整层丢弃。
        var fontPath = Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "EOS.API", "Fonts", "NotoSansCJKsc-Regular.otf");
        if (File.Exists(fontPath))
            QuestPDF.Drawing.FontManager.RegisterFontWithCustomName("Noto Sans CJK SC", File.OpenRead(fontPath));
    }
}

/// <summary>
/// QuestPdfLayoutRenderer 解释层冒烟测试（ADR-010 §3 / §5 S1）：
/// 绝对定位（Layers + Offset）、table 流动渲染、字段取值、fail-closed。
/// 渲染产物落盘 logs/layout-smoke/ 供 pdfplumber 对拍脚本复核坐标。
/// </summary>
public class LayoutRendererSmokeTests
{
    private static readonly QuestPdfLayoutRenderer Renderer =
        new(NullLogger<QuestPdfLayoutRenderer>.Instance);

    private static readonly PrintData Sample = new(
        ModuleId: 1405,
        Title: "客户订单",
        HeaderCompany: "示例公司",
        HeaderCompanyEn: "DEMO COMPANY",
        HeaderText: "TEL: 0000-0000000",
        FooterText: "谢谢惠顾",
        LogoPath: null,
        TailText: "以下空白",
        MasterFields: [new PrintField("ORDER_NO", "订单号"), new PrintField("AMOUNT_TAX", "金额")],
        DetailFields: [new PrintField("PRO_NO", "料号"), new PrintField("QTY", "数量"), new PrintField("AMOUNT_TAX", "金额")],
        Master: new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["ORDER_NO"] = "DD13010001", ["ORDER_DATE"] = new DateTime(2026, 8, 31),
            ["CLIENT_ID"] = "C001", ["CLIENT_NAME"] = "深圳测试客户有限公司",
            ["AMOUNT_TAX"] = 1240.00m, ["CREATE_PERSON"] = "admin", ["CONFIRM_PERSON"] = "manager",
        },
        Details:
        [
            new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["PRO_NO"] = "HTP6-32-2", ["PRO_NAME"] = "电子元件", ["PRO_SPEC"] = "10KΩ",
                ["QTY"] = 24000, ["UNIT_ID"] = "PCS", ["PRICE"] = 0.31m, ["REBATE"] = 100, ["AMOUNT_TAX"] = 1240m,
            },
        ],
        ClientProfile: new ClientPrintProfile(
            "深圳测试客户有限公司", "深圳测试客户有限公司", "SHENZHEN TEST CLIENT",
            "深圳市南山区科技园路1号", null, "0755-12345678", "0755-87654321", "张三", null, false));

    private const string MinimalLayout = """
        {
          "schemaVersion": 1,
          "kind": "document",
          "page": { "size": "A4", "orientation": "portrait",
                    "margin": { "top": 11.29, "right": 11.29, "bottom": 11.29, "left": 11.29 } },
          "sections": {
            "header": { "height": 26, "elements": [
              { "id": "h1", "type": "text", "x": 0, "y": 0.71, "w": 187.4, "h": 7.2,
                "content": "{{SYS.HEADER_COMPANY}}", "style": { "fontSize": 14, "bold": true, "align": "center" } },
              { "id": "h2", "type": "text", "x": 0, "y": 7.94, "w": 187.4, "h": 4.7,
                "content": "{{SYS.TITLE}}", "style": { "fontSize": 12, "semiBold": true, "align": "center" } },
              { "id": "h3", "type": "line", "x": 0, "y": 25.2, "w": 187.4, "h": 0.18, "style": { "lineWidth": 0.5 } }
            ] },
            "content": { "elements": [
              { "id": "c1", "type": "field", "x": 0, "y": 1.0, "w": 100, "h": 4.1,
                "field": "MASTER.ORDER_NO", "style": { "fontSize": 9 } },
              { "id": "c2", "type": "table", "x": 0, "y": 12, "w": 187.4, "h": 0,
                "dataSource": "details",
                "columns": [
                  { "field": "DETAILS.PRO_NO", "label": "料号", "width": 62.5 },
                  { "field": "DETAILS.QTY", "label": "数量", "width": 62.5, "align": "right", "format": "#,##0.######" },
                  { "field": "DETAILS.AMOUNT_TAX", "label": "金额", "width": 62.4, "align": "right", "format": "#,##0.00" }
                ],
                "showHeader": true, "repeatHeaderOnPageBreak": true,
                "totalsLabel": "价税合计", "totalsField": "MASTER.AMOUNT_TAX" }
            ] },
            "footer": { "height": 14, "elements": [
              { "id": "f1", "type": "text", "x": 0, "y": 9.9, "w": 120, "h": 4.1,
                "content": "{{SYS.FOOTER_TEXT}}", "style": { "fontSize": 8 } },
              { "id": "f2", "type": "text", "x": 120, "y": 9.9, "w": 67.4, "h": 4.1,
                "content": "列印人：{{SYS.PRINT_PERSON}}　第 {{SYS.PAGE_NUMBER}} / {{SYS.TOTAL_PAGES}} 页",
                "style": { "fontSize": 8, "align": "right" } }
            ] }
          }
        }
        """;

    [Fact]
    public void Render_MinimalLayout_ProducesPdf()
    {
        var pdf = Renderer.Render(Sample, MinimalLayout, new LayoutRenderContext("admin"));
        Assert.True(pdf.Length > 1000, "PDF 字节过小");
        Assert.StartsWith("%PDF", Encoding.ASCII.GetString(pdf[..4]));

        // 从当前目录向上定位仓库根（EOS.API 目录存在处），避免产物落入用户目录
        var repoRoot = FindRepoRoot(Environment.CurrentDirectory) ?? Environment.CurrentDirectory;
        var outDir = Path.Combine(repoRoot, "logs", "layout-smoke");
        Directory.CreateDirectory(outDir);
        File.WriteAllBytes(Path.Combine(outDir, "smoke-minimal.pdf"), pdf);
    }

    private static string? FindRepoRoot(string start)
    {
        var dir = new DirectoryInfo(start);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "EOS.API"))) return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }

    [Fact]
    public void Render_UnknownField_RendersEmptyAndDoesNotThrow()
    {
        var layout = MinimalLayout.Replace("MASTER.ORDER_NO", "MASTER.NOT_A_FIELD", StringComparison.Ordinal);
        var pdf = Renderer.Render(Sample, layout, new LayoutRenderContext("admin"));
        Assert.StartsWith("%PDF", Encoding.ASCII.GetString(pdf[..4]));
    }

    [Fact]
    public void Render_MalformedJson_Throws()
    {
        Assert.Throws<LayoutInvalidException>(() => Renderer.Render(Sample, "{ not json"));
    }

    [Fact]
    public void Render_UnsupportedSchemaVersion_Throws()
    {
        var layout = MinimalLayout.Replace("\"schemaVersion\": 1", "\"schemaVersion\": 2", StringComparison.Ordinal);
        Assert.Throws<LayoutInvalidException>(() => Renderer.Render(Sample, layout));
    }
}
