using EOS.API.Features.Assistant.ModelAccess;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 预设目录（<see cref="AssistantProviderCatalog"/>）的完整性检查——**不需要数据库**。
///
/// <para>
/// 为什么值得单独测：目录是"给用户带出初值"的那份数据，一个拼错的环境变量名或一个不成立的端点，
/// 要到管理员真的点了"添加供应商"才会暴露，而那时看到的只是一个表单里的怪值。把它当成一份要交付的
/// 数据来断言，比指望人工复核可靠。
/// </para>
/// </summary>
public sealed class AssistantProviderCatalogTests
{
    public static TheoryData<string> ProviderCodes()
    {
        var data = new TheoryData<string>();
        foreach (var provider in AssistantProviderCatalog.All)
        {
            data.Add(provider.Code);
        }

        return data;
    }

    [Fact]
    public void Codes_Are_Unique_And_Lowercase()
    {
        var codes = AssistantProviderCatalog.All.Select(item => item.Code).ToList();
        // CODE 会被写进库、并与目录比对，重复或大小写不一致会让"目录里有、对不上"
        Assert.Equal(codes.Count, codes.Distinct(StringComparer.Ordinal).Count());
        Assert.All(codes, code => Assert.Equal(code.ToLowerInvariant(), code));
        Assert.All(codes, code => Assert.False(string.IsNullOrWhiteSpace(code)));
    }

    [Fact]
    public void SupportedCodes_Matches_The_Catalog()
    {
        Assert.Equal(
            AssistantProviderCatalog.All.Select(item => item.Code).OrderBy(item => item, StringComparer.Ordinal),
            AssistantProviderCatalog.SupportedCodes.OrderBy(item => item, StringComparer.Ordinal));
    }

    [Theory]
    [MemberData(nameof(ProviderCodes))]
    public void Provider_Is_Usable_As_A_Preset(string code)
    {
        var provider = AssistantProviderCatalog.Find(code);
        Assert.NotNull(provider);

        Assert.False(string.IsNullOrWhiteSpace(provider.DisplayName));
        if (code == "custom")
        {
            // 自定义厂商没有已知端点，留空是刻意的：控制器会以"端点不能为空"拦下来让人填。
            // 反倒是填一个像模像样的假地址更危险——它可能被一路保存下去。
            Assert.Equal(string.Empty, provider.BaseUrl);
        }
        else
        {
            // 其余预设必须是一个真的能用 HttpClient 发出去的绝对地址
            Assert.True(Uri.TryCreate(provider.BaseUrl, UriKind.Absolute, out var uri),
                $"{code} 的端点不是绝对 URL：{provider.BaseUrl}");
            Assert.True(uri!.Scheme is "https" or "http", $"{code} 的端点是 {uri.Scheme}，只接受 http/https");
        }

        Assert.InRange(provider.TimeoutSeconds, 10, 3600);

        // 环境变量名的形状：只允许字母/数字/下划线，且不以数字开头（与控制器里的校验同一条规则）
        Assert.False(string.IsNullOrWhiteSpace(provider.SuggestedApiKeyEnvVar));
        Assert.False(char.IsDigit(provider.SuggestedApiKeyEnvVar[0]));
        Assert.All(provider.SuggestedApiKeyEnvVar, ch =>
            Assert.True((ch >= 'A' && ch <= 'Z') || (ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9') || ch == '_',
                $"{code} 的环境变量名里有非法字符：{ch}"));
    }

    [Fact]
    public void Model_Presets_Are_Usable()
    {
        foreach (var provider in AssistantProviderCatalog.All)
        {
            var codes = provider.Models.Select(model => model.ModelCode).ToList();
            Assert.Equal(codes.Count, codes.Distinct(StringComparer.Ordinal).Count());

            foreach (var model in provider.Models)
            {
                Assert.False(string.IsNullOrWhiteSpace(model.ModelCode));
                Assert.False(string.IsNullOrWhiteSpace(model.DisplayName));
                if (model.ContextWindow is { } window)
                {
                    Assert.InRange(window, 1000, 20_000_000);
                }

                if (model.MaxOutputTokens is { } maxOutput)
                {
                    Assert.InRange(maxOutput, 1, 200_000);
                    // 输出上限不可能超过窗口——算错了会直接带着非法参数去请求
                    if (model.ContextWindow is { } context)
                    {
                        Assert.True(maxOutput <= context,
                            $"{model.ModelCode} 的最大输出 {maxOutput} 超过了上下文窗口 {context}");
                    }
                }
            }
        }
    }

    [Fact]
    public void Unknown_Codes_Are_Rejected()
    {
        Assert.False(AssistantProviderCatalog.IsSupported("不存在的供应商"));
        Assert.False(AssistantProviderCatalog.IsSupported(null));
        Assert.False(AssistantProviderCatalog.IsSupported("  "));
        Assert.Null(AssistantProviderCatalog.Find("不存在的供应商"));
        // 大小写不该成为障碍
        Assert.True(AssistantProviderCatalog.IsSupported("DeepSeek"));
    }

    [Fact]
    public void Custom_Preset_Has_No_Models_But_Is_Still_Selectable()
    {
        var custom = AssistantProviderCatalog.Find("custom");
        Assert.NotNull(custom);
        // 自定义厂商没有已知模型清单，但它必须可选——否则接入线协议相同的新厂商会被目录挡住
        Assert.Empty(custom.Models);
    }
}
