using System.Text.RegularExpressions;

namespace EOS.API.Telemetry;

/// <summary>
/// 调用方标识（clientId）契约（ADR-005 §5.1）：
/// 由调用方经 X-Client-Id 请求头传入，服务端透传并随日志、审计与错误响应返回。
/// 仅接受小写字母/数字/点/下划线/连字符（≤64），非法或缺失统一为 unknown。
/// </summary>
public static class ClientIds
{
    public const string EosWeb = "eos.web";
    public const string EosClient = "eos.client";
    public const string EosAgent = "eos.agent";
    public const string External = "external";
    public const string Unknown = "unknown";

    private static readonly Regex ValidClientId = new("^[A-Za-z0-9_.-]{1,64}$", RegexOptions.Compiled);

    public static string Normalize(string? value) =>
        !string.IsNullOrWhiteSpace(value) && ValidClientId.IsMatch(value) ? value.Trim() : Unknown;
}
