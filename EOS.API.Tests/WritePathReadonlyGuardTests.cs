using EOS.API.Data;
using EOS.API.Models;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 写入路径的只读守卫（`RecordPayloadValidator.ValidateSubmitted`）的行为钉桩。
///
/// 这条守卫是"服务端才是边界"在文档字段上的落点：**表单渲染只是体验，能不能写由服务端说了算**。
/// 它被三条写入路径共用（`WorkbenchCommandHandler` 的新增主表 / 修改主表 / 明细），因此这里按
/// 决策表逐格断言，改动它会立刻红。
///
/// **两个豁免是既有行为，不是漏洞**（代码注释写明"与前端 writableFields 同一口径"）：
/// 只读**且必填**的联动字段、只读**且带活动选择器**的回填字段，界面确实会带值，故仍可提交。
/// 生效范围的判据落在 `INV_PRO_MONTH_D.QTY` / `PRICE` 这两个派生列上——它们实测为
/// `IS_READONLY=1` / `IS_VERIFY=0`（非必填）/ 无可选数据源（`BROWSE_M_IDX=0`、`CHOOSE_PAGE=''`），
/// 正好落在"被拒"那一格：**手工改数量 / 单价会被服务端拒绝**。
/// </summary>
public sealed class WritePathReadonlyGuardTests
{
    private static FormFieldDefinition Field(
        string key = "QTY",
        bool isReadonly = true,
        bool isRequired = false,
        bool serverFilled = false,
        bool isVirtual = false,
        bool displayOnly = false,
        IReadOnlyList<FieldChooserSource>? choosers = null)
        => new(
            Key: key, Label: key, DataType: "float", DisplayLength: 70, DisplayFormat: null,
            IsRequired: isRequired, VerifyIndex: null, Regex: null, DefaultValue: null,
            IsReadonly: isReadonly, IsVisible: true, OnlyChoose: false, ChooseMultiple: false, ChoosePage: null,
            Choosers: choosers ?? [], IsPrimaryKey: false, IsAutoIncrement: false, IsVirtual: isVirtual,
            IsCost: false, IsSecrecy: false, ServerFilled: serverFilled, MaxLength: null,
            DisplayOnly: displayOnly);

    private static RecordPayloadValidator.ValidationResult Submit(FormFieldDefinition field, string? value = "110")
        => RecordPayloadValidator.ValidateSubmitted([field], new Dictionary<string, string?> { [field.Key] = value });

    [Fact]
    public void 只读且非必填且无可选数据源_提交即被拒()
    {
        // 这一格就是 1304 明细的数量 / 单价的形态
        var result = Submit(Field());

        var error = Assert.Single(result.Errors);
        Assert.Equal("READONLY_FIELD", error.Code);
        Assert.Equal("QTY", error.Field);
        Assert.DoesNotContain("QTY", result.Converted.Keys);
    }

    [Fact]
    public void 只读但必填的联动字段_仍可提交()
    {
        var result = Submit(Field(isRequired: true));

        Assert.Empty(result.Errors);
        Assert.True(result.Converted.ContainsKey("QTY"));
    }

    [Fact]
    public void 只读但带活动选择器的回填字段_仍可提交()
    {
        var choosers = new[] { new FieldChooserSource(true, "PRODUCT", "料件", 1201, null, null) };
        var result = Submit(Field(choosers: choosers));

        Assert.Empty(result.Errors);
        Assert.True(result.Converted.ContainsKey("QTY"));
    }

    [Fact]
    public void 只读但选择器未激活或未配表_仍被拒()
    {
        var inactive = Submit(Field(choosers: [new FieldChooserSource(false, "PRODUCT", "料件", 1201, null, null)]));
        Assert.Equal("READONLY_FIELD", Assert.Single(inactive.Errors).Code);

        var noTable = Submit(Field(choosers: [new FieldChooserSource(true, "  ", "料件", 1201, null, null)]));
        Assert.Equal("READONLY_FIELD", Assert.Single(noTable.Errors).Code);
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public void 服务端维护的字段_一律不可提交(bool serverFilled, bool isVirtual, bool displayOnly)
    {
        var result = Submit(Field(isReadonly: false, serverFilled: serverFilled, isVirtual: isVirtual, displayOnly: displayOnly));

        var error = Assert.Single(result.Errors);
        Assert.Equal("READONLY_FIELD", error.Code);
    }

    [Fact]
    public void 可写字段_正常通过并转换()
    {
        var result = Submit(Field(isReadonly: false));

        Assert.Empty(result.Errors);
        Assert.Equal(110d, Assert.IsType<double>(result.Converted["QTY"]));
    }
}
