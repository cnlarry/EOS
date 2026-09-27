using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// NOLOCK registry enforcement (section 9): new code defaults to no
/// NOLOCK. Files using WITH (NOLOCK) must be registered in NolockAllowlist.txt
/// after review; stale entries must be removed.
/// </summary>
public sealed class NolockAllowlistTests
{
    [Fact]
    public void NolockUsage_MatchesAllowlist()
    {
        var repoRoot = FindRepoRoot();
        var allowlistPath = Path.Combine(repoRoot, "EOS.API.Tests", "NolockAllowlist.txt");
        Assert.True(File.Exists(allowlistPath), $"Allowlist not found: {allowlistPath}");
        var allowed = new HashSet<string>(
            File.ReadAllLines(allowlistPath)
                .Select(line => line.Trim())
                .Where(line => line.Length > 0 && !line.StartsWith('#'))
                .Select(line => line.Replace('/', Path.DirectorySeparatorChar)),
            StringComparer.OrdinalIgnoreCase);

        var apiRoot = Path.Combine(repoRoot, "EOS.API");
        var actual = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(apiRoot, "*.cs", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            if (text.Contains("NOLOCK", StringComparison.OrdinalIgnoreCase))
            {
                actual.Add(Path.GetRelativePath(repoRoot, file));
            }
        }

        var unregistered = actual.Except(allowed, StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToList();
        var stale = allowed.Except(actual, StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToList();
        Assert.True(unregistered.Count == 0,
            $"Files use NOLOCK without allowlist registration: {string.Join("; ", unregistered)}");
        Assert.True(stale.Count == 0,
            $"Allowlist entries no longer use NOLOCK, remove them: {string.Join("; ", stale)}");
    }

    private static string FindRepoRoot()
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
}
