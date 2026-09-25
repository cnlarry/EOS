using EOS.API.Data;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 定义内容比较器的判据：只忽略**运行期证明看不见**的差异，其余一律算差异。
///
/// 每条"判等价"都配一条"判不等价"——只有正例的话，把判据放宽到"什么都能复用"也会全绿，
/// 而那正是最坏的结果：真改了配置却复用旧版本，对拍证据看着还新鲜、其实已经不对应当前行为。
/// </summary>
public class WorkbenchDefinitionContentComparerTests
{
    /// <summary>翻译期随行带出的空公式行：算子、目标表、目标列皆空。</summary>
    private const string PlaceholderOp =
        """{"opSeq":1,"targetTable":"","targetField":"","opCode":"","sourceScope":"MASTER","sourceTable":"","sourceField":"","sourceAgg":null,"sourceConstant":"","sourceTerms":"","match":"","condition":"","remark":""}""";

    private static string Definition(string actions) => $$"""{"moduleId":1406,"businessActions":[{{actions}}]}""";

    private static string ServiceActionWithPlaceholderOp() => Definition(
        $$"""{"seq":11,"eventCode":"APPROVE_EFFECT","effectKey":"inventory-move","ops":[{{PlaceholderOp}}]}""");

    private static string ServiceActionWithoutOps() => Definition(
        """{"seq":11,"eventCode":"APPROVE_EFFECT","effectKey":"inventory-move","ops":[]}""");

    [Fact]
    public void 只差一条占位公式行判等价_但两者字节不同()
    {
        var withRow = ServiceActionWithPlaceholderOp();
        var withoutRow = ServiceActionWithoutOps();

        Assert.NotEqual(withRow, withoutRow);
        Assert.True(WorkbenchDefinitionContentComparer.AreEquivalent(withRow, withoutRow));
    }

    [Fact]
    public void 多出一条真公式行判不等价()
    {
        var withRealRow = Definition(
            $$"""{"seq":11,"eventCode":"APPROVE_EFFECT","effectKey":"inventory-move","ops":[{{PlaceholderOp}},{"opSeq":2,"targetTable":"PRODUCT","targetField":"QTY","opCode":"ACCUM"}]}""");

        Assert.False(WorkbenchDefinitionContentComparer.AreEquivalent(withRealRow, ServiceActionWithPlaceholderOp()));
    }

    [Fact]
    public void 白名单字段的空串与缺省判等价()
    {
        var emptySpelling = Definition(
            """
            {"seq":11,"eventCode":"APPROVE_EFFECT","effectKey":"inventory-move","effectName":"","failMode":"",
             "condition":"","params":"","reverse":"",
             "ops":[{"opSeq":1,"targetTable":"PRODUCT","targetField":"QTY","opCode":"ACCUM","sourceScope":"DETAIL",
                     "sourceTable":"","sourceField":"","sourceAgg":"","sourceTerms":"","match":"","condition":"","remark":""}]}
            """);
        var unsetSpelling = Definition(
            """
            {"seq":11,"eventCode":"APPROVE_EFFECT","effectKey":"inventory-move","effectName":null,"failMode":null,
             "condition":null,"params":null,"reverse":null,
             "ops":[{"opSeq":1,"targetTable":"PRODUCT","targetField":"QTY","opCode":"ACCUM","sourceScope":"DETAIL",
                     "sourceTable":null,"sourceField":null,"sourceAgg":null,"sourceTerms":null,"match":null,"condition":null,"remark":null}]}
            """);

        Assert.NotEqual(emptySpelling, unsetSpelling);
        Assert.True(WorkbenchDefinitionContentComparer.AreEquivalent(emptySpelling, unsetSpelling));
    }

    [Fact]
    public void 校验规则的空串与缺省判等价()
    {
        var withRuleEmpty = """{"moduleId":1406,"businessActions":[],"validationRules":[{"seq":1,"stage":"SAVE","validationKey":"line-require","params":"","message":""}]}""";
        var withRuleUnset = """{"moduleId":1406,"businessActions":[],"validationRules":[{"seq":1,"stage":"SAVE","validationKey":"line-require","params":null,"message":null}]}""";

        Assert.NotEqual(withRuleEmpty, withRuleUnset);
        Assert.True(WorkbenchDefinitionContentComparer.AreEquivalent(withRuleEmpty, withRuleUnset));
    }

