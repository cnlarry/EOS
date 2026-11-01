using System.Reflection;
using System.Text.Json;

namespace EOS.API.Features.Assistant.Quality;

/// <summary>一条判定条款：编号、标题、要回答的问题、判据出处。</summary>
public sealed record AssistantJudgeClause(string Code, string Title, string Question, string Source);

/// <summary>
/// 版本化的判定条款（rubric）。
///
/// <para>
/// 条款文本是 `AGENTS.md` 助手红线的镜像——**改文本就是改契约**：必须同批回改 `AGENTS.md` 与
/// ADR-044 §2.1，并升版本号。版本号会写进每条 verdict，所以"这条结论是拿哪一版条款判的"永远查得到。
/// </para>
///
/// <para>
/// 文件以嵌入资源随程序集发布（`Quality/rubric.v*.json`）：它必须在任何运行环境里都读得到，
/// 不依赖工作目录或部署时是否复制了某个文件——判官拿不到条款就不能判，这不该是一个部署问题。
/// </para>
/// </summary>
public sealed record AssistantJudgeRubric(string Version, IReadOnlyList<AssistantJudgeClause> Clauses)
{
    /// <summary>当前随代码发布的版本。判分记录用它，不用"文件里写着什么"。</summary>
    public const string CurrentVersion = "v1";

    private static readonly Lazy<AssistantJudgeRubric> Cached = new(() => Load(CurrentVersion));

    /// <summary>当前条款（进程内只读一次）。</summary>
    public static AssistantJudgeRubric Current => Cached.Value;

    /// <summary>按版本号读嵌入资源；缺文件或条款为空直接抛（判官不能带着半份条款跑）。</summary>
    public static AssistantJudgeRubric Load(string version)
    {
        var name = $"EOS.API.Features.Assistant.Quality.rubric.{version}.json";
        var assembly = typeof(AssistantJudgeRubric).Assembly;
        using var stream = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"判定条款资源缺失：{name}。判官不能在没有条款的情况下运行。");

        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;
        var declared = root.TryGetProperty("version", out var versionElement)
            ? versionElement.GetString()
            : null;
        if (!string.Equals(declared, version, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"条款文件声明版本 {declared}，与加载的 {version} 不一致。");
        }

        var clauses = new List<AssistantJudgeClause>();
        foreach (var item in root.GetProperty("clauses").EnumerateArray())
        {
            clauses.Add(new AssistantJudgeClause(
                item.GetProperty("code").GetString() ?? string.Empty,
                item.GetProperty("title").GetString() ?? string.Empty,
                item.GetProperty("question").GetString() ?? string.Empty,
                item.GetProperty("source").GetString() ?? string.Empty));
        }

        if (clauses.Count == 0)
        {
            throw new InvalidOperationException("条款文件里没有任何条款：判官无事可判，视为配置错误。");
        }

        var duplicates = clauses.GroupBy(clause => clause.Code, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToList();
        if (duplicates.Count > 0)
        {
            throw new InvalidOperationException($"条款编号重复：{string.Join("、", duplicates)}。");
        }

        return new AssistantJudgeRubric(version, clauses);
    }

    /// <summary>嵌入资源的实际名字（排障用：资源名拼错时把它打印出来对照）。</summary>
    public static IReadOnlyList<string> ResourceNames() =>
        typeof(AssistantJudgeRubric).Assembly
            .GetManifestResourceNames()
            .Where(name => name.Contains(".Quality.rubric.", StringComparison.Ordinal))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();
}
