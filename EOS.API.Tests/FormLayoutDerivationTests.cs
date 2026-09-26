using EOS.API.Data.Forms;
using EOS.API.Models;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 默认推导与版式施加的纯函数口径。
///
/// 其中"未定制的表原样返回"是本改造**观感零变化**的实现依据：零配置模块的字段集与顺序
/// 仍由既有的字段级配置决定，默认推导只作为"开始定制时的起点"存在。
/// </summary>
public sealed class FormLayoutDerivationTests
{
    private static FormLayoutFieldInput Input(
        string key,
        string dataType = "nvarchar",
        bool visible = true,
        bool required = false,
        int tabNo = 1,
        int? formOrder = null,
        int span = 1,
        bool newLine = false,
        string? cellGroup = null,
        int cellRole = 0)
        => new(key, dataType, visible, required, tabNo, formOrder, span, newLine, cellGroup, cellRole);

    private static FormFieldDefinition Field(string key, string dataType = "nvarchar", bool isVirtual = false)
        => new(key, key, dataType, 100, null, IsRequired: false, VerifyIndex: null, Regex: null, DefaultValue: null,
            IsReadonly: false, IsVisible: true, OnlyChoose: false, ChooseMultiple: false, ChoosePage: null,
            Choosers: [], IsPrimaryKey: false, IsAutoIncrement: false, IsVirtual: isVirtual, IsCost: false,
            IsSecrecy: false, ServerFilled: false, MaxLength: null);

    [Theory]
    [InlineData("REMARK", "nvarchar", true)]
    [InlineData("LINE_REMARK", "nvarchar", true)]
    [InlineData("MEMO", "text", true)]
    [InlineData("MEMO", "ntext", true)]
    [InlineData("REMARK_DATE", "nvarchar", false)]
    [InlineData("QTY", "decimal", false)]
    public void WideTextField_IsKeyedOnRemarkSuffixOrLongText(string key, string dataType, bool expected)
        => Assert.Equal(expected, FormLayoutDerivation.IsWideText(key, dataType));

    [Fact]
    public void DeriveDefault_TakesFieldLevelLayoutAndMarksWideTextRows()
    {
        var layout = FormLayoutDerivation.DeriveDefault(
            [Input("A", formOrder: 5, span: 2), Input("REMARK", dataType: "text"), Input("B")],
            [],
            columns: 4);

        Assert.Equal(4, layout.Columns);
        Assert.Empty(layout.Tabs);
        Assert.False(layout.MasterCustomized);
        // 字段级旧语义映射到子列：span=2（整行独占）→ 4 子列；span=1（半行）→ 2 子列；备注类整行
        Assert.Equal(
            [("A", 5, 4, 1), ("REMARK", 2, 4, 2), ("B", 3, 2, 1)],
            layout.Master.Select(row => (row.Key, row.OrderNo, row.Span, row.RowSpan)).ToArray());
    }

    [Fact]
    public void DeriveDefault_HidesOnlyFieldsThatNeverReachTheForm()
    {
        var layout = FormLayoutDerivation.DeriveDefault(
            [
                Input("PLAIN"),
                Input("HIDDEN_PLAIN", visible: false),
                Input("HIDDEN_REQUIRED", visible: false, required: true),
                Input("HIDDEN_COMPANION", visible: false, cellGroup: "CLIENT", cellRole: 2),
            ],
            [],
            columns: 2);

        Assert.Equal(
            [false, true, false, false],
            layout.Master.Select(row => row.Hidden).ToArray());
    }

    [Fact]
    public void DeriveDefault_ClampsSpanAndColumns()
    {
        var layout = FormLayoutDerivation.DeriveDefault([Input("A", span: 0), Input("B", span: 9)], [], columns: 0);
        Assert.Equal(FormLayoutDerivation.DefaultColumns, layout.Columns);
        // span 先夹到旧语义 1..2，再映射到子列（0→半行、9→整行）
        Assert.Equal([2, 4], layout.Master.Select(row => row.Span).ToArray());
    }

    [Fact]
    public void ApplyMasterLayout_WithoutCustomization_KeepsFieldSetAndOrder()
    {
        var fields = new List<FormFieldDefinition> { Field("A"), Field("B") };
        var derived = FormLayoutDerivation.DeriveDefault([Input("A"), Input("B")], [], 2);
        var customized = new FormLayoutDefinition(2, [], [new FormLayoutRow("B", 1, 1, 2, 1, false, null, null, 0, false)],
            [], MasterCustomized: true);

        Assert.Equal(["A", "B"], FormLayoutDerivation.ApplyMasterLayout(fields, null).Select(field => field.Key).ToArray());
        Assert.Equal(["A", "B"], FormLayoutDerivation.ApplyMasterLayout(fields, derived).Select(field => field.Key).ToArray());
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
        var layout = new FormLayoutDefinition(2, [],
            [new FormLayoutRow("CLIENT_NAME", 1, 1, 1, 1, false, null, "CLIENT", 2, false)],
            [], MasterCustomized: true);

        var result = FormLayoutDerivation.ApplyMasterLayout(fields, layout);

        Assert.Equal(["CLIENT_NAME"], result.Select(field => field.Key).ToArray());
    }

    [Fact]
    public void ApplyMasterLayout_RewritesOrderPlacementAndDropsUnlistedFields()
    {
        var layout = new FormLayoutDefinition(
            3,
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
    public void ApplyMasterLayout_SkipsHiddenRowsAndClampsSpanToModuleColumns()
    {
        var layout = new FormLayoutDefinition(
            2,
            [],
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
    public void ApplyDetailOrder_WithoutCustomization_ReturnsInputUntouched()
    {
        var keys = new List<string> { "A", "B" };
        var derived = FormLayoutDerivation.DeriveDefault([Input("A"), Input("B")], [], 2);
        var customized = new FormLayoutDefinition(2, [], [],
            [new FormDetailLayoutRow("B", 1, false)], MasterCustomized: false, DetailCustomized: true);

        Assert.Same(keys, FormLayoutDerivation.ApplyDetailOrder(keys, null));
        Assert.Same(keys, FormLayoutDerivation.ApplyDetailOrder(keys, derived));
        Assert.Equal(["B"], FormLayoutDerivation.ApplyDetailOrder(keys, customized));
    }

    [Fact]
    public void ApplyDetailOrder_ReordersByLayoutAndDropsHiddenColumns()
    {
        var layout = new FormLayoutDefinition(
            2, [], [],
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
