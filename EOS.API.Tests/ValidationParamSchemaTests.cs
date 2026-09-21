using EOS.API.Data;
using EOS.API.Data.ValidationRules;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 校验模板参数根键暴露面：2301 的结构化参数编辑器按这份白名单渲染，
/// 模板登记了根键却取不到（或取到空集）时界面会退化成"该模板不允许配置参数"，
/// 与保存期校验口径不一致，因此在编译期后立刻钉住。
/// </summary>
public sealed class ValidationParamSchemaTests
{
    [Fact]
    public void EveryValidationKeyExposesRootKeys()
    {
        var missing = new List<string>();
        foreach (var key in BusinessActionCatalog.ValidationKeys)
            if (ValidationRuleRegistry.ParamRootKeys(key).Count == 0)
                missing.Add(key);
        Assert.True(missing.Count == 0, "以下校验模板没有暴露参数根键：" + string.Join("、", missing));
    }

    [Fact]
    public void UnknownValidationKeyYieldsEmptySet()
    {
        Assert.Empty(ValidationRuleRegistry.ParamRootKeys("not-a-template"));
        Assert.Empty(ValidationRuleRegistry.ParamRootKeys(null));
    }

    [Fact]
    public void SharedWhenKeyIsAlwaysAvailable()
    {
        foreach (var key in BusinessActionCatalog.ValidationKeys)
            Assert.Contains("when", ValidationRuleRegistry.ParamRootKeys(key));
    }
}
