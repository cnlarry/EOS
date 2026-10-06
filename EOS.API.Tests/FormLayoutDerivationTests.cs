using EOS.API.Data.Forms;
using EOS.API.Models;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 版式施加的纯函数口径。
///
/// 其中"未定制的表原样返回"是本改造**观感零变化**的实现依据：无版式行 = 未定制，
/// 字段集与顺序保持字段元数据读取的原样。
///
/// 列数一律来自页签（`FormTabDefinition.Columns`，迁移 322 起模块级那层已删除）：
/// 页签没声明列数时按 4 列兜底。
/// </summary>
public sealed class FormLayoutDerivationTests
{
    private static FormFieldDefinition Field(string key, string dataType = "nvarchar", bool isVirtual = false)
        => new(key, key, dataType, 100, null, IsRequired: false, VerifyIndex: null, Regex: null, DefaultValue: null,
            IsReadonly: false, IsVisible: true, OnlyChoose: false, ChooseMultiple: false, ChoosePage: null,
            Choosers: [], IsPrimaryKey: false, IsAutoIncrement: false, IsVirtual: isVirtual, IsCost: false,
            IsSecrecy: false, ServerFilled: false, MaxLength: null);

    [Fact]
    public void ApplyMasterLayout_WithoutCustomization_KeepsFieldSetAndOrder()
    {
        var fields = new List<FormFieldDefinition> { Field("A"), Field("B") };
        var notCustomized = new FormLayoutDefinition([], [], [], MasterCustomized: false);
        var customized = new FormLayoutDefinition([], [new FormLayoutRow("B", 1, 1, 2, 1, false, null, null, 0, false)],
            [], MasterCustomized: true);

        Assert.Equal(["A", "B"], FormLayoutDerivation.ApplyMasterLayout(fields, null).Select(field => field.Key).ToArray());
        Assert.Equal(["A", "B"], FormLayoutDerivation.ApplyMasterLayout(fields, notCustomized).Select(field => field.Key).ToArray());
        Assert.NotSame(fields, FormLayoutDerivation.ApplyMasterLayout(fields, customized));
    }

    [Fact]
    public void ApplyMasterLayout_WithoutCustomization_DropsVirtualCompanionColumns()
    {
        // 虚拟列是选择器回写出来的伴生显示列：未定制的模块不该凭空多出这些没有输入控件语义的列
        var fields = new List<FormFieldDefinition> { Field("A"), Field("CLIENT_NAME", isVirtual: true) };

        var result = FormLayoutDerivation.ApplyMasterLayout(fields, null);

        Assert.Equal(["A"], result.Select(field => field.Key).ToArray());
    }

    [Fact]
    public void ApplyMasterLayout_WithCustomization_KeepsVirtualColumnsThatLayoutLists()
    {
        // 版式显式排入的虚拟列要保留（回写目标得在表单上存在，否则值只活在内存里）
        var fields = new List<FormFieldDefinition> { Field("A"), Field("CLIENT_NAME", isVirtual: true) };
        var layout = new FormLayoutDefinition([],
            [new FormLayoutRow("CLIENT_NAME", 1, 1, 1, 1, false, null, "CLIENT", 2, false)],
            [], MasterCustomized: true);

        var result = FormLayoutDerivation.ApplyMasterLayout(fields, layout);

        Assert.Equal(["CLIENT_NAME"], result.Select(field => field.Key).ToArray());
    }

    [Fact]
    public void ApplyMasterLayout_RewritesOrderPlacementAndDropsUnlistedFields()
    {
        var layout = new FormLayoutDefinition(
            [],
            [
                new FormLayoutRow("B", 2, 1, 2, 2, true, "SECTION", "CLIENT", 1, false),
                new FormLayoutRow("A", 1, 2, 1, 1, false, null, null, 0, false),
            ],
            [],
            MasterCustomized: true);

        var result = FormLayoutDerivation.ApplyMasterLayout([Field("A"), Field("B"), Field("C")], layout);

        Assert.Equal(["B", "A"], result.Select(field => field.Key).ToArray());
        var b = result[0];
        Assert.Equal(2, b.TabNo);
        Assert.Equal(1, b.FormOrder);
        Assert.Equal(2, b.Span);
        Assert.Equal(2, b.RowSpan);
        Assert.True(b.NewLine);
        Assert.Equal("SECTION", b.SectionId);
        Assert.Equal("CLIENT", b.CellGroup);
        Assert.Equal(1, b.CellRole);
    }

    [Fact]
    public void ApplyMasterLayout_SkipsHiddenRowsAndClampsSpanToTabColumns()
    {
        // 页签声明两列：span 5 夹到 2（列数是页签级事实，不再有模块级兜底值）
        var layout = new FormLayoutDefinition(
            [new FormTabDefinition(1, "默认", 2)],
            [
                new FormLayoutRow("A", 1, 1, 5, 9, false, null, null, 0, false),
                new FormLayoutRow("B", 1, 2, 1, 1, false, null, null, 0, Hidden: true),
            ],
            [],
            MasterCustomized: true);

        var result = FormLayoutDerivation.ApplyMasterLayout([Field("A"), Field("B")], layout);

        Assert.Equal(["A"], result.Select(field => field.Key).ToArray());
        Assert.Equal(2, result[0].Span);
        Assert.Equal(FormLayoutDerivation.MaxRowSpan, result[0].RowSpan);
    }

    [Fact]
    public void ApplyMasterLayout_WithoutTabColumns_ClampsToDefaultFour()
    {
        // 页签没声明列数（历史快照）：按 4 列兜底——span 5 夹到 4
        var layout = new FormLayoutDefinition(
            [new FormTabDefinition(1, "默认")],
            [new FormLayoutRow("A", 1, 1, 5, 1, false, null, null, 0, false)],
            [],
            MasterCustomized: true);

        var result = FormLayoutDerivation.ApplyMasterLayout([Field("A")], layout);

        Assert.Equal(FormLayoutDerivation.DefaultColumns, result[0].Span);
    }

    [Fact]
    public void ApplyDetailOrder_WithoutCustomization_ReturnsInputUntouched()
    {
        var keys = new List<string> { "A", "B" };
        var notCustomized = new FormLayoutDefinition([], [], [], MasterCustomized: false);
        var customized = new FormLayoutDefinition([], [],
            [new FormDetailLayoutRow("B", 1, false)], MasterCustomized: false, DetailCustomized: true);

        Assert.Same(keys, FormLayoutDerivation.ApplyDetailOrder(keys, null));
        Assert.Same(keys, FormLayoutDerivation.ApplyDetailOrder(keys, notCustomized));
        Assert.Equal(["B"], FormLayoutDerivation.ApplyDetailOrder(keys, customized));
    }

    [Fact]
    public void ApplyDetailOrder_ReordersByLayoutAndDropsHiddenColumns()
    {
        var layout = new FormLayoutDefinition(
            [], [],
            [
                new FormDetailLayoutRow("C", 1, false),
                new FormDetailLayoutRow("A", 2, false),
                new FormDetailLayoutRow("B", 3, true),
                new FormDetailLayoutRow("NOT_PRESENT", 4, false),
            ],
            MasterCustomized: false, DetailCustomized: true);

        Assert.Equal(["C", "A"], FormLayoutDerivation.ApplyDetailOrder(["A", "B", "C"], layout));
    }
}
