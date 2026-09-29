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

    /// <summary>
    /// 反例护栏：凭据之后的中文说明不得被一起吃掉。
    /// 旧实现的凭据字符集是"排除空白/逗号/分号"，中文标点不在排除集里，
    /// 会把 `Password=x，随后整段说明` 全部替换成 `***`——那是脱敏最该避免的误伤。
    /// </summary>
    [Fact]
    public void Redact_KeepsTextAfterCredential()
    {
        var redacted = LogRedactor.Redact("连接失败 Password=P@ssw0rd!，业务文本应当原样保留");
        Assert.DoesNotContain("P@ssw0rd!", redacted);
        Assert.Contains("业务文本应当原样保留", redacted);
    }

    /// <summary>连接串各要素逐项掩码，且不吞掉后面的分号与其它键。</summary>
    [Fact]
    public void Redact_MasksEachConnectionStringPart()
    {
        var redacted = LogRedactor.Redact("Server=db01;Database=EOS.ERP;User ID=sa;Password=P@ssw0rd!;");
        Assert.DoesNotContain("db01", redacted);
        Assert.DoesNotContain("sa", redacted);
        Assert.DoesNotContain("P@ssw0rd!", redacted);
        // 库名不是凭据，保留它才有利于排障
        Assert.Contains("EOS.ERP", redacted);
    }
}
