using EOS.API.Data.Forms;
using EOS.API.Models;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 设计态提交的规范化口径：页签常驻位、序号重排、跨度夹取、复合格角色收敛。
/// 这几条决定"保存下来的行长什么样"，用纯函数钉住，避免各调用点各写一份。
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
            Request(tabs: [new FormTabInput(2, "基本信息")], master: [new FormLayoutRowInput("A")]), columns: 3);

        Assert.Equal([1, 2], layout.Tabs.Select(tab => tab.No).ToArray());
        Assert.Equal(string.Empty, layout.Tabs[0].Title);
        Assert.Equal("基本信息", layout.Tabs[1].Title);
    }

    [Fact]
    public void Normalize_KeepsSubmittedTabOrderAndTrimsTitles()
    {
        var layout = FormLayoutSubmission.Normalize(
            Request(tabs: [new FormTabInput(3, " 收尾 "), new FormTabInput(1, " 主 "), new FormTabInput(3, "重复")]),
            columns: 2);

        Assert.Equal([1, 3], layout.Tabs.Select(tab => tab.No).ToArray());
        Assert.Equal("主", layout.Tabs[0].Title);
        Assert.Equal("收尾", layout.Tabs[1].Title);
    }

    [Fact]
    public void Normalize_RenumbersOrderBySubmissionSequence()
    {
        var layout = FormLayoutSubmission.Normalize(
            Request(master: [new FormLayoutRowInput("B"), new FormLayoutRowInput("A"), new FormLayoutRowInput("C")]),
            columns: 2);

        Assert.Equal(["B", "A", "C"], layout.Master.Select(row => row.Key).ToArray());
        Assert.Equal([1, 2, 3], layout.Master.Select(row => row.OrderNo).ToArray());
        Assert.True(layout.MasterCustomized);
        Assert.True(layout.DetailCustomized);
    }

    [Fact]
    public void Normalize_ClampsSpanAndRowSpanToSchemaLimits()
    {
        var layout = FormLayoutSubmission.Normalize(
            Request(master:
            [
                new FormLayoutRowInput("A", Span: 9, RowSpan: 5),
                new FormLayoutRowInput("B", Span: 0, RowSpan: 0),
            ]),
            columns: 3);

        Assert.Equal(3, layout.Master[0].Span);
        Assert.Equal(FormLayoutDerivation.MaxRowSpan, layout.Master[0].RowSpan);
        Assert.Equal(1, layout.Master[1].Span);
        Assert.Equal(1, layout.Master[1].RowSpan);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(0, 0)]
    [InlineData(7, 0)]
    public void Normalize_KeepsOnlyValidCellRoles(int input, int expected)
    {
        var layout = FormLayoutSubmission.Normalize(
            Request(master: [new FormLayoutRowInput("A", CellRole: input)]), columns: 2);
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
            ], detail: [new FormDetailLayoutRowInput(" D1 "), new FormDetailLayoutRowInput("")]),
            columns: 2);

        Assert.Single(layout.Master);
        Assert.Equal("A", layout.Master[0].Key);
        Assert.Equal("基本信息", layout.Master[0].SectionId);
        Assert.Equal("CLIENT", layout.Master[0].CellGroup);
        Assert.Equal(["D1"], layout.Detail.Select(row => row.Key).ToArray());
        Assert.Equal(1, layout.Detail[0].OrderNo);
    }

    [Fact]
    public void Normalize_FallsBackToDefaultColumnsWhenModuleHasNoColumnSetting()
    {
        var layout = FormLayoutSubmission.Normalize(
            Request(master: [new FormLayoutRowInput("A", Span: 2)]), columns: 0);
        Assert.Equal(FormLayoutDerivation.DefaultColumns, layout.Columns);
        Assert.Equal(2, layout.Master[0].Span);
    }
}
