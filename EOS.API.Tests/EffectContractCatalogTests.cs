using EOS.API.Data;
using EOS.API.Data.Effects;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 契约下沉的纯逻辑测试：反向 kind 兼容矩阵、参数深 Schema、效果键与反向 kind 的说明覆盖。
/// 三者都是"发布期/界面要拦下错配置"的地基——判据错了，要么把能用的配置判死，
/// 要么把会炸的组合放过去。
/// </summary>
public sealed class EffectContractCatalogTests
{
    [Theory]
    [InlineData("detail-field-sync", "restore-previous", true)]
    [InlineData("detail-field-sync", "auto-reverse", false)]
    [InlineData("car-filloil-sync", "restore-previous", true)]
    [InlineData("car-filloil-sync", "none", false)]
    [InlineData("link-stamp", "clear-refs", true)]
    [InlineData("link-stamp", "clear-refs-unfinish", true)]
    [InlineData("link-stamp", "no-reverse", true)]
    [InlineData("link-stamp", "none", true)]
    [InlineData("link-stamp", "auto-reverse", false)]
    [InlineData("order-change-apply", "none", true)]
    [InlineData("order-change-apply", "no-reverse", true)]
    [InlineData("order-change-apply", "auto-reverse", false)]
    [InlineData("payment-date-calc", "clear-on-deapprove", true)]
    [InlineData("payment-date-calc", "no-reverse", true)]
    [InlineData("payment-date-calc", "recompute", false)]
    [InlineData("half-stock-move", "reverse-flow", true)]
    [InlineData("hr-usage-sync", "auto-reverse", true)]
    [InlineData("mould-batch-apply", "auto-reverse", true)]
    [InlineData("employee-contract-sync", "recompute-excluding-self", true)]
    [InlineData("employee-dimission-sync", "restore-active", true)]
    [InlineData("sample-edition-bump", "none", true)]
    [InlineData("client-price-sync", "restore-old-price", true)]
    [InlineData("supplier-price-sync", "no-reverse", true)]
    [InlineData("supplier-price-sync", "auto-reverse", false)]
    public void IsSupported_按Handler实际接受的集合判定(string effectKey, string kind, bool expected)
    {
        Assert.Equal(expected, EffectReverseCompatibility.IsSupported(effectKey, kind, hasFormulaRows: false));
    }

    [Fact]
    public void IsSupported_公式行路径不接受无法由单条公式行表达的_kind()
    {
        Assert.False(EffectReverseCompatibility.IsSupported("field-accumulate", "clear-on-deapprove", hasFormulaRows: true));
        Assert.True(EffectReverseCompatibility.IsSupported("field-accumulate", "auto-reverse", hasFormulaRows: true));
        // 同一个键在没有公式行时走服务处理器，约束不同——判据必须按执行路径分流。
        Assert.True(EffectReverseCompatibility.IsSupported("field-accumulate", "clear-on-deapprove", hasFormulaRows: false));
    }

    [Fact]
    public void AllowedKinds_不受约束的键返回全集_受约束的键只返回子集()
    {
        var all = EffectStructSchemas.AllReverseKinds();
        Assert.Equal(all.Count, EffectReverseCompatibility.AllowedKinds("inventory-move", hasFormulaRows: false).Count);
        Assert.Equal(all.Count, EffectReverseCompatibility.AllowedKinds("set-state", hasFormulaRows: false).Count);

        var detail = EffectReverseCompatibility.AllowedKinds("detail-field-sync", hasFormulaRows: false);
        Assert.Equal(["restore-previous"], detail);
    }

    [Fact]
    public void AllowedKinds_的子集必须都在反向_kind_闭集内()
    {
        var closed = EffectStructSchemas.AllReverseKinds().ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var key in EffectReverseCompatibility.ConstrainedKeys())
        {
            var allowed = EffectReverseCompatibility.AllowedKinds(key, hasFormulaRows: false);
            Assert.NotEmpty(allowed);
            foreach (var kind in allowed)
            {
                Assert.Contains(kind, closed);
            }
        }
    }

    [Fact]
    public void 参数描述的根键不得超出_Schema_登记的根键()
    {
        foreach (var effectKey in EffectParamDescriptors.DescribedKeys())
        {
            Assert.True(EffectStructSchemas.TryGetParamRootKeys(effectKey, out var rootKeys),
                $"效果键 {effectKey} 有参数描述却没有登记参数根键。");
            foreach (var field in EffectParamDescriptors.For(effectKey))
            {
                Assert.True(rootKeys.Contains(field.Name, StringComparer.OrdinalIgnoreCase),
                    $"{effectKey}.{field.Name} 不在已登记的参数根键内。");
                Assert.Contains(field.Type, EffectParamDescriptors.Types);
                Assert.False(string.IsNullOrWhiteSpace(field.Description), $"{effectKey}.{field.Name} 说明为空。");
            }
        }
    }

    [Fact]
    public void 带枚举的参数描述的默认值必须落在枚举内()
    {
        foreach (var effectKey in EffectParamDescriptors.DescribedKeys())
        {
            foreach (var field in EffectParamDescriptors.For(effectKey))
            {
                if (field.EnumValues is null || field.Default is null)
                {
                    continue;
                }
                Assert.Contains(field.Default, field.EnumValues);
            }
        }
    }

    [Fact]
    public void 每个效果键与反向_kind_都有非空说明()
    {
        Assert.Empty(BusinessActionLabels.MissingDescriptionKeys());
    }

    [Fact]
    public void 反向兼容矩阵的每个受约束键都能对上一个已登记的效果键()
    {
        foreach (var key in EffectReverseCompatibility.ConstrainedKeys())
        {
            Assert.True(BusinessActionCatalog.EffectKeys.Contains(key), $"受约束的反向键 {key} 不在效果目录内。");
        }
    }
}
