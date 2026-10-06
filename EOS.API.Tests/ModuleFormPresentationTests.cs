using EOS.API.Data.Forms;
using EOS.API.Models;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 表单呈现配置的取值口径：打开方式（`MODULES.FORM_OPEN_MODE`）与**页签级**栅格列数
/// （`MODULE_FORM_TAB.LAYOUT_COLUMNS`，迁移 322 起 NOT NULL DEFAULT 4）。
/// 未配置/越界一律回落默认值、写入侧拒绝非法取值、列数真的约束列跨度。
/// 这几条决定"元数据配错时表单还能不能打开"，用纯函数钉住。
/// </summary>
public sealed class ModuleFormPresentationTests
{
    [Theory]
    [InlineData(null, FormLayoutDerivation.DefaultColumns)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 3)]
    [InlineData(4, 4)]
    // 越界（直改库或历史快照）回落兜底 4 列，不把栅格排出界
    [InlineData(0, FormLayoutDerivation.DefaultColumns)]
    [InlineData(5, FormLayoutDerivation.DefaultColumns)]
    [InlineData(9, FormLayoutDerivation.DefaultColumns)]
    public void ResolveTabColumns_FallsBackToDefaultWhenUnsetOrOutOfRange(int? declared, int expected)
    {
        var tabs = new List<FormTabDefinition> { new(1, "默认", declared) };
        Assert.Equal(expected, FormLayoutDerivation.ResolveTabColumns(tabs, 1));
    }

    [Theory]
    [InlineData(null, FormOpenModes.Tab)]
    [InlineData("", FormOpenModes.Tab)]
    [InlineData("tab", FormOpenModes.Tab)]
    [InlineData("TAB", FormOpenModes.Tab)]
    [InlineData(" newtab ", FormOpenModes.NewTab)]
    [InlineData("dialog", FormOpenModes.Dialog)]
    [InlineData("DIALOG", FormOpenModes.Dialog)]
    // 无法识别的取值一律回落本页签：呈现开关不该让表单打不开
    [InlineData("POPUP", FormOpenModes.Tab)]
    public void Normalize_IsLenientAndFallsBackToCurrentTab(string? value, string expected)
        => Assert.Equal(expected, FormOpenModes.Normalize(value));

    [Fact]
    public void IsDialog_OnlyTrueForDialog()
    {
        Assert.True(FormOpenModes.IsDialog("dialog"));
        Assert.False(FormOpenModes.IsDialog(FormOpenModes.NewTab));
        Assert.False(FormOpenModes.IsDialog(null));
    }

    [Fact]
    public void ParseForWrite_ReturnsNullWhenUnsetAndCanonicalUppercaseWhenKnown()
    {
        Assert.Null(FormOpenModes.ParseForWrite(null));
        Assert.Null(FormOpenModes.ParseForWrite("   "));
        Assert.Equal(FormOpenModes.NewTab, FormOpenModes.ParseForWrite(" newtab "));
        Assert.Equal(FormOpenModes.Dialog, FormOpenModes.ParseForWrite("Dialog"));
    }

    [Fact]
    public void ParseForWrite_RejectsUnknownValue()
    {
        var error = Assert.Throws<ArgumentException>(() => FormOpenModes.ParseForWrite("POPUP"));
        Assert.Contains("NEWTAB", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParsePresentation_TreatsAllEmptyAsNotGiven()
    {
        // 只存版式的调用方（含历史客户端）不能顺手把模块的呈现配置清空
        var presentation = FormLayoutSubmission.ParsePresentation(
            new FormLayoutSaveRequest(null, "k", null, null, null));
        Assert.False(presentation.IsGiven);
        Assert.Equal(FormPresentation.None, presentation);
    }

    [Fact]
    public void ParsePresentation_NormalisesOpenModeAndClearsDialogSizeOffDialog()
    {
        // 从弹窗改回本页签：窗体宽高必须被清掉（留着就是"看着配了、其实不生效"的值）
        var presentation = FormLayoutSubmission.ParsePresentation(
            new FormLayoutSaveRequest(null, "k", null, null, null,
                OpenMode: "tab", DialogWidth: 900, DialogHeight: 640));

        Assert.True(presentation.IsGiven);
        Assert.Equal(FormOpenModes.Tab, presentation.OpenMode);
        Assert.Null(presentation.DialogWidth);
        Assert.Null(presentation.DialogHeight);
    }

    [Fact]
    public void ParsePresentation_KeepsDialogSizeForDialog()
    {
        var presentation = FormLayoutSubmission.ParsePresentation(
            new FormLayoutSaveRequest(null, "k", null, null, null,
                OpenMode: "dialog", DialogWidth: 900, DialogHeight: 600));

        Assert.Equal(FormOpenModes.Dialog, presentation.OpenMode);
        Assert.Equal(900, presentation.DialogWidth);
        Assert.Equal(600, presentation.DialogHeight);
    }

    // 非法值一律 400（由统一异常出口映射），不放行到库里去撞 CHECK 约束变成 500
    [Theory]
    [InlineData("POPUP", null, null)]
    [InlineData("DIALOG", 100, null)]
    [InlineData("DIALOG", null, 5000)]
    public void ParsePresentation_RejectsInvalidValues(string? openMode, int? width, int? height)
    {
        Assert.Throws<ArgumentException>(() => FormLayoutSubmission.ParsePresentation(
            new FormLayoutSaveRequest(null, "k", null, null, null,
                OpenMode: openMode, DialogWidth: width, DialogHeight: height)));
    }

    [Fact]
    public void Validator_HonoursTabDeclaredColumns()
    {
        // 页签 1 声明两列、页签 2 声明一列：同模块不同页签各判各的
        var fields = new Dictionary<string, FormLayoutFieldFact>(StringComparer.OrdinalIgnoreCase)
        {
            ["A"] = new("A", "T", false, false, false, false),
            ["B"] = new("B", "T", false, false, false, false),
            ["C"] = new("C", "T", false, false, false, false),
        };
        var layout = new FormLayoutDefinition(
            [new FormTabDefinition(1, "默认", 2), new FormTabDefinition(2, "明细", 1)],
            [
                new FormLayoutRow("A", 1, 1, 2, 1, false, null, null, 0, false),   // 页签 1：2 合法
                new FormLayoutRow("B", 1, 2, 3, 1, false, null, null, 0, false),   // 页签 1：3 越界
                new FormLayoutRow("C", 2, 1, 2, 1, false, null, null, 0, false),   // 页签 2：2 越界（只声明 1 列）
            ],
            [],
            MasterCustomized: true);

        var issues = FormLayoutValidator.Validate(layout, fields, fields);

        Assert.DoesNotContain(issues, issue => issue.Code == "FORM_LAYOUT_SPAN_OUT_OF_RANGE" && issue.Key == "A");
        Assert.Contains(issues, issue => issue.Code == "FORM_LAYOUT_SPAN_OUT_OF_RANGE" && issue.Key == "B");
        Assert.Contains(issues, issue => issue.Code == "FORM_LAYOUT_SPAN_OUT_OF_RANGE" && issue.Key == "C");
        // 报错文案要说清是哪个页签的上限——否则用户按别的页签去改会更懵
        Assert.Contains(issues, issue => issue.Key == "C" && issue.Message.Contains("页签 2", StringComparison.Ordinal));
    }

    [Fact]
    public void Validator_FallsBackToFourWhenTabDeclaresNothing()
    {
        // 页签没声明列数（历史快照）：按兜底 4 列判——span 3 合法、span 5 越界
        var fields = new Dictionary<string, FormLayoutFieldFact>(StringComparer.OrdinalIgnoreCase)
        {
            ["A"] = new("A", "T", false, false, false, false),
            ["B"] = new("B", "T", false, false, false, false),
        };
        var layout = new FormLayoutDefinition(
            [new FormTabDefinition(1, "默认")],
            [
                new FormLayoutRow("A", 1, 1, 3, 1, false, null, null, 0, false),
                new FormLayoutRow("B", 1, 2, 5, 1, false, null, null, 0, false),
            ],
            [],
            MasterCustomized: true);

        var issues = FormLayoutValidator.Validate(layout, fields, fields);

        Assert.DoesNotContain(issues, issue => issue.Code == "FORM_LAYOUT_SPAN_OUT_OF_RANGE" && issue.Key == "A");
        Assert.Contains(issues, issue => issue.Code == "FORM_LAYOUT_SPAN_OUT_OF_RANGE" && issue.Key == "B");
    }

    [Fact]
    public void Normalize_ClampsSpanPerTabAndRejectsUnknownTabColumns()
    {
        var request = new FormLayoutSaveRequest(
            null,
            "k",
            [new FormTabInput(1, "默认", 2), new FormTabInput(2, "明细", null)],
            [
                new FormLayoutRowInput("A", TabNo: 1, Span: 4),   // 页签 1 两列 → 夹到 2
                new FormLayoutRowInput("B", TabNo: 2, Span: 4),   // 页签 2 未声明 → 兜底 4 列 → 4 合法
            ],
            []);

        var layout = FormLayoutSubmission.Normalize(request);

        Assert.Equal(2, layout.Tabs.First(tab => tab.No == 1).Columns);
        // 未声明的页签落兜底 4 列（写入侧不落 NULL，库内 NOT NULL DEFAULT 4）
        Assert.Equal(FormLayoutDerivation.DefaultColumns, layout.Tabs.First(tab => tab.No == 2).Columns);
        Assert.Equal(2, layout.Master.First(row => row.Key == "A").Span);
        Assert.Equal(4, layout.Master.First(row => row.Key == "B").Span);

        // 页签列数越界即 400（不放行到库里去撞 CHECK）
        Assert.Throws<ArgumentException>(() => FormLayoutSubmission.Normalize(
            new FormLayoutSaveRequest(null, "k", [new FormTabInput(1, "默认", 9)], [], [])));
    }
}
