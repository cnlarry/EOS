using EOS.API.Data.Forms;
using EOS.API.Models;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 设计态提交的规范化口径：页签常驻位、序号重排、跨度夹取、复合格角色收敛。
/// 这几条决定"保存下来的行长什么样"，用纯函数钉住，避免各调用点各写一份。
///
/// 列数一律来自页签（`FormTabInput.Columns`）：未给即落兜底 4 列（库内 NOT NULL DEFAULT 4，
/// 模块级那一层已随迁移 322 删除）。
/// </summary>
public sealed class FormLayoutSubmissionTests
{
    private static FormLayoutSaveRequest Request(
        IReadOnlyList<FormTabInput>? tabs = null,
        IReadOnlyList<FormLayoutRowInput>? master = null,
        IReadOnlyList<FormDetailLayoutRowInput>? detail = null)
        => new(BaseUpdatedAt: null, IdempotencyKey: null, tabs, master, detail);

    [Fact]
    public void Normalize_InjectsResidentDefaultTabWhenMissing()
    {
        var layout = FormLayoutSubmission.Normalize(
            Request(tabs: [new FormTabInput(2, "基本信息")], master: [new FormLayoutRowInput("A")]));

        Assert.Equal([1, 2], layout.Tabs.Select(tab => tab.No).ToArray());
        Assert.Equal(string.Empty, layout.Tabs[0].Title);
        Assert.Equal("基本信息", layout.Tabs[1].Title);
        // 补出来的常驻页签与未给列数的页签一律落兜底 4 列（写入侧不落 NULL）
        Assert.Equal(FormLayoutDerivation.DefaultColumns, layout.Tabs[0].Columns);
        Assert.Equal(FormLayoutDerivation.DefaultColumns, layout.Tabs[1].Columns);
    }

    [Fact]
    public void Normalize_KeepsSubmittedTabOrderAndTrimsTitles()
    {
        var layout = FormLayoutSubmission.Normalize(
            Request(tabs: [new FormTabInput(3, " 收尾 "), new FormTabInput(1, " 主 "), new FormTabInput(3, "重复")]));

        Assert.Equal([1, 3], layout.Tabs.Select(tab => tab.No).ToArray());
        Assert.Equal("主", layout.Tabs[0].Title);
        Assert.Equal("收尾", layout.Tabs[1].Title);
    }

    [Fact]
    public void Normalize_RenumbersOrderBySubmissionSequence()
    {
        var layout = FormLayoutSubmission.Normalize(
            Request(master: [new FormLayoutRowInput("B"), new FormLayoutRowInput("A"), new FormLayoutRowInput("C")]));

        Assert.Equal(["B", "A", "C"], layout.Master.Select(row => row.Key).ToArray());
        Assert.Equal([1, 2, 3], layout.Master.Select(row => row.OrderNo).ToArray());
        Assert.True(layout.MasterCustomized);
        Assert.True(layout.DetailCustomized);
    }

    [Fact]
    public void Normalize_ClampsSpanAndRowSpanToSchemaLimits()
    {
        // 页签声明三列：span 9 夹到 3；span 0 提到 1
        var layout = FormLayoutSubmission.Normalize(
            Request(
                tabs: [new FormTabInput(1, "默认", 3)],
                master:
                [
                    new FormLayoutRowInput("A", Span: 9, RowSpan: 5),
                    new FormLayoutRowInput("B", Span: 0, RowSpan: 0),
                ]));

        Assert.Equal(3, layout.Master[0].Span);
        Assert.Equal(FormLayoutDerivation.MaxRowSpan, layout.Master[0].RowSpan);
        Assert.Equal(1, layout.Master[1].Span);
        Assert.Equal(1, layout.Master[1].RowSpan);
    }

    [Fact]
    public void Normalize_ClampsSpanPerTabNotByASingleValue()
    {
        // 同一份版式、两个页签：页签 1 两列、页签 2 一列——同是 span 4，落库结果不同
        var layout = FormLayoutSubmission.Normalize(
            Request(
                tabs: [new FormTabInput(1, "主", 2), new FormTabInput(2, "明细", 1)],
                master:
                [
                    new FormLayoutRowInput("A", TabNo: 1, Span: 4),
                    new FormLayoutRowInput("B", TabNo: 2, Span: 4),
                ]));

        Assert.Equal(2, layout.Master[0].Span);
        Assert.Equal(1, layout.Master[1].Span);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(0, 0)]
    [InlineData(7, 0)]
    public void Normalize_KeepsOnlyValidCellRoles(int input, int expected)
    {
        var layout = FormLayoutSubmission.Normalize(
            Request(master: [new FormLayoutRowInput("A", CellRole: input)]));
        Assert.Equal(expected, layout.Master[0].CellRole);
    }

    [Fact]
    public void Normalize_TrimsSectionAndGroupAndDropsBlankKeys()
    {
        var layout = FormLayoutSubmission.Normalize(
            Request(master:
            [
                new FormLayoutRowInput(" A ", SectionId: " 基本信息 ", CellGroup: " CLIENT "),
                new FormLayoutRowInput("   "),
            ], detail: [new FormDetailLayoutRowInput(" D1 "), new FormDetailLayoutRowInput("")]));

        Assert.Single(layout.Master);
        Assert.Equal("A", layout.Master[0].Key);
        Assert.Equal("基本信息", layout.Master[0].SectionId);
        Assert.Equal("CLIENT", layout.Master[0].CellGroup);
        Assert.Equal(["D1"], layout.Detail.Select(row => row.Key).ToArray());
        Assert.Equal(1, layout.Detail[0].OrderNo);
    }

    [Fact]
    public void Normalize_DefaultsTabColumnsToFourWhenNotGiven()
    {
        // 页签没给列数 ⇒ 落兜底 4 列；span 2 因而合法
        var layout = FormLayoutSubmission.Normalize(
            Request(tabs: [new FormTabInput(1, "默认")], master: [new FormLayoutRowInput("A", Span: 2)]));

        Assert.Equal(FormLayoutDerivation.DefaultColumns, layout.Tabs[0].Columns);
        Assert.Equal(2, layout.Master[0].Span);
    }

    [Fact]
    public void Normalize_RejectsOutOfRangeTabColumns()
    {
        Assert.Throws<ArgumentException>(() => FormLayoutSubmission.Normalize(
            Request(tabs: [new FormTabInput(1, "默认", 9)], master: [new FormLayoutRowInput("A")])));
    }
}
