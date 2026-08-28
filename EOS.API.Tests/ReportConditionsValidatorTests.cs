using EOS.API.Data;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 报表过滤条件设置（2205）写侧校验单测：规则与报表运行时解析镜像
/// （F_TYPE 语义、选项 DSL、数据源语句、主表字段白名单）。
/// </summary>
public class ReportConditionsValidatorTests
{
    private static readonly HashSet<(string Table, string Column)> MasterFields =
        new HashSet<(string Table, string Column)>
        {
            ("COP_ORDER_M", "ORDER_DATE"),
            ("COP_ORDER_M", "STATUS"),
        };

    // ---- 模块号 / 序号 / 类型 ----

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void InvalidModuleIds_Throw(int moduleId) =>
        Assert.Throws<ArgumentException>(() => ReportConditionsValidator.ValidateModuleId(moduleId));

    [Theory]
    [InlineData(0)]
    [InlineData(32768)]
    public void InvalidSerialNos_Throw(int serialNo) =>
        Assert.Throws<ArgumentException>(() => ReportConditionsValidator.ValidateSerialNo(serialNo));

    [Theory]
    [InlineData(1)]
    [InlineData(32767)]
    public void BoundarySerialNos_Pass(int serialNo) =>
        ReportConditionsValidator.ValidateSerialNo(serialNo);

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    public void InvalidTypes_Throw(int type) =>
        Assert.Throws<ArgumentException>(() => ReportConditionsValidator.ValidateType(type));

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void KnownTypes_Pass(int type) => ReportConditionsValidator.ValidateType(type);

    // ---- 查询字段（F_ID：主表.列 + 白名单）----

    [Fact]
    public void MasterTableFieldInWhitelist_Passes() =>
        Assert.Equal("COP_ORDER_M.STATUS", ReportConditionsValidator.ValidateField(
            " COP_ORDER_M.STATUS ", "COP_ORDER_M", MasterFields));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyField_Throws(string? field) =>
        Assert.Throws<ArgumentException>(() => ReportConditionsValidator.ValidateField(field, "COP_ORDER_M", MasterFields));

    [Fact]
    public void NonDottedField_Throws() =>
        Assert.Throws<ArgumentException>(() => ReportConditionsValidator.ValidateField("STATUS", "COP_ORDER_M", MasterFields));

    [Fact]
    public void DetailTableField_Throws() =>
        // 运行时条件仅作用于主表 WHERE，明细表前缀不解析
        Assert.Throws<ArgumentException>(() => ReportConditionsValidator.ValidateField("COP_ORDER_D.QTY", "COP_ORDER_M", MasterFields));

    [Fact]
    public void FieldOutOfWhitelist_Throws() =>
        Assert.Throws<ArgumentException>(() => ReportConditionsValidator.ValidateField("COP_ORDER_M.HACK_COL", "COP_ORDER_M", MasterFields));

    // ---- 条件语句（F_EXPR：按类型 DSL）----

    [Fact]
    public void RangeType_ExpressionMustBeEmpty()
    {
        Assert.Null(ReportConditionsValidator.ValidateExpression(1, null));
        Assert.Null(ReportConditionsValidator.ValidateExpression(1, "  "));
        Assert.Throws<ArgumentException>(() => ReportConditionsValidator.ValidateExpression(1, "A:1"));
    }

    [Theory]
    [InlineData("是:1;否:0", "是:1;否:0")]
    [InlineData(" 是 : 1 ; 否 : 0 ", "是:1;否:0")]
    [InlineData("启用:", "启用:")]
    public void FixedOptionDsl_Normalizes(string raw, string expected) =>
        Assert.Equal(expected, ReportConditionsValidator.ValidateExpression(2, raw));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void FixedOptionDsl_Empty_Throws(string? raw) =>
        Assert.Throws<ArgumentException>(() => ReportConditionsValidator.ValidateExpression(4, raw));

    [Theory]
    [InlineData("是")]      // 缺冒号
    [InlineData(":1")]      // 缺标签
    [InlineData("是:1:2")]  // 多冒号（旧运行时按段丢弃，写侧拦截）
    public void FixedOptionDsl_Malformed_Throws(string raw) =>
        Assert.Throws<ArgumentException>(() => ReportConditionsValidator.ValidateExpression(2, raw));

    [Fact]
    public void FixedOptionDsl_EmptySegments_AreDropped() =>
        Assert.Equal("是:1;否:0", ReportConditionsValidator.ValidateExpression(2, "是:1;;否:0"));

    [Theory]
    [InlineData("SELECT W_ID C_ID, W_NAME C_VALUE FROM WAREHOUSE")]
    [InlineData("select W_ID C_ID, W_NAME C_VALUE from WAREHOUSE")]
    [InlineData("  SELECT W_ID C_ID,W_NAME C_VALUE FROM WAREHOUSE  ")]
    public void SelectSourceDsl_Passes(string raw) =>
        Assert.Equal(raw.Trim(), ReportConditionsValidator.ValidateExpression(3, raw));

    [Theory]
    [InlineData("SELECT * FROM WAREHOUSE")]
    [InlineData("SELECT W_ID C_ID, W_NAME C_VALUE FROM WAREHOUSE WHERE 1=1")]
    [InlineData("DELETE FROM WAREHOUSE")]
    [InlineData("")]
    public void SelectSourceDsl_Malformed_Throws(string raw) =>
        Assert.Throws<ArgumentException>(() => ReportConditionsValidator.ValidateExpression(5, raw));

    // ---- 数据源语句解析（表/列提取供物理存在校验）----

    [Fact]
    public void MatchSelectSource_ExtractsGroups()
    {
        var match = ReportConditionsValidator.MatchSelectSource("SELECT W_ID C_ID, W_NAME C_VALUE FROM dbo.WAREHOUSE".Replace("dbo.", ""));
        Assert.NotNull(match);
        Assert.Equal("W_ID", match!.Groups[1].Value);
        Assert.Equal("W_NAME", match.Groups[2].Value);
        Assert.Equal("WAREHOUSE", match.Groups[3].Value);
    }

    // ---- 文本长度 ----

    [Fact]
    public void ValidateText_TrimsAndNullifiesEmpty()
    {
        Assert.Equal("abc", ReportConditionsValidator.ValidateText("  abc  ", 50, "条件描述"));
        Assert.Null(ReportConditionsValidator.ValidateText("", 50, "条件描述"));
        Assert.Null(ReportConditionsValidator.ValidateText(null, 50, "条件描述"));
    }

    [Fact]
    public void ValidateText_Overlong_Throws()
    {
        Assert.Throws<ArgumentException>(() => ReportConditionsValidator.ValidateText(new string('a', 51), 50, "条件描述"));
        Assert.Equal(new string('a', 50), ReportConditionsValidator.ValidateText(new string('a', 50), 50, "条件描述"));
    }
}
