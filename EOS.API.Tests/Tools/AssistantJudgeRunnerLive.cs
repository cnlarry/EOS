using System.Text.RegularExpressions;
using EOS.API.Data;
using EOS.API.Data.Assistant;
using EOS.API.Features.Assistant.Governance;
using EOS.API.Features.Assistant.ModelAccess;
using EOS.API.Features.Assistant.Parameters;
using EOS.API.Features.Assistant.Quality;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 判官 J1 的**真跑一轮**（ADR-044 §11.5）：真库 + 真模型，判几条、落库、记账，再把四个数字打出来。
///
/// <para>
/// **手动跑，不进 CI**：它要连真库、要调模型（产生费用），标 <c>Category=Tool</c>，
/// CI 的过滤是 <c>Category!=Integration&amp;Category!=Tool</c>。
/// </para>
///
/// <para>
/// 与回放器（<see cref="AssistantJudgeReplay"/>）的分工：那个只打印不落库，这个走**生产同一条链路**
/// （候选查询 → 抽样 → 判 → 落库 → 记系统账户），因此能验到"库里真的多了一行结论"。
/// 抽样在这里被固定成 100%，否则小样本下大概率一条都不判、看不到效果。
/// </para>
///
/// <para>
/// 跑法：<c>dotnet test EOS.API.Tests/EOS.API.Tests.csproj --filter FullyQualifiedName~AssistantJudgeRunnerLive</c>，
/// 前置 <c>$env:MSSQL_ERP_CONN</c> 与当前判官模型（或对话模型）的密钥环境变量。
/// </para>
/// </summary>
[Trait("Category", "Tool")]
public sealed class AssistantJudgeRunnerLive
{
    private static readonly Lazy<string?> ConnectionString = new(ResolveConnectionString);

    [Fact]
    public async Task Run_One_Batch_Persist_And_Print_Metrics()
    {
        var connectionString = ConnectionString.Value
            ?? throw new InvalidOperationException("需要 MSSQL_ERP_CONN（真库）；未配置即失败，不静默跳过。");

        var connections = PolicyServiceFactory.Connections(connectionString);
        var catalog = new AssistantModelCatalog(connections);
        var resolver = new AssistantParameterResolver(
            connections, NullLogger<AssistantParameterResolver>.Instance);
        var registry = new AssistantRuntimeRegistry(
            catalog, resolver, new EnvironmentSecretStore(), NullLogger<AssistantRuntimeRegistry>.Instance);
        await registry.RefreshAsync(CancellationToken.None);

        var judgeRepository = new AssistantJudgeRepository(connections);
        var verdictRepository = new AssistantVerdictRepository(connections);
        var usageRepository = new AssistantUsageRepository(connections);
        var judge = new AssistantAnswerJudge(new AssistantJudgeModel(catalog, registry, new StubHttpClientFactory()));

        // 抽样固定 100%、批量 2、窗口 30 天：小样本下要"一定判得到"，否则验不出落库
        var limits = new AssistantJudgeLimitsOptions
        {
            SampleRatePct = 100, AmountSampleRatePct = 100, BatchSize = 2, WindowDays = 30,
        };
        var runner = new AssistantJudgeRunner(
            judgeRepository, verdictRepository, usageRepository,
            new FixedParameters(AssistantPolicyValues.Default with { Judge = limits }),
            judge, NullLogger<AssistantJudgeRunner>.Instance);

        var result = await runner.RunAsync(2, CancellationToken.None);

        var since = DateTimeOffset.UtcNow.AddDays(-30);
        var summary = await verdictRepository.LoadSummaryAsync(since, CancellationToken.None);
        var today = await usageRepository.GetLedgerDayAsync(
            AssistantJudgeAccounts.SystemUserId, new DateTimeOffset(DateTime.UtcNow.Date, TimeSpan.Zero),
            CancellationToken.None);
        var block = EOS.API.Features.Assistant.Admin.AssistantAnswerQualityView.Build(summary, today, limits);

        var lines = new List<string>
        {
            "== 判官 J1 真跑一轮 ==",
            $"候选 {result.Candidates} / 判了 {result.Judged} / 抽样跳过 {result.SkippedBySampling}"
                + $" / 上限跳过 {result.SkippedByCap} / 失败 {result.Failed} / 本轮花费 {result.CostYuan:0.######} 元",
            result.StopReason is null ? "未触发上限" : $"提前结束：{result.StopReason}",
            $"库内近 30 天：判过 {block.Judged} 条回答、{summary.VerdictRows} 行结论；"
                + $"口径标注率 {Format(block.CitationRate)}；范围一致率 {Format(block.ScopeRate)}；"
                + $"越界承诺 {block.OverreachCount}；无法判定率 {Format(block.UnsureRate)}",
            $"判定花费：近 30 天 {block.SpentYuan:0.######} 元（{block.Calls} 次）；"
                + $"今日 {block.DailySpentYuan:0.######} 元（{block.DailyCalls}/{block.DailyMaxCalls} 次，"
                + $"上限 {block.DailyCostCapYuan:0.##} 元）",
            $"判官模型：{(block.JudgeModels.Count == 0 ? "（无）" : string.Join("、", block.JudgeModels))}；条款版本 {block.RubricVersion}",
            "条款明细：",
        };
        lines.AddRange(block.Clauses.Select(clause =>
            $"  {clause.Clause} {clause.Title}：PASS {clause.Pass} / FAIL {clause.Fail}"
            + $" / NA {clause.Na} / UNSURE {clause.Unsure}"));

        var directory = Path.Combine(FindRepoRoot(), "logs", "judge");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"j1-run-{DateTime.Now:yyyyMMdd-HHmmss}.md");
        await File.WriteAllTextAsync(path, string.Join(Environment.NewLine, lines));
        Console.WriteLine(string.Join(Environment.NewLine, lines));
        Console.WriteLine($"已写入 {path}");

        // 判定链路本身不能有技术失败；没候选也算通过（全部已判过是正常状态）
        Assert.Equal(0, result.Failed);
    }

    private static string Format(double? rate) => rate is null ? "—" : $"{rate.Value:P1}";

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "EOS.API", "EOS.API.csproj"))) return dir.FullName;
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
            var match = Regex.Match(File.ReadAllText(path),
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

    /// <summary>只在全局层取参数：判分是系统行为，不叠加任何人的作用域覆盖。</summary>
    private sealed class FixedParameters(AssistantPolicyValues values) : IAssistantEffectiveParameters
    {
        public Task<AssistantPolicyValues> ForAsync(string userId, int? moduleId, CancellationToken token) =>
            Task.FromResult(values);
    }

    /// <summary>密钥从环境变量读（与生产同一口径：库里只存变量名）。</summary>
    private sealed class EnvironmentSecretStore : IAssistantSecretStore
    {
        public string? Read(string? envVarName) =>
            string.IsNullOrWhiteSpace(envVarName) ? null : Environment.GetEnvironmentVariable(envVarName);

        public bool IsConfigured(string? envVarName) => !string.IsNullOrEmpty(Read(envVarName));

        public string? MaskedTail(string? envVarName)
        {
            var value = Read(envVarName);
            return string.IsNullOrEmpty(value) || value.Length <= 4 ? null : value[^4..];
        }

        public (bool ProcessUpdated, bool Persisted) Write(string envVarName, string secret) => (false, false);
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
