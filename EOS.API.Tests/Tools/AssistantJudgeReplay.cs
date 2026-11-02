using System.Text.Json;
using System.Text.RegularExpressions;
using EOS.API.Data;
using EOS.API.Data.Assistant;
using EOS.API.Features.Assistant.ModelAccess;
using EOS.API.Features.Assistant.Parameters;
using EOS.API.Features.Assistant.Quality;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 判官 J0 的真实回放（ADR-044 §11.5「第一次运行的最小闭环」）：取最近 N 条已落库问答，
/// 真调一次判官模型，打印六条四态与证据，并汇总四个数字。
///
/// <para>
/// **手动跑，不进 CI**：它要连真库、要调模型（产生费用）。CI 的测试过滤是
/// <c>Category!=Integration&amp;Category!=Tool</c>，本类标 <c>Tool</c>，所以不会被自动执行。
/// </para>
///
/// <para>
/// 前置：<c>$env:MSSQL_ERP_CONN</c> 指向业务库；3102 里已有可用的对话模型（或判官模型）且密钥环境变量生效。
/// 跑法：<c>dotnet test EOS.API.Tests/EOS.API.Tests.csproj --filter FullyQualifiedName~AssistantJudgeReplay</c>
/// </para>
/// </summary>
[Trait("Category", "Tool")]
public sealed class AssistantJudgeReplay
{
    private static readonly Lazy<string?> ConnectionString = new(ResolveConnectionString);

    [Fact]
    public async Task Replay_Recent_Answers_And_Print_Baseline()
    {
        if (ConnectionString.Value is null)
        {
            throw new InvalidOperationException("回放需要 MSSQL_ERP_CONN（真库）；未配置即失败，不静默跳过。");
        }

        var connections = PolicyServiceFactory.Connections(ConnectionString.Value);
        var catalog = new AssistantModelCatalog(connections);
        var resolver = new AssistantParameterResolver(
            connections, NullLogger<AssistantParameterResolver>.Instance);
        var registry = new AssistantRuntimeRegistry(
            catalog, resolver, new EnvironmentSecretStore(), NullLogger<AssistantRuntimeRegistry>.Instance);
        await registry.RefreshAsync(CancellationToken.None);

        var repository = new AssistantJudgeRepository(connections);
        var limits = new AssistantJudgeLimitsOptions();
        var transcripts = await repository.LoadRecentAnswersAsync(3, limits, CancellationToken.None);
        Assert.NotEmpty(transcripts);

        var judge = new AssistantAnswerJudge(new AssistantJudgeModel(catalog, registry, new StubHttpClientFactory()));
        var outcomes = new List<AssistantJudgeOutcome>();
        foreach (var transcript in transcripts)
        {
            outcomes.Add(await judge.JudgeAsync(transcript, limits, CancellationToken.None));
        }

        // 打印到测试输出：这一份就是 J0 的首次基线证据
        var lines = new List<string> { "== 判官 J0 回放 ==" };
        foreach (var outcome in outcomes)
        {
            lines.Add($"message#{outcome.MessageId} rubric={outcome.RubricVersion} judge={outcome.JudgeModel}"
                + (outcome.FellBackToChat ? "（回落到对话模型）" : string.Empty));
            foreach (var verdict in outcome.Verdicts)
            {
                lines.Add($"  {verdict.Clause} {verdict.Verdict}  证据：{verdict.Evidence ?? "（无）"}"
                    + (string.IsNullOrWhiteSpace(verdict.Note) ? string.Empty : $"  备注：{verdict.Note}"));
            }
        }

        var metrics = AssistantJudgeMetrics.From(outcomes);
        lines.Add($"聚合：判 {metrics.Judged} 条；口径标注率 {Format(metrics.CitationRate)}；"
            + $"范围一致率 {Format(metrics.ScopeRate)}；越界承诺 {metrics.OverreachCount}；"
            + $"无法判定率 {Format(metrics.UnsureRate)}");
        foreach (var stat in metrics.Clauses)
        {
            lines.Add($"  {stat.Clause} {stat.Title}：PASS {stat.Pass} / FAIL {stat.Fail}"
                + $" / NA {stat.Na} / UNSURE {stat.Unsure}");
        }

        // 与既有 Tools 运行器同一做法：结果落 logs/ 并打到控制台（`dotnet test` 默认吞掉输出，
        // 只靠控制台会什么都看不到——而这份输出就是 J0 的首次基线证据）。
        var directory = Path.Combine(FindRepoRoot(), "logs", "judge");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"replay-{DateTime.Now:yyyyMMdd-HHmmss}.md");
        await File.WriteAllTextAsync(path, string.Join(Environment.NewLine, lines));
        Console.WriteLine(string.Join(Environment.NewLine, lines));
        Console.WriteLine($"基线已写入 {path}");

        // 结构断言：每条都有六条结论；PASS/FAIL 必须带证据（解析器已经强制，这里再钉一次）
        foreach (var outcome in outcomes)
        {
            Assert.Equal(AssistantJudgeRubric.Current.Clauses.Count, outcome.Verdicts.Count);
            Assert.All(outcome.Verdicts.Where(v => v.Verdict is "PASS" or "FAIL"),
                verdict => Assert.False(string.IsNullOrWhiteSpace(verdict.Evidence)));
        }
    }

    private static string Format(double? rate) => rate is null ? "—" : $"{rate.Value:P1}";

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

    private static string? ResolveConnectionString()
    {
        var env = Environment.GetEnvironmentVariable("MSSQL_ERP_CONN");
        if (!string.IsNullOrWhiteSpace(env)) return env;
        try
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".codex", "config.toml");
            var text = File.ReadAllText(path);
            var match = Regex.Match(text,
                "\\[mcp_servers\\.mssql\\.env\\][\\s\\S]*?MSSQL_ERP_CONN\\s*=\\s*\"([^\"]+)\"");
            return match.Success && match.Groups[1].Value.Contains("Database=EOS.ERP")
                ? match.Groups[1].Value
                : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>密钥从环境变量读（与生产同一口径：库里只存变量名）。</summary>
    private sealed class EnvironmentSecretStore : IAssistantSecretStore
    {
        public string? Read(string? envVarName) =>
            string.IsNullOrWhiteSpace(envVarName)
                ? null
                : Environment.GetEnvironmentVariable(envVarName);

        public bool IsConfigured(string? envVarName) => !string.IsNullOrEmpty(Read(envVarName));

        public string? MaskedTail(string? envVarName)
        {
            var value = Read(envVarName);
            return string.IsNullOrEmpty(value) || value.Length <= 4 ? null : value[^4..];
        }

        // 回放只读密钥：写入口在这里没有意义，明确不做并如实返回"未写入"。
        public (bool ProcessUpdated, bool Persisted) Write(string envVarName, string secret) => (false, false);
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
