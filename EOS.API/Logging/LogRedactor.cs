using System.Text.RegularExpressions;

namespace EOS.API.Logging;

/// <summary>
/// 日志/审计输出统一脱敏（ADR-005 §5.1/§5.4）：
/// 密码、Token、连接串、Cookie、JWT 等模式一律掩码；不返回附件正文与 LLM 全文
/// （调用方本就不记录）。输出前对所有字符串字段与消息应用。
/// </summary>
public static class LogRedactor
{
    private static readonly Regex[] Patterns =
    [
        new(@"(?i)(password|pwd|passwd|secret|api[_-]?key|token)\s*[=:]\s*[^\s,;""']+", RegexOptions.Compiled),
        new(@"(?i)(Authorization\s*:\s*Bearer\s+)[A-Za-z0-9._~+/=-]+", RegexOptions.Compiled),
        new(@"(?i)(Cookie\s*:\s*)[^;\r\n]+", RegexOptions.Compiled),
        new(@"(?i)(\beyJ[a-zA-Z0-9_-]{10,}\.[a-zA-Z0-9_-]{10,}\.[a-zA-Z0-9_-]{10,})", RegexOptions.Compiled),
        new(@"(?i)([Ss]erver\s*=\s*)[^;,\s]+", RegexOptions.Compiled),
        new(@"(?i)([Pp]assword\s*=\s*)[^;,\s]+", RegexOptions.Compiled),
        new(@"(?i)([Uu]ser\s*[Ii][Dd]\s*=\s*)[^;,\s]+", RegexOptions.Compiled),
        new(@"(?i)([Pp]wd\s*=\s*)[^;,\s]+", RegexOptions.Compiled),
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
