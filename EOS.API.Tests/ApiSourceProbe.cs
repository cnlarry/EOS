using System.Text.RegularExpressions;

namespace EOS.API.Tests;

/// <summary>
/// 生产代码（`EOS.API`）的"消费方探针"：统计某个正则在源码里命中的**行数**。
///
/// 口径逐条承接退役门禁 `check-capability-ledger.ps1` 的 `Get-ConsumerHits`：
///   · 排除 `obj` / `bin`；
///   · 排除**声明文件与其控制器**（`DepotStockPolicyService.cs` / `DepotStockPolicyController.cs`）——
///     它们出现某个维度名只说明"这是被声明的维度"，不是消费方；排除后命中即证明运行期真的读了它；
///   · 排除 `Controllers/` 目录；
///   · 保留词边界 `\b`：不加时 `EFFECT_DATE` 会把 `IN_EFFECT_DATE`（另一列的报价生效日期）一并算上，
///     足以把"零消费方"的结论整个翻过来。
///
/// 命中**行数**而非匹配个数，与 `Select-String` 的语义一致（多处命中同一行只算一次）。
/// </summary>
internal static class ApiSourceProbe
{
    /// <summary>声明文件与其控制器：出现维度名不代表消费。</summary>
    private static readonly string[] ExcludedFileNames = { "DepotStockPolicyService.cs", "DepotStockPolicyController.cs" };

    /// <summary>定位仓库根：以 `EOS.API/EOS.API.csproj` 为锚。</summary>
    public static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "EOS.API", "EOS.API.csproj")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Repository root not found (EOS.API/EOS.API.csproj).");
    }

    /// <summary>`EOS.API` 源码目录的绝对路径。</summary>
    public static string ApiRoot() => Path.Combine(FindRepoRoot(), "EOS.API");

    /// <summary>统计命中行数；源码目录缺失时返回 -1（调用方应据此失败，不要静默放行）。</summary>
    public static int CountConsumerHits(string apiRoot, string pattern)
    {
        if (!Directory.Exists(apiRoot)) { return -1; }

        var regex = new Regex(@"\b(?:" + pattern + @")\b");
        var hits = 0;

        foreach (var file in Directory.EnumerateFiles(apiRoot, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (ExcludedFileNames.Contains(Path.GetFileName(file), StringComparer.OrdinalIgnoreCase)) { continue; }
            if (file.Contains($"{Path.DirectorySeparatorChar}Controllers{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)) { continue; }

            foreach (var line in File.ReadLines(file))
            {
                if (regex.IsMatch(line)) { hits++; }
            }
        }
        return hits;
    }
}
