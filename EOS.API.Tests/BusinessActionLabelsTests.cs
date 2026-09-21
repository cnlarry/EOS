using EOS.API.Data;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 业务动作目录中文标签覆盖校验：目录新增键而未补标签时，2301 配置界面会回落到英文码，
/// 本测试把这种漂移在编译期后立刻暴露。
/// </summary>
public sealed class BusinessActionLabelsTests
{
    [Fact]
    public void EveryCatalogValueHasChineseLabel()
    {
        var missing = BusinessActionLabels.MissingLabelKeys();
        Assert.True(missing.Count == 0, "缺少中文标签：" + string.Join("、", missing));
    }

    [Fact]
    public void LabelsDoNotContainUnknownKeys()
    {
        var extra = new List<string>();
        foreach (var (name, keys, labels) in BusinessActionLabels.CatalogPairs())
            foreach (var key in labels.Keys)
                if (!keys.Contains(key))
                    extra.Add($"{name}:{key}");
        Assert.True(extra.Count == 0, "标签指向不存在的目录值：" + string.Join("、", extra));
    }
}
