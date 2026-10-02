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
                    // 上界与库里的检查约束**同源**：厂商现在的最大输出已经到 393216，
                    // 早期 200000 的上界会把预设值直接挡在库外（迁移 307 已放宽到 1000000）
                    Assert.InRange(maxOutput, 1, 1_000_000);
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
    public void Model_Presets_Declare_Kind_And_Dimension_Consistently()
    {
        foreach (var model in AssistantProviderCatalog.All.SelectMany(provider => provider.Models))
        {
            if (model.Kind == AssistantModelKind.Embedding)
            {
                // 嵌入模型不给维度，等于把"尺寸对不对"推迟到运行时才暴露
                Assert.NotNull(model.Dimension);
                Assert.InRange(model.Dimension!.Value, 1, 20_000);
                // 嵌入端点没有工具调用这回事：声明成支持会把 tools 带到不认识的请求里
                Assert.False(model.SupportsTools);
            }
            else
            {
                // 对话模型带维度是语义错位：维度是向量的属性，不是对话的属性
                Assert.Null(model.Dimension);
            }
        }
    }

    [Fact]
    public void Embedding_Capability_Matches_What_The_Presets_Offer()
    {
        foreach (var provider in AssistantProviderCatalog.All)
        {
            if (provider.Embedding != AssistantCapability.Unsupported)
            {
                continue;
            }

            // 核实过"没有嵌入端点"的那几家，预设里就不能摆嵌入模型：
            // 摆上等于给一个必定 404 的端点做广告
            Assert.DoesNotContain(provider.Models, model => model.Kind == AssistantModelKind.Embedding);
        }

        // 候选清单只列"支持或未核实"的——明知不支持的，不该出现在"选嵌入"的地方
        Assert.DoesNotContain(
            AssistantProviderCatalog.EmbeddingCandidates,
            provider => provider.Embedding == AssistantCapability.Unsupported);
        Assert.Contains(AssistantProviderCatalog.EmbeddingCandidates, provider => provider.Code == "dashscope");
        Assert.Contains(AssistantProviderCatalog.EmbeddingCandidates, provider => provider.Code == "zhipu");
    }

    [Fact]
    public void Verified_Vendor_Facts_Are_Pinned()
    {
        // 下面几条是**核过官方文档的事实**（不是印象）。钉在这里是为了让"顺手把认证头统一成 Bearer"
        // 这类改动当场撞红——那会让那家供应商再也接不上（它用的是 api-key 头）。
        var xiaomi = AssistantProviderCatalog.Find("xiaomi");
        Assert.NotNull(xiaomi);
        Assert.Equal("https://api.xiaomimimo.com/v1", xiaomi.BaseUrl);
        Assert.Equal(AssistantAuthStyle.ApiKeyHeader, xiaomi.AuthStyle);
        Assert.Equal(AssistantAuthStyle.ApiKeyHeader, AssistantProviderCatalog.AuthStyleOf("XIAOMI"));

        // 核过"没有嵌入端点"的三家：不得出现在嵌入候选里
        foreach (var code in new[] { "deepseek", "moonshot", "xiaomi" })
        {
            Assert.Equal(AssistantCapability.Unsupported, AssistantProviderCatalog.Find(code)!.Embedding);
        }

        // 目录外的 CODE 按主流形态处理：不抛也不拒绝（与协议解析同一口径）
        Assert.Equal(AssistantAuthStyle.Bearer, AssistantProviderCatalog.AuthStyleOf("unknown-vendor"));
        Assert.Equal(AssistantAuthStyle.Bearer, AssistantProviderCatalog.AuthStyleOf(null));
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
