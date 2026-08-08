using EOS.API.Security;
using Xunit;

namespace EOS.API.Tests;

public sealed class PasswordPolicyTests
{
    [Theory]
    [InlineData("Abcdef12!")]
    [InlineData("abcdefgh")]
    [InlineData("ab cd ef")] // 长度 8，允许中间空格
    [InlineData("密码长度八位a1")]
    public void Validate_AcceptsValidPasswords(string password) =>
        PasswordPolicy.Validate(password);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1234567")]               // 过短
    [InlineData("01234567890123456789012345678901234567890123456789012345678901234")] // 65 位，过长
    [InlineData(" leading1")]             // 前导空格
    [InlineData("trailing1 ")]            // 尾随空格
    public void Validate_RejectsInvalidPasswords(string? password)
    {
        if (password is null)
            Assert.Throws<ArgumentException>(() => PasswordPolicy.Validate(password!));
        else
            Assert.Throws<ArgumentException>(() => PasswordPolicy.Validate(password));
    }
}
