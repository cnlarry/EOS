using EOS.API.Features.Assistant.ModelAccess;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 模型把工具调用写进正文时的识别与按住（见 <see cref="ToolCallTextGuard"/>）：
/// 标记不能被当成回答下发，也不能被当成回答留存。
/// </summary>
public sealed class ToolCallTextGuardTests
{
    /// <summary>全角竖线：标记在**文本通道**里用它转义（结构化通道里不存在这个差异）。</summary>
    private const string Pipe = "\uFF5C";

    private static string Open(string suffix) => $"<{Pipe}{Pipe}DSML{Pipe}{Pipe} {suffix}";

    [Theory]
    [InlineData("")]
    [InlineData(" \n ")]
    [InlineData("<")]
    [InlineData("<|")]
    [InlineData("<||")]
    [InlineData("<||dsml||")]
    [InlineData("<||dsml|| calls>")]
    [InlineData("<||DSML|| invoke name=\"run_report\">")]
    public void Markup_Opening_Is_Held(string text)
    {
        Assert.True(ToolCallTextGuard.CouldBeMarkupOpen(text));
        // 按住的是"可能成为标记开头"的那一段：这里从头就是，所以可下发部分为空
        Assert.Equal(0, ToolCallTextGuard.PossibleMarkupStart(text, 0));
    }

    [Theory]
    [InlineData("你好")]
    [InlineData("结论：销售额见口径表。")]
    [InlineData("<table>")]
    [InlineData("金额 < 1000 的单")]
    public void Normal_Text_Is_Not_Held(string text)
    {
        Assert.False(ToolCallTextGuard.CouldBeMarkupOpen(text));
        Assert.Equal(text.Length, ToolCallTextGuard.PossibleMarkupStart(text, 0));
    }

    [Fact]
    public void Markup_In_The_Middle_Holds_From_The_Marker_Only()
    {
        // 模型先答一半、再想调工具：按住的是**最早**那个标记的位置，前半截正文照常下发
        var text = "结论：口径表见下。\n" + Open("calls>") + "\n" + $"</{Pipe}{Pipe}DSML{Pipe}{Pipe} calls>";

        Assert.Equal("结论：口径表见下。\n".Length, ToolCallTextGuard.PossibleMarkupStart(text, 0));
        Assert.Equal("结论：口径表见下。", ToolCallTextGuard.Strip(text));
    }

    [Fact]
    public void Markup_Only_Strips_To_Empty()
    {
        var text = $"{Open("calls>")}\n{Open("invoke name=\"run_report\">")}\n</{Pipe}{Pipe}DSML{Pipe}{Pipe} calls>";
        Assert.Equal(string.Empty, ToolCallTextGuard.Strip(text));
    }

    [Fact]
    public void Unclosed_Markup_Is_Dropped_To_The_End()
    {
        // 不完整的标记同样是标记：留在正文里只会变成看不懂的一段
        var text = $"结论如下。\n{Open("calls>")}\n{Open("invoke name=\"run_report\">")}";
        Assert.Equal("结论如下。", ToolCallTextGuard.Strip(text));
    }

    [Fact]
    public void Half_Width_Pipe_Is_Recognized_Too()
    {
        const string markup = "<||DSML|| calls><||DSML|| invoke name=\"run_report\"></||DSML|| calls>";
        Assert.Equal(string.Empty, ToolCallTextGuard.Strip(markup));
        Assert.Equal(0, ToolCallTextGuard.PossibleMarkupStart(markup, 0));
    }

    [Fact]
    public void Text_Without_Markup_Is_Returned_Untouched()
    {
        // 没剥过标记就不能动正文：存进去的正文必须与模型给的一模一样（含首尾空白）
        Assert.Equal("  结论：见口径表。  \n", ToolCallTextGuard.Strip("  结论：见口径表。  \n"));
        Assert.Equal("金额 < 1000 的单 <table>", ToolCallTextGuard.Strip("金额 < 1000 的单 <table>"));
    }
}
