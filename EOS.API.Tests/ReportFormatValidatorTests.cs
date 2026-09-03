using System.Text.Json;
using EOS.API.Data;
using EOS.API.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 字段白名单校验器单测：
/// 24 个内置格式包必须全过；越权字段 / 非法坐标 / 未知类型 / 未知系统值必须被拦截。
/// </summary>
public class ReportFormatValidatorTests
{
    private static readonly ReportFormatValidator Validator = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private static readonly Lazy<string> LayoutJson = new(() =>
    {
        var root = ResolveFormatsRoot()!;
        return File.ReadAllText(Path.Combine(root, "1405", "layout.json"));
    });

    private static readonly Lazy<ReportFormatDefinition> Format = new(() =>
    {
        var root = ResolveFormatsRoot()!;
        return JsonSerializer.Deserialize<ReportFormatDefinition>(
            File.ReadAllText(Path.Combine(root, "1405", "format.json")), JsonOptions)!;
    });

    [Fact]
    public void All_24_BuiltinPackages_PassValidation()
    {
        var root = ResolveFormatsRoot();
        Assert.NotNull(root);
        foreach (var moduleId in LayoutFormatPackagesTests.ModuleIds)
        {
            var dir = Path.Combine(root, moduleId);
            var format = JsonSerializer.Deserialize<ReportFormatDefinition>(
                File.ReadAllText(Path.Combine(dir, "format.json")), JsonOptions)!;
            var layoutJson = File.ReadAllText(Path.Combine(dir, "layout.json"));
            var errors = Validator.Validate(format, layoutJson);
            Assert.True(errors.Count == 0, $"{moduleId} 应通过校验：{string.Join("; ", errors)}");
        }
    }

    [Fact]
    public void UnauthorizedMasterField_IsRejected()
    {
        var layoutJson = LayoutJson.Value.Replace(
            "{{MASTER.ORDER_NO}}", "{{MASTER.SALARY}}", StringComparison.Ordinal);
        var errors = Validator.Validate(Format.Value, layoutJson);
        Assert.Contains(errors, error => error.Contains("MASTER.SALARY") && error.Contains("白名单"));
    }

    [Fact]
    public void UnauthorizedDetailField_IsRejected()
    {
        var layoutJson = LayoutJson.Value.Replace(
            "DETAILS.PRO_NO", "DETAILS.COST_PRICE", StringComparison.Ordinal);
        var errors = Validator.Validate(Format.Value, layoutJson);
        Assert.Contains(errors, error => error.Contains("DETAILS.COST_PRICE") && error.Contains("白名单"));
    }

    [Fact]
    public void UnknownSystemValue_IsRejected()
    {
        var layoutJson = LayoutJson.Value.Replace(
            "{{SYS.HEADER_COMPANY}}", "{{SYS.INTERNAL_SECRET}}", StringComparison.Ordinal);
        var errors = Validator.Validate(Format.Value, layoutJson);
        Assert.Contains(errors, error => error.Contains("SYS.INTERNAL_SECRET") && error.Contains("白名单"));
    }

    [Fact]
    public void ElementOutsideContentArea_IsRejected()
    {
        var layoutJson = LayoutJson.Value.Replace(
            "\"w\": 187.4", "\"w\": 190", StringComparison.Ordinal);
        var errors = Validator.Validate(Format.Value, layoutJson);
        Assert.Contains(errors, error => error.Contains("超出内容区宽度"));
    }

    [Fact]
    public void UnknownElementType_IsRejected()
    {
        var layoutJson = LayoutJson.Value.Replace(
            "\"type\": \"text\"", "\"type\": \"chart\"", StringComparison.Ordinal);
        var errors = Validator.Validate(Format.Value, layoutJson);
        Assert.Contains(errors, error => error.Contains("未知元素类型"));
    }

    [Fact]
    public void MalformedJson_IsRejected()
    {
        var errors = Validator.Validate(Format.Value, "{ not json");
        Assert.NotEmpty(errors);
        Assert.Contains(errors, error => error.Contains("解析失败"));
    }

    private static string? ResolveFormatsRoot()
    {
        var metadata = typeof(ReportFormatValidatorTests).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
            .OfType<System.Reflection.AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == "RepoRoot");
        if (metadata?.Value is { Length: > 0 } repoRoot)
        {
            var candidate = Path.Combine(repoRoot, "EOS.API", "ReportFormats");
            if (Directory.Exists(candidate)) return candidate;
        }
        return null;
    }
}
