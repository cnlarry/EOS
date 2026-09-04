using System.Text;

namespace EOS.API.Features.Assistant.Kb;

/// <summary>
/// Chinese semantic chunker: paragraph boundaries first, then sentence boundaries
/// （。！？；), hard length split only for overlong single sentences.
/// Overlap keeps neighboring context for recall.
/// </summary>
public static class KbChunker
{
    public const int DefaultMaxChars = 700;
    public const int DefaultOverlapChars = 100;

    private static readonly char[] SentenceEndings = ['。', '！', '？', ';', '；', '\n'];

    public static IReadOnlyList<string> Split(
        string content, int maxChars = DefaultMaxChars, int overlapChars = DefaultOverlapChars)
    {
        var normalized = (content ?? string.Empty).Replace("\r\n", "\n").Trim();
        if (normalized.Length == 0) return [];
        maxChars = Math.Max(100, maxChars);
        overlapChars = Math.Clamp(overlapChars, 0, maxChars / 2);

        var units = new List<string>();
        foreach (var paragraph in normalized.Split(["\n\n"], StringSplitOptions.RemoveEmptyEntries))
        {
            var text = paragraph.Trim();
            if (text.Length == 0) continue;
            if (text.Length <= maxChars)
            {
                units.Add(text);
                continue;
            }

            units.AddRange(SplitSentences(text, maxChars));
        }

        var chunks = new List<string>();
        var current = new StringBuilder();
        foreach (var unit in units)
        {
            if (current.Length + unit.Length + 1 <= maxChars)
            {
                if (current.Length > 0) current.Append('\n');
                current.Append(unit);
                continue;
            }

            if (current.Length > 0) chunks.Add(current.ToString());
            current.Clear().Append(unit);
        }

        if (current.Length > 0) chunks.Add(current.ToString());
        if (overlapChars == 0 || chunks.Count <= 1) return chunks;

        var overlapped = new List<string> { chunks[0] };
        for (var index = 1; index < chunks.Count; index++)
        {
            var tail = chunks[index - 1];
            var prefix = tail.Length <= overlapChars ? tail : tail[^overlapChars..];
            overlapped.Add(prefix + "\n" + chunks[index]);
        }

        return overlapped;
    }

    private static IEnumerable<string> SplitSentences(string text, int maxChars)
    {
        var current = new StringBuilder();
        foreach (var ch in text)
        {
            current.Append(ch);
            if (Array.IndexOf(SentenceEndings, ch) >= 0 && current.Length >= maxChars / 3)
            {
                yield return current.ToString().Trim();
                current.Clear();
            }

            while (current.Length >= maxChars)
            {
                yield return current.ToString(0, maxChars);
                current.Remove(0, maxChars);
            }
        }

        if (current.Length > 0) yield return current.ToString().Trim();
    }
}
