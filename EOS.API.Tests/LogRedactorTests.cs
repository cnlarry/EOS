using EOS.API.Logging;
using Xunit;

namespace EOS.API.Tests;

public sealed class LogRedactorTests
{
    [Fact]
    public void Redact_MasksPasswordAndToken()
    {
        var redacted = LogRedactor.Redact("password=secret123 token=abc.def.ghi");
        Assert.DoesNotContain("secret123", redacted);
        Assert.DoesNotContain("abc.def.ghi", redacted);
        Assert.Contains("***", redacted);
    }

    [Fact]
    public void Redact_MasksConnectionStringSecret()
    {
        var redacted = LogRedactor.Redact("Server=localhost;Database=EOS.ERP;User ID=sa;Password=P@ssw0rd!;");
        Assert.DoesNotContain("P@ssw0rd!", redacted);
        Assert.Contains("***", redacted);
    }

    [Fact]
    public void Redact_MasksBearerToken()
    {
        var redacted = LogRedactor.Redact("Authorization: Bearer eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxIn0.sig1234567890");
        Assert.DoesNotContain("eyJhbGciOiJIUzI1NiJ9", redacted);
        Assert.Contains("***", redacted);
    }

    [Fact]
    public void Redact_LeavesPlainText()
    {
        var redacted = LogRedactor.Redact("普通文本，无敏感信息");
        Assert.Equal("普通文本，无敏感信息", redacted);
    }
}
