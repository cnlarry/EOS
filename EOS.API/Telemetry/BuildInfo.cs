using System.Reflection;

namespace EOS.API.Telemetry;

/// <summary>
/// 构建元数据读取：把编译期写入程序集的提交号、构建时间与产品版本收敛到一处，
/// 供 `/health/version`、诊断包（`diagnostics.json`）与日志管理页共用同一份读数，
/// 避免"端点报一个、诊断包报另一个"。
/// 无 `.git` 的构建（发布包解压部署）会缺 GitCommit/BuildTimeUtc，如实返回 unknown。
/// </summary>
public static class BuildInfo
{
    public const string Unknown = "unknown";

    public static string? Read(Assembly assembly, string key) =>
        assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => string.Equals(attribute.Key, key, StringComparison.Ordinal))?.Value;

    /// <summary>产品版本：优先 version.json 派生的信息版（形如 0.1.0+&lt;sha&gt;），缺失时回落程序集版本。</summary>
    public static string ProductVersion(Assembly assembly) =>
        assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? assembly.GetName().Version?.ToString()
        ?? Unknown;

    public static string Commit(Assembly assembly) => Read(assembly, "GitCommit") ?? Unknown;

    public static string BuildTimeUtc(Assembly assembly) => Read(assembly, "BuildTimeUtc") ?? Unknown;

    /// <summary>
    /// 进程启动早于二进制构建时间 ⇒ 运行中的进程不是当前构建（部署漂移，需重启）。
    /// 构建时间缺失时无法判定，返回 false 而不是猜。
    /// </summary>
    public static bool IsStale(string builtUtc, DateTime processStartUtc) =>
        DateTime.TryParse(builtUtc, null, System.Globalization.DateTimeStyles.AdjustToUniversal, out var built)
        && processStartUtc < built;
}
