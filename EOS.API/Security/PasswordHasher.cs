using System.Security.Cryptography;
using System.Text;

namespace EOS.API.Security;

/// <summary>
/// 现代密码哈希。
///
/// 存储格式（写入现有 SYSDL.USER_PWD，nvarchar(50)）：
/// "v1" + Base64(盐[12] || PBKDF2-SHA256(密码, 盐, 210000, 24))
/// 共 2 + 48 = 50 字符，不超出旧表列宽；不新增任何字段。
///
/// 约定：
/// - 空/纯空白密码不允许哈希或验证（登录空输入由 API 层拒绝，空哈希必然校验失败）；
/// - 迭代次数固定在 v1 编码中（不含在存储串里），后续需要升级时换版本前缀并重哈希；
/// - 该实现是唯一密码验证路径，可逆算法已随 ? 删除。
/// </summary>
public static class PasswordHasher
{
    public const string VersionPrefix = "v1";
    public const int SaltSize = 12;
    public const int KeySize = 24;
    public const int Iterations = 210_000;

    public static readonly int EncodedLength = VersionPrefix.Length + Convert.ToBase64String(new byte[SaltSize + KeySize]).Length;

    public static string Hash(string password)
    {
        if (string.IsNullOrEmpty(password) || string.IsNullOrWhiteSpace(password))
            throw new ArgumentException("密码不能为空。", nameof(password));

        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var key = Rfc2898DeriveBytes.Pbkdf2(
            password, salt, Iterations, HashAlgorithmName.SHA256, KeySize);
        var combined = new byte[SaltSize + KeySize];
        Buffer.BlockCopy(salt, 0, combined, 0, SaltSize);
        Buffer.BlockCopy(key, 0, combined, SaltSize, KeySize);
        return VersionPrefix + Convert.ToBase64String(combined);
    }

    public static bool Verify(string? encoded, string? password)
    {
        if (string.IsNullOrEmpty(password) || string.IsNullOrWhiteSpace(password))
            return false;
        if (!TryDecode(encoded, out var salt, out var expectedKey))
            return false;

        var actualKey = Rfc2898DeriveBytes.Pbkdf2(
            password, salt, Iterations, HashAlgorithmName.SHA256, expectedKey.Length);
        return CryptographicOperations.FixedTimeEquals(expectedKey, actualKey);
    }

    /// <summary>判断存量值是否已是现代哈希格式（用于迁移脚本校验与运行时防御）。</summary>
    public static bool IsValidFormat(string? encoded) =>
        TryDecode(encoded, out _, out _);

    private static bool TryDecode(string? encoded, out byte[] salt, out byte[] key)
    {
        salt = [];
        key = [];
        if (string.IsNullOrEmpty(encoded) || !encoded.StartsWith(VersionPrefix, StringComparison.Ordinal))
            return false;

        try
        {
            var combined = Convert.FromBase64String(encoded[VersionPrefix.Length..]);
            if (combined.Length != SaltSize + KeySize)
                return false;
            salt = combined[..SaltSize];
            key = combined[SaltSize..];
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
