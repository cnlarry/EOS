using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;

namespace EOS.API.Configuration;

/// <summary>
/// 配置值里的环境变量引用解析：把 <c>${VAR}</c> 替换成环境变量的值。
///
/// 配置文件里只出现引用名（因此可以入库），真实值只存在于环境变量。未定义的环境变量
/// **解析为空串**并把「哪个键引用了哪个变量」记进 issues，由启动时的 Warning 点名——
/// 语义上等于「未配置」，而不是把 <c>${VAR}</c> 字面量当值使用（后者会让连接串/密钥
/// 以字面量身份静默失败）。空值随后由既有检查点接住：连接串为空时 /health/ready 报 Unhealthy。
/// </summary>
public static partial class EnvironmentReferenceResolver
{
    /// <summary>引用形态：<c>${NAME}</c>，NAME 为字母或下划线开头。同一值内可出现多个引用。</summary>
    [GeneratedRegex(@"\$\{([A-Za-z_][A-Za-z0-9_]*)\}")]
    private static partial Regex ReferencePattern();

    /// <summary>
    /// 扫描配置树，返回**只有值发生变化**的键值对（可直接 AddInMemoryCollection 追加到配置链末尾）。
    /// 不含引用的值原样跳过，因此不会改变其它配置项的来源与优先级。
    /// </summary>
    /// <param name="configuration">已装载全部配置源（含环境变量）的配置。</param>
    /// <param name="issues">承接未定义引用的说明文本，供启动日志点名。</param>
    /// <param name="variableReader">读取环境变量的入口；仅测试注入用，缺省读进程环境。</param>
    public static IReadOnlyDictionary<string, string?> Resolve(
        IConfiguration configuration,
        ICollection<string> issues,
        Func<string, string?>? variableReader = null)
    {
        var read = variableReader ?? Environment.GetEnvironmentVariable;
        var resolved = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        foreach (var pair in configuration.AsEnumerable())
        {
            var value = pair.Value;
            if (string.IsNullOrEmpty(value) || !value.Contains("${", StringComparison.Ordinal))
            {
                continue;
            }

            var replaced = ReferencePattern().Replace(value, match =>
            {
                var name = match.Groups[1].Value;
                var assigned = read(name);
                if (assigned is null)
                {
                    issues.Add($"{pair.Key} 引用了未定义的环境变量 {name}，已按空值处理");
                    return string.Empty;
                }

                return assigned;
            });

            if (!string.Equals(replaced, value, StringComparison.Ordinal))
            {
                resolved[pair.Key] = replaced;
            }
        }

        return resolved;
    }
}
