using EOS.API.Data;
using EOS.API.Data.Effects;
using EOS.API.Models;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 「带真公式行 ⇒ 走公式解释器；只有占位公式行 ⇒ 走服务处理器」这条分流的回归用例。
///
/// 两条路径接受的反向 kind 不同：公式路径**禁用** `clear-on-deapprove`，而服务型键
/// `payment-date-calc` 恰恰**只认**它（解批时清空预计收 / 付款日）。占位行（算子 / 目标表 /
/// 目标字段全空，由配置迁移写入、运行时会被跳过）若被算作公式行，服务型动作就会被按公式规则
/// 拒掉——配置是对的，却报出"解批时会在真单据上抛错"的结论。
/// </summary>
public sealed class EffectReverseKindPathTests
{
    private const string ServiceKey = "payment-date-calc";
    private const string ClearOnDeapprove = "clear-on-deapprove";

    private static BusinessActionOpDto Op(string opCode, string targetTable, string targetField)
        => new(OpSeq: 1, TargetTable: targetTable, TargetField: targetField, OpCode: opCode, SourceScope: "master");

    [Fact]
    public void 只有占位公式行时按服务路径判_清空型反向合法()
    {
        var ops = new[] { Op("", "", "") };

        Assert.False(ModuleBusinessConfigValidator.HasFormulaRows(ops));
        Assert.True(EffectReverseCompatibility.IsSupported(ServiceKey, ClearOnDeapprove, hasFormulaRows: false));
    }

    [Fact]
    public void 带真公式行时按公式路径判_清空型反向被拒()
    {
        var ops = new[] { Op("", "", ""), Op("set", "COP_ACCOUNT_M", "PAY_DATE") };

        Assert.True(ModuleBusinessConfigValidator.HasFormulaRows(ops));
        Assert.False(EffectReverseCompatibility.IsSupported(ServiceKey, ClearOnDeapprove, hasFormulaRows: true));
    }

    [Theory]
    [InlineData(" ", "\t", " ")]
    public void 全空白的算子与目标列都算占位(string opCode, string targetTable, string targetField)
        => Assert.False(ModuleBusinessConfigValidator.HasFormulaRows([Op(opCode, targetTable, targetField)]));

    [Theory]
    [InlineData("set", "", "")]
    [InlineData("", "COP_ACCOUNT_M", "")]
    [InlineData("", "", "PAY_DATE")]
    public void 任一列非空即算真公式行(string opCode, string targetTable, string targetField)
        => Assert.True(ModuleBusinessConfigValidator.HasFormulaRows([Op(opCode, targetTable, targetField)]));

    [Fact]
    public void 没有公式行或没有动作时不算带公式行()
    {
        Assert.False(ModuleBusinessConfigValidator.HasFormulaRows([]));
        Assert.False(ModuleBusinessConfigValidator.HasFormulaRows(null));
    }
}
