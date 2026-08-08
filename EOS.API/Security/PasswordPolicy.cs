namespace EOS.API.Security;

/// <summary>
/// 密码强度策略（服务端唯一权威校验，供管理员设置密码与用户自助改密共用）。
/// </summary>
public static class PasswordPolicy
{
    public const int MinLength = 8;
    public const int MaxLength = 64;

    public static void Validate(string password)
    {
        if (string.IsNullOrEmpty(password) || password.Length < MinLength || password.Length > MaxLength)
            throw new ArgumentException($"密码长度必须在 {MinLength}-{MaxLength} 个字符之间。", nameof(password));
        if (!password.Equals(password.Trim(), StringComparison.Ordinal))
            throw new ArgumentException("密码不能以空格开头或结尾。", nameof(password));
    }
}
