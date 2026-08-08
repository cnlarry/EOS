using EOS.API.Services;
using Xunit;

namespace EOS.API.Tests;

public sealed class ImFileValidationTests
{
    [Theory]
    [InlineData(1024, "a.pdf", "application/pdf", true)]
    [InlineData(1024, "a.PNG", "image/png", true)]
    [InlineData(1024, "a.docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document", true)]
    [InlineData(1024, "a.exe", "application/octet-stream", false)]
    [InlineData(1024, "a.pdf", "text/plain", true)]
    [InlineData(0, "a.txt", "text/plain", false)]
    [InlineData(21 * 1024 * 1024, "a.pdf", "application/pdf", false)]
    [InlineData(1024, "noextension", "text/plain", false)]
    public void IsAllowed_EnforcesWhitelistAndSize(long size, string? name, string? type, bool expected)
        => Assert.Equal(expected, ImFileValidation.IsAllowed(size, name, type));

    [Fact]
    public void MaxBytes_IsTwentyMegabytes()
        => Assert.Equal(20 * 1024 * 1024, ImFileValidation.MaxBytes);
}
