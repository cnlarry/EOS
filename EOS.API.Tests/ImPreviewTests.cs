using EOS.API.Models;
using EOS.API.Services;
using Xunit;

namespace EOS.API.Tests;

public sealed class ImPreviewTests
{
    [Fact]
    public void Text_ReturnsTruncatedText()
    {
        var preview = ImPreview.Build(ImMessageType.Text, """{"text":"这是一条很长的消息内容"}""", maxLength: 8);
        Assert.Equal("这是一条很长的消…", preview);
    }

    [Fact]
    public void Text_HandlesInvalidJsonGracefully()
    {
        Assert.Equal(string.Empty, ImPreview.Build(ImMessageType.Text, "not-json"));
    }

    [Theory]
    [InlineData("purchase-order", "[卡片]：purchase-order")]
    [InlineData("inventory-count", "[卡片]：inventory-count")]
    public void Card_ReturnsFixedPreview(string cardType, string expected)
    {
        var preview = ImPreview.Build(ImMessageType.Card, $$"""{"cardType":"{{cardType}}"}""");
        Assert.Equal(expected, preview);
    }

    [Fact]
    public void SystemAndFile_ReturnFixedLabels()
    {
        Assert.Equal("[系统消息]", ImPreview.Build(ImMessageType.System, "{}"));
        Assert.Equal("[文件]", ImPreview.Build(ImMessageType.File, "{}"));
    }
}
