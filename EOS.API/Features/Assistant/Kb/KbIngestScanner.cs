using System.Text.Json;
using System.Text.RegularExpressions;

namespace EOS.API.Features.Assistant.Kb;

/// <summary>
/// red-line scanner: sensitive-data blocking + business-reference extraction.
/// Pure and unit-tested; the only place that decides what a "sensitive kind" or a
/// "module + _keys reference" is (rule doc: docs/plans/KB-入库扫描规则.md).
/// </summary>
public sealed record BusinessReference(int ModuleId, IReadOnlyList<string> Keys);

public sealed record KbScanResult(
    IReadOnlyList<string> SensitiveKinds, IReadOnlyList<BusinessReference> References);

public static class KbIngestScanner
{
    private static readonly (string Kind, Regex Pattern)[] SensitivePatterns =
    [
        ("ID_CARD", new(@"\d{17}[\dXx]", RegexOptions.Compiled)),
        ("BANK_CARD", new(@"\d{16,19}", RegexOptions.Compiled)),
        ("MOBILE", new(@"1\d{10}", RegexOptions.Compiled)),
        ("CREDENTIAL", new(@"(?i)(password|passwd|pwd|secret|api[_-]?key|token)\s*[:=]", RegexOptions.Compiled)),
    ];

    private static readonly Regex ModulePattern =
        new(@"module\s*=\s*(\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex KeysPattern =
        new(@"_keys\s*=\s*\[(.*?)\]", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    public static KbScanResult Scan(string content)
    {
        var text = content ?? string.Empty;
        var kinds = new List<string>();
        foreach (var (kind, pattern) in SensitivePatterns)
        {
            if (pattern.IsMatch(text) && !kinds.Contains(kind)) kinds.Add(kind);
        }

        return new(kinds, ExtractReferences(text));
    }

    /// <summary>
    /// Extract module + _keys references: each module marker pairs with the next
    /// unconsumed _keys marker after it. Unpairable markers are ignored (fail-closed
    /// at the call site: callers treat unparseable content as unreviewable).
    /// </summary>
    public static IReadOnlyList<BusinessReference> ExtractReferences(string text)
    {
        var modules = ModulePattern.Matches(text ?? string.Empty);
        var keys = KeysPattern.Matches(text ?? string.Empty);
        var result = new List<BusinessReference>();
        var keyIndex = 0;
        foreach (Match module in modules)
        {
            if (!int.TryParse(module.Groups[1].Value, out var moduleId) || moduleId <= 0) continue;
            while (keyIndex < keys.Count && keys[keyIndex].Index < module.Index) keyIndex++;
            if (keyIndex >= keys.Count) break;
            var parsed = ParseKeys(keys[keyIndex].Groups[1].Value);
            keyIndex++;
            if (parsed.Count == 0 || parsed.Any(string.IsNullOrEmpty)) continue;
            result.Add(new(moduleId, parsed));
        }

        return result;
    }

    private static IReadOnlyList<string> ParseKeys(string inner)
    {
        try
        {
            using var document = JsonDocument.Parse("[" + inner + "]");
            if (document.RootElement.ValueKind != JsonValueKind.Array) return [];
            var keys = new List<string>();
            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.String) return [];
                keys.Add(element.GetString() ?? string.Empty);
            }

            return keys;
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
