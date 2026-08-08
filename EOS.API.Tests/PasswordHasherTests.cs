using EOS.API.Security;
using Xunit;

namespace EOS.API.Tests;

public sealed class PasswordHasherTests
{
    [Fact]
    public void Hash_ReturnsV1FormatWithin50Chars()
    {
        var hash = PasswordHasher.Hash("correct horse battery staple");
        Assert.StartsWith(PasswordHasher.VersionPrefix, hash, StringComparison.Ordinal);
        Assert.Equal(50, hash.Length);
        Assert.True(PasswordHasher.IsValidFormat(hash));
    }

    [Fact]
    public void Verify_RoundTrip()
    {
        const string password = "S3cret!Pass";
        var hash = PasswordHasher.Hash(password);
        Assert.True(PasswordHasher.Verify(hash, password));
    }

    [Fact]
    public void Verify_WrongPassword_ReturnsFalse()
    {
        var hash = PasswordHasher.Hash("right-password");
        Assert.False(PasswordHasher.Verify(hash, "wrong-password"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Verify_EmptyOrNullPassword_ReturnsFalse(string? password) =>
        Assert.False(PasswordHasher.Verify(PasswordHasher.Hash("x"), password));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Hash_EmptyOrNullPassword_Throws(string? password) =>
        Assert.Throws<ArgumentException>(() => PasswordHasher.Hash(password!));

    [Fact]
    public void Hash_UsesUniqueSalt()
    {
        const string password = "same-password";
        Assert.NotEqual(PasswordHasher.Hash(password), PasswordHasher.Hash(password));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("v1!!!!")]
    [InlineData("v1AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")] // 长度错误
    public void IsValidFormat_RejectsInvalidValues(string? value) =>
        Assert.False(PasswordHasher.IsValidFormat(value));

    [Fact]
    public void AdminBootstrapHash_InMigrationScript_VerifiesBootstrapPassword()
    {
        var scriptPath = FindMigrationScript();
        var script = File.ReadAllText(scriptPath);
        var match = System.Text.RegularExpressions.Regex.Match(
            script,
            @"DECLARE\s+@AdminBootstrapHash\s+nvarchar\(50\)\s*=\s*N'([^']+)'",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        Assert.True(match.Success, "update.sql 中未找到 @AdminBootstrapHash 声明。");

        var hash = match.Groups[1].Value;
        Assert.True(PasswordHasher.IsValidFormat(hash), "迁移脚本中的 admin 初始哈希必须是 v1 格式。");
        Assert.Equal(50, hash.Length);
        Assert.True(PasswordHasher.Verify(hash, "admin"),
            "迁移脚本中的 admin 初始哈希必须能验证初始密码 admin。");
    }

    private static string FindMigrationScript()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "docs", "migrations", "update.sql");
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }
        throw new FileNotFoundException("未找到 docs/migrations/update.sql。");
    }
}
