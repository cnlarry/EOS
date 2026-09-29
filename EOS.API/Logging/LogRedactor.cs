using System.Text.RegularExpressions;

namespace EOS.API.Logging;

/// <summary>
/// 日志/审计输出统一脱敏：
/// 密码、Token、连接串、Cookie、JWT 等模式一律掩码；不返回附件正文与 LLM 全文
/// （调用方本就不记录）。输出前对所有字符串字段与消息应用。
/// </summary>
public static class LogRedactor
{
    /// <summary>
    /// 凭据值的合法字符集：字母数字与常见符号。
    /// **刻意不用 `[^\s,;]+` 这类"排除法"**——中文标点（如全角逗号）不在排除集里，
    /// 会把凭据后面整段中文说明一起吃掉（实测：`Password=x！后面的话` 被整段替换成 `***`），
    /// 而这正是脱敏最该避免的"误伤"：排障信息没了，只因为一句口令。
    /// </summary>
    private const string ValueChars = @"[A-Za-z0-9._~+/=@!$%^&*()\[\]{}|:?<>-]{1,256}";

    private static readonly Regex[] Patterns =
    [
        new(@"(?i)(password|pwd|passwd|secret|api[_-]?key|token)\s*[=:]\s*" + ValueChars, RegexOptions.Compiled),
        new(@"(?i)(Authorization\s*:\s*Bearer\s+)[" + ValueChars[1..], RegexOptions.Compiled),
        new(@"(?i)(Cookie\s*:\s*)[^;\r\n]+", RegexOptions.Compiled),
        new(@"(?i)(\beyJ[a-zA-Z0-9_-]{10,}\.[a-zA-Z0-9_-]{10,}\.[a-zA-Z0-9_-]{10,})", RegexOptions.Compiled),
        new(@"(?i)([Ss]erver\s*=\s*)" + ValueChars, RegexOptions.Compiled),
        new(@"(?i)([Pp]assword\s*=\s*)" + ValueChars, RegexOptions.Compiled),
        new(@"(?i)([Uu]ser\s*[Ii][Dd]\s*=\s*)" + ValueChars, RegexOptions.Compiled),
        new(@"(?i)([Pp]wd\s*=\s*)" + ValueChars, RegexOptions.Compiled),
    ];

    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }
        var result = text;
        foreach (var pattern in Patterns)
        {
            result = pattern.Replace(result, "$1***");
        }
        return result;
    }
}
