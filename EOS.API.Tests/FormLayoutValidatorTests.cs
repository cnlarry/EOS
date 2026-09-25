using EOS.API.Data.Forms;
using EOS.API.Models;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 模块级版式的 fail-closed 校验：逐条规则各配正反例——规则表是"配错了不许存"的唯一执行点，
/// 漏一条就会让悬空引用、越界占位或"必填被藏起来"静默落库。
/// </summary>
public sealed class FormLayoutValidatorTests
{
    private static FormLayoutFieldFact Fact(
        string key,
        string table = "COP_ORDER_M",
        bool primaryKey = false,
        bool lifecycle = false,
        bool required = false,
        bool chooser = false)
        => new(key, table, primaryKey, lifecycle, required, chooser);

    private static IReadOnlyDictionary<string, FormLayoutFieldFact> Fields(params FormLayoutFieldFact[] items)
        => items.ToDictionary(item => item.Key, StringComparer.OrdinalIgnoreCase);

    private static FormLayoutRow Row(
        string key,
        int span = 1,
        int rowSpan = 1,
        int tabNo = 1,
        string? cellGroup = null,
        int cellRole = 0,
        bool hidden = false)
        => new(key, tabNo, 1, span, rowSpan, false, null, cellGroup, cellRole, hidden);

    private static FormLayoutDefinition Layout(
        IReadOnlyList<FormLayoutRow> master,
        IReadOnlyList<FormDetailLayoutRow>? detail = null,
        IReadOnlyList<FormTabDefinition>? tabs = null,
        bool masterCustomized = true,
        bool detailCustomized = false,
        int columns = 3)
        => new(columns, tabs ?? [new FormTabDefinition(1, "默认")], master, detail ?? [], masterCustomized, detailCustomized);

    [Fact]
    public void ValidLayout_ReportsNothing()
    {
        var master = Fields(
            Fact("CLIENT_ID", chooser: true),
            Fact("CLIENT_NAME"),
            Fact("REMARK"));
        var layout = Layout([
            Row("CLIENT_ID", cellGroup: "CLIENT", cellRole: 1),
            Row("CLIENT_NAME", cellGroup: "CLIENT", cellRole: 2),
            Row("REMARK", span: 3, rowSpan: 2),
        ], tabs: [new FormTabDefinition(1, "默认"), new FormTabDefinition(2, "其它")]);

        Assert.Empty(FormLayoutValidator.Validate(layout, master, Fields()));
    }

    [Fact]
    public void FieldOfAnotherTable_IsRejected()
    {
        var layout = Layout([Row("NOT_MINE")]);
        var issues = FormLayoutValidator.Validate(layout, Fields(Fact("MINE")), Fields());
        Assert.Contains(issues, issue => issue.Code == "FORM_LAYOUT_FIELD_NOT_IN_MODULE" && issue.Key == "NOT_MINE");
    }

    [Fact]
    public void BlankKey_IsRejected()
    {
        var issues = FormLayoutValidator.Validate(Layout([Row("  ")]), Fields(), Fields());
        Assert.Contains(issues, issue => issue.Code == "FORM_LAYOUT_FIELD_UNKNOWN");
    }

    [Fact]
    public void Tabs_MustBeUniqueAndKeepDefault()
    {
        var issues = FormLayoutValidator.Validate(
            Layout([Row("A")], tabs: [new FormTabDefinition(2, "其它")]), Fields(Fact("A")), Fields());
        Assert.Contains(issues, issue => issue.Code == "FORM_LAYOUT_TAB_DEFAULT_MISSING");

        var duplicated = FormLayoutValidator.Validate(
            Layout([Row("A")], tabs: [new FormTabDefinition(1, "默认"), new FormTabDefinition(1, "重复")]),
            Fields(Fact("A")), Fields());
        Assert.Contains(duplicated, issue => issue.Code == "FORM_LAYOUT_TAB_DUPLICATE");
    }

    [Fact]
    public void RowOnUnknownTab_IsRejected()
    {
        var layout = Layout([Row("A", tabNo: 5)], tabs: [new FormTabDefinition(1, "默认")]);
        var issues = FormLayoutValidator.Validate(layout, Fields(Fact("A")), Fields());
        Assert.Contains(issues, issue => issue.Code == "FORM_LAYOUT_TAB_UNKNOWN" && issue.Key == "A");
    }

    [Fact]
    public void SpanAndRowSpan_OutOfRange_AreRejected()
    {
        var issues = FormLayoutValidator.Validate(
            Layout([Row("A", span: 4), Row("B", rowSpan: 4)]), Fields(Fact("A"), Fact("B")), Fields());
        Assert.Contains(issues, issue => issue.Code == "FORM_LAYOUT_SPAN_OUT_OF_RANGE" && issue.Key == "A");
        Assert.Contains(issues, issue => issue.Code == "FORM_LAYOUT_ROW_SPAN_OUT_OF_RANGE" && issue.Key == "B");
    }

