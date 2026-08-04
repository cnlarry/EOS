using System.Security.Cryptography;
using System.Text;

namespace EOS.API.Security;

public static class LegacyPassword
{
    public static bool Verify(string encrypted, string password)
    {
        try { return FixedTimeEquals(Decrypt(encrypted), password.Trim()); }
        catch (FormatException) { return false; }
    }

    private static string Decrypt(string encrypted)
    {
        const string key = "A9>^";
        var bytes = Convert.FromBase64String(encrypted);
        var text = Encoding.Default.GetString(bytes);
        text = PassportKey(text, key);
        var result = new StringBuilder(text.Length / 2);
        for (var i = 0; i + 1 < text.Length; i += 2)
            result.Append((char)(text[i] ^ text[i + 1]));
        return result.ToString();
    }

    private static string PassportKey(string text, string key)
    {
        var digest = Convert.ToHexString(MD5.HashData(Encoding.Default.GetBytes(key))).ToLowerInvariant();
        var result = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
            result.Append((char)(text[i] ^ digest[i % digest.Length]));
        return result.ToString();
    }

    private static bool FixedTimeEquals(string left, string right)
    {
        var leftBytes = Encoding.UTF8.GetBytes(left);
        var rightBytes = Encoding.UTF8.GetBytes(right);
        return leftBytes.Length == rightBytes.Length && CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }
}