    [Fact]
    public void CONSTANT来源的空常量与缺省判不等价()
    {
        // 空串在 CONSTANT 下是真实取值（清空该列），缺省则会让运行期直接抛错，两者语义不同
        var emptyConstant = Definition(
            """{"seq":11,"eventCode":"APPROVE_EFFECT","effectKey":"inventory-move","ops":[{"opSeq":1,"targetTable":"PRODUCT","targetField":"QTY","opCode":"ASSIGN","sourceScope":"CONSTANT","sourceConstant":""}]}""");
        var unsetConstant = Definition(
            """{"seq":11,"eventCode":"APPROVE_EFFECT","effectKey":"inventory-move","ops":[{"opSeq":1,"targetTable":"PRODUCT","targetField":"QTY","opCode":"ASSIGN","sourceScope":"CONSTANT","sourceConstant":null}]}""");

        Assert.False(WorkbenchDefinitionContentComparer.AreEquivalent(emptyConstant, unsetConstant));
    }

    [Fact]
    public void 非CONSTANT来源的空常量与缺省判等价()
    {
        var emptyConstant = Definition(
            """{"seq":11,"eventCode":"APPROVE_EFFECT","effectKey":"inventory-move","ops":[{"opSeq":1,"targetTable":"PRODUCT","targetField":"QTY","opCode":"ACCUM","sourceScope":"DETAIL","sourceConstant":""}]}""");
        var unsetConstant = Definition(
            """{"seq":11,"eventCode":"APPROVE_EFFECT","effectKey":"inventory-move","ops":[{"opSeq":1,"targetTable":"PRODUCT","targetField":"QTY","opCode":"ACCUM","sourceScope":"DETAIL","sourceConstant":null}]}""");

        Assert.True(WorkbenchDefinitionContentComparer.AreEquivalent(emptyConstant, unsetConstant));
    }

    [Theory]
    [InlineData("""{"seq":11,"eventCode":"APPROVE_EFFECT","effectKey":"inventory-move","ops":[{"opSeq":1,"targetTable":"PRODUCT","targetField":"QTY","opCode":"ACCUM"}]}""",
        """{"seq":11,"eventCode":"APPROVE_EFFECT","effectKey":"inventory-move","ops":[{"opSeq":1,"targetTable":"PRODUCT","targetField":"AMOUNT","opCode":"ACCUM"}]}""")]
    [InlineData("""{"seq":11,"eventCode":"APPROVE_EFFECT","effectKey":"inventory-move","failMode":"BLOCK"}""",
        """{"seq":11,"eventCode":"APPROVE_EFFECT","effectKey":"inventory-move","failMode":"WARN"}""")]
    [InlineData("""{"seq":11,"eventCode":"APPROVE_EFFECT","effectKey":"inventory-move","enabled":true}""",
        """{"seq":11,"eventCode":"APPROVE_EFFECT","effectKey":"inventory-move","enabled":false}""")]
    [InlineData("""{"seq":11,"eventCode":"APPROVE_EFFECT","effectKey":"inventory-move","ops":[]}""",
        """{"seq":11,"eventCode":"APPROVE_EFFECT","effectKey":"set-state","ops":[]}""")]
    public void 真实业务字段改变判不等价(string before, string after)
    {
        Assert.False(WorkbenchDefinitionContentComparer.AreEquivalent(Definition(before), Definition(after)));
    }

    [Fact]
    public void 白名单之外的空串不归一()
    {
        // 算子/目标表/目标列空串不是"缺省"：加载器把它们当必填，空即配置错误，两者行为不同
        var emptyOpCode = Definition(
            """{"seq":11,"eventCode":"APPROVE_EFFECT","effectKey":"inventory-move","ops":[{"opSeq":1,"targetTable":"PRODUCT","targetField":"QTY","opCode":""}]}""");
        var missingOpCode = Definition(
            """{"seq":11,"eventCode":"APPROVE_EFFECT","effectKey":"inventory-move","ops":[{"opSeq":1,"targetTable":"PRODUCT","targetField":"QTY"}]}""");

        Assert.False(WorkbenchDefinitionContentComparer.AreEquivalent(emptyOpCode, missingOpCode));
    }

    [Fact]
    public void 无法解析的内容回退字节比较()
    {
        Assert.True(WorkbenchDefinitionContentComparer.AreEquivalent("not json", "not json"));
        Assert.False(WorkbenchDefinitionContentComparer.AreEquivalent("not json", "other"));
        // 一侧能解析、另一侧不能：宁可判不等价，也不放行
        Assert.False(WorkbenchDefinitionContentComparer.AreEquivalent(
            """{"moduleId":1406}""", "not json"));
    }

    [Fact]
    public void 规范化幂等()
    {
        Assert.True(WorkbenchDefinitionContentComparer.TryNormalize(
            ServiceActionWithPlaceholderOp(), out var once));
        Assert.True(WorkbenchDefinitionContentComparer.TryNormalize(once, out var twice));
        Assert.Equal(once, twice);
    }

    [Fact]
    public void 根不是对象时判不等价()
    {
        Assert.False(WorkbenchDefinitionContentComparer.AreEquivalent("[1]", "[2]"));
        Assert.False(WorkbenchDefinitionContentComparer.TryNormalize("[1]", out _));
    }
}
