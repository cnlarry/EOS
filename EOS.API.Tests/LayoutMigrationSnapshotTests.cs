using System.Text.Json;
using EOS.API.Data;
using EOS.API.Models;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 迁移对拍快照（ADR-010 决策 6 / §5 S1 ④）：
/// 同一 sample.json 数据分别走 C# 命令式版式（DocumentPdfService，基线）与
/// layout.json 解释层（QuestPdfLayoutRenderer），产物落盘 logs/layout-migration/，
/// 由 scripts/check-layout-migration.py 做逐元素对拍（文本内容 + 坐标）。
/// 对拍通过后 C# 版式退役，本测试随之退役（不持续运行）。
/// </summary>
public class LayoutMigrationSnapshotTests
{
    private static readonly QuestPdfLayoutRenderer Renderer =
        new(NullLogger<QuestPdfLayoutRenderer>.Instance);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    [Fact]
    public void Generate_MigrationSnapshots_ForAllModules()
    {
        var repoRoot = ResolveRepoRoot();
        Assert.NotNull(repoRoot);
        var formatsRoot = Path.Combine(repoRoot, "EOS.API", "ReportFormats");
        var outRoot = Path.Combine(repoRoot, "logs", "layout-migration");

        var environment = new FakeWebHostEnvironment(Path.Combine(repoRoot, "EOS.API"));
        var documentService = new DocumentPdfService(environment, NullLogger<DocumentPdfService>.Instance);

        var moduleIds = LayoutFormatPackagesTests.ModuleIds;
        foreach (var moduleId in moduleIds)
        {
            var dir = Path.Combine(formatsRoot, moduleId);
            var sampleJson = File.ReadAllText(Path.Combine(dir, "sample.json"));
            var layoutJson = File.ReadAllText(Path.Combine(dir, "layout.json"));
            var data = LayoutFormatPackagesTests.BuildPrintData(moduleId, sampleJson);

            var header = new ReportHeaderOption(
                "H1", "默认", data.HeaderCompany ?? string.Empty,
                data.HeaderCompanyEn, data.HeaderText, data.LogoPath, null);
            var legacy = documentService.Generate(
                data, header, data.TailText, showRemark: true, "admin");
            var interpreter = Renderer.Render(data, layoutJson, new LayoutRenderContext("admin"));

            Assert.True(legacy.Length > 500, $"{moduleId} C# 版式 PDF 过小");
            Assert.True(interpreter.Length > 500, $"{moduleId} 解释层 PDF 过小");

            var outDir = Path.Combine(outRoot, moduleId);
            Directory.CreateDirectory(outDir);
            File.WriteAllBytes(Path.Combine(outDir, "legacy.pdf"), legacy);
            File.WriteAllBytes(Path.Combine(outDir, "interpreter.pdf"), interpreter);
        }
    }

    [Fact]
    public void Generate_MultiPageSnapshot_TableHeaderRepeats()
    {
        var repoRoot = ResolveRepoRoot();
        Assert.NotNull(repoRoot);
        var formatsRoot = Path.Combine(repoRoot, "EOS.API", "ReportFormats");
        var outDir = Path.Combine(repoRoot, "logs", "layout-migration", "1405-multi");

        var environment = new FakeWebHostEnvironment(Path.Combine(repoRoot, "EOS.API"));
        var documentService = new DocumentPdfService(environment, NullLogger<DocumentPdfService>.Instance);
        var sampleJson = File.ReadAllText(Path.Combine(formatsRoot, "1405", "sample.json"));
        var layoutJson = File.ReadAllText(Path.Combine(formatsRoot, "1405", "layout.json"));
        var data = LayoutFormatPackagesTests.BuildPrintData("1405", sampleJson);

        // 120 行明细：验证自动分页 + 表头重复（C# 基线 vs 解释层均须分页且表头重复）
        var details = data.Details.ToList();
        for (var i = 1; i < 120; i++)
        {
            details.Add(new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["PRO_NO"] = $"HTP6-32-{i + 1}",
                ["PRO_NAME"] = "电子元件",
                ["PRO_SPEC"] = "10KΩ",
                ["QTY"] = 1000 + i,
                ["UNIT_ID"] = "PCS",
                ["PRICE"] = 0.31m,
                ["REBATE"] = 100,
                ["AMOUNT_TAX"] = 310.00m + i,
            });
        }
        data = data with { Details = details };

        var header = new ReportHeaderOption(
            "H1", "默认", data.HeaderCompany ?? string.Empty,
            data.HeaderCompanyEn, data.HeaderText, data.LogoPath, null);
        var legacy = documentService.Generate(data, header, data.TailText, showRemark: true, "admin");
        var interpreter = Renderer.Render(data, layoutJson, new LayoutRenderContext("admin"));

        Directory.CreateDirectory(outDir);
        File.WriteAllBytes(Path.Combine(outDir, "legacy.pdf"), legacy);
        File.WriteAllBytes(Path.Combine(outDir, "interpreter.pdf"), interpreter);
    }

    private static string? ResolveRepoRoot()
    {
        var metadata = typeof(LayoutMigrationSnapshotTests).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
            .OfType<System.Reflection.AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == "RepoRoot");
        if (metadata?.Value is { Length: > 0 } repoRoot
            && Directory.Exists(Path.Combine(repoRoot, "EOS.API")))
            return Path.GetFullPath(repoRoot);
        var dir = new DirectoryInfo(Environment.CurrentDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "EOS.API"))) return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }

    private sealed class FakeWebHostEnvironment(
        string contentRoot) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "EOS.API";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = string.Empty;
        public string EnvironmentName { get; set; } = "Test";
        public string ContentRootPath { get; set; } = contentRoot;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