    [Fact]
    public void CompanionWithoutMain_AndDuplicateMain_AreRejected()
    {
        var orphan = FormLayoutValidator.Validate(
            Layout([Row("CLIENT_NAME", cellGroup: "CLIENT", cellRole: 2)]),
            Fields(Fact("CLIENT_NAME")), Fields());
        Assert.Contains(orphan, issue => issue.Code == "FORM_LAYOUT_COMPANION_WITHOUT_MAIN");

        var twoMains = FormLayoutValidator.Validate(
            Layout([
                Row("CLIENT_ID", cellGroup: "CLIENT", cellRole: 1),
                Row("CLIENT_NAME", cellGroup: "CLIENT", cellRole: 1),
            ]),
            Fields(Fact("CLIENT_ID", chooser: true), Fact("CLIENT_NAME", chooser: true)), Fields());
        // 同组多主字段在既有数据里存在（组名复用），渲染侧按独立两格处理，不再作为保存期拦截项
        Assert.DoesNotContain(twoMains, issue => issue.Code == "FORM_LAYOUT_CELL_GROUP_MAIN_CONFLICT");
    }

    [Fact]
    public void MultipleCompanionsInOneGroup_AreAllowed()
    {
        // 既有一格多从字段（如 BOM 的 PRO = 料号 + 品名 + 颜色）必须能保存：
        // 推导默认的版式本身就长这样，卡住等于那些模块永远存不了版式
        var issues = FormLayoutValidator.Validate(
            Layout([
                Row("CLIENT_ID", cellGroup: "CLIENT", cellRole: 1),
                Row("CLIENT_NAME", cellGroup: "CLIENT", cellRole: 2),
                Row("CLIENT_SHORT", cellGroup: "CLIENT", cellRole: 2),
            ]),
            Fields(Fact("CLIENT_ID", chooser: true), Fact("CLIENT_NAME"), Fact("CLIENT_SHORT")), Fields());
        Assert.DoesNotContain(issues, issue => issue.Code == "FORM_LAYOUT_CELL_COMPANION_MULTIPLE");
        Assert.Empty(issues);
    }

    [Fact]
    public void CellMainWithoutActiveChooser_IsAllowed()
    {
        // 既有数据里有"有组名但没配选择器"的格子（如 LEADER_EMP_ID + 同格从字段），
        // 渲染上就是普通控件 + 同格从控件：卡住会让这类模块的推导版式保存不了
        var issues = FormLayoutValidator.Validate(
            Layout([Row("CLIENT_ID", cellGroup: "CLIENT", cellRole: 1)]),
            Fields(Fact("CLIENT_ID", chooser: false)), Fields());
        Assert.DoesNotContain(issues, issue => issue.Code == "FORM_LAYOUT_CELL_MAIN_WITHOUT_CHOOSER");
        Assert.Empty(issues);
    }

    [Fact]
    public void HiddenUserFillableRequired_IsRejected()
    {
        var issues = FormLayoutValidator.Validate(
            Layout([Row("QTY", hidden: true)]), Fields(Fact("QTY", required: true)), Fields());
        Assert.Contains(issues, issue => issue.Code == "FORM_LAYOUT_REQUIRED_HIDDEN" && issue.Key == "QTY");
    }

    [Fact]
    public void HiddenSystemColumn_IsRejected()
    {
        var issues = FormLayoutValidator.Validate(
            Layout([Row("CREATE_PERSON", hidden: true)]),
            Fields(Fact("CREATE_PERSON", primaryKey: false, lifecycle: true, required: true)), Fields());
        Assert.Contains(issues, issue => issue.Code == "FORM_LAYOUT_REQUIRED_HIDDEN" && issue.Key == "CREATE_PERSON");
    }

    [Fact]
    public void CustomizedTable_MustKeepRequiredFields()
    {
        var missing = FormLayoutValidator.Validate(
            Layout([Row("NAME")]), Fields(Fact("NAME"), Fact("QTY", required: true)), Fields());
        Assert.Contains(missing, issue => issue.Code == "FORM_LAYOUT_REQUIRED_MISSING" && issue.Key == "QTY");

        // 未定制的表不做"缺行"判定：它的字段集由字段级配置决定，版式表还没接管
        var derived = FormLayoutValidator.Validate(
            Layout([Row("NAME")], masterCustomized: false), Fields(Fact("NAME"), Fact("QTY", required: true)), Fields());
        Assert.DoesNotContain(derived, issue => issue.Code == "FORM_LAYOUT_REQUIRED_MISSING");
    }

    [Fact]
    public void DetailRows_AreValidatedAgainstDetailFields()
    {
        var detailFields = Fields(Fact("PRO_NO", table: "COP_ORDER_D", required: true), Fact("QTY", table: "COP_ORDER_D"));
        var layout = Layout([Row("NAME")], [new FormDetailLayoutRow("OTHER", 1, false)], detailCustomized: true);
        var issues = FormLayoutValidator.Validate(layout, Fields(Fact("NAME")), detailFields);
        Assert.Contains(issues, issue => issue.Code == "FORM_LAYOUT_FIELD_NOT_IN_MODULE" && issue.Key == "OTHER");
        Assert.Contains(issues, issue => issue.Code == "FORM_LAYOUT_REQUIRED_MISSING" && issue.Key == "PRO_NO");

        var hidden = FormLayoutValidator.Validate(
            Layout([Row("NAME")],
                [new FormDetailLayoutRow("PRO_NO", 1, true), new FormDetailLayoutRow("QTY", 2, false)],
                detailCustomized: true),
            Fields(Fact("NAME")), detailFields);
        Assert.Contains(hidden, issue => issue.Code == "FORM_LAYOUT_REQUIRED_HIDDEN" && issue.Key == "PRO_NO");
    }
}
