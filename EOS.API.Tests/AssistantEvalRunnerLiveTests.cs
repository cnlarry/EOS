using System.Text.RegularExpressions;
using EOS.API.Data;
using EOS.API.Data.Assistant;
using EOS.API.Features.Assistant.Evaluation;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 评估运行器的真库用例（模块 3107，ADR-046 缺口 #4）：一轮跑完能落抬头与逐用例结果、
/// 结果里的分诊是从用例抄下来的、两轮之间互不覆盖、只保留最近若干轮。
///
/// <para>
/// 用例自己造的运行记录跑完即删（`finally` 里按 RUN_ID 删），不借真实数据、不留残留。
/// </para>
/// </summary>
[Trait("Category", "Integration")]
[Collection("live-database")]
public sealed class AssistantEvalRunnerLiveTests
{
    private static readonly Lazy<string?> ConnectionString = new(ResolveConnectionString);

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

    private static DbConnectionFactory Connections()
    {
        if (ConnectionString.Value is null)
        {
            throw new InvalidOperationException("真库集成测试需要 MSSQL_ERP_CONN；未配置即失败（无连接跳过≠已验证）。");
        }
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:ErpDatabase"] = ConnectionString.Value,
            })
            .Build();
        return new DbConnectionFactory(config);
    }

    [Fact]
    public async Task Run_Writes_Header_Results_And_Copies_Triage()
    {
        var connections = Connections();
        var repository = new AssistantEvalRepository(connections);
        var runner = new AssistantEvalRunner(repository);
        var runId = 0;
        try
        {
            var report = await runner.RunAsync(["diagnosis"], "live-test", CancellationToken.None);
            runId = report.Run.RunId;

            Assert.Equal(AssistantEvalRunner.StatusSucceeded, report.Run.Status);
            Assert.Equal("diagnosis", report.Run.Layers);
            Assert.Equal("live-test", report.Run.RunBy);
            Assert.NotNull(report.Run.FinishedTime);
            Assert.Equal(report.Results.Count, report.Run.CaseTotal);
            Assert.Equal(report.Run.CaseTotal, report.Run.Passed + report.Run.Failed + report.Run.Skipped);

            // 诊断层至少 30 条（黄金集规模），且每条结果都带了分诊
            Assert.True(report.Results.Count >= 30, $"诊断层只跑了 {report.Results.Count} 条，样本面过窄。");
            Assert.All(report.Results, result =>
            {
                Assert.Equal("diagnosis", result.Layer);
                Assert.Contains(result.Outcome,
                    new[] { AssistantEvalRunner.OutcomePass, AssistantEvalRunner.OutcomeFail, AssistantEvalRunner.OutcomeSkip });
                Assert.False(string.IsNullOrWhiteSpace(result.FixAt));
            });

            // 落库读回：抬头与结果行一致（分诊抄在结果行上，不回查用例表）
            var stored = await repository.GetRunAsync(runId, CancellationToken.None);
            Assert.NotNull(stored);
            Assert.Equal(AssistantEvalRunner.StatusSucceeded, stored!.Status);
            Assert.Equal(report.Results.Count, stored.CaseTotal);

            var rows = await repository.LoadResultsAsync(runId, CancellationToken.None);
            Assert.Equal(report.Results.Count, rows.Count);
            Assert.Equal(
                report.Results.OrderBy(result => result.CaseId).Select(result => result.CaseId),
                rows.OrderBy(result => result.CaseId).Select(result => result.CaseId));
        }
        finally
        {
            if (runId > 0) await DeleteRunAsync(connections, runId);
        }
    }

    [Fact]
    public async Task Two_Runs_Do_Not_Overwrite_Each_Other()
    {
        var connections = Connections();
        var repository = new AssistantEvalRepository(connections);
        var runner = new AssistantEvalRunner(repository);
        var runIds = new List<int>();
        try
        {
            var first = await runner.RunAsync(["config"], "live-test", CancellationToken.None);
            runIds.Add(first.Run.RunId);
            var second = await runner.RunAsync(["config"], "live-test", CancellationToken.None);
            runIds.Add(second.Run.RunId);

            Assert.NotEqual(first.Run.RunId, second.Run.RunId);
            var firstRows = await repository.LoadResultsAsync(first.Run.RunId, CancellationToken.None);
            var secondRows = await repository.LoadResultsAsync(second.Run.RunId, CancellationToken.None);
            Assert.Equal(first.Results.Count, firstRows.Count);
            Assert.Equal(second.Results.Count, secondRows.Count);

            // 最近若干轮里两轮都在，且新的在前
            var runs = await repository.ListRunsAsync(50, CancellationToken.None);
            var ours = runs.Where(run => run.RunBy == "live-test").Select(run => run.RunId).ToList();
            Assert.Contains(first.Run.RunId, ours);
            Assert.Contains(second.Run.RunId, ours);
            Assert.True(runs[0].RunId >= runs[^1].RunId);
        }
        finally
        {
            foreach (var id in runIds) await DeleteRunAsync(connections, id);
        }
    }

    [Fact]
    public async Task Empty_Layer_Selection_Is_Rejected()
    {
        var runner = new AssistantEvalRunner(new AssistantEvalRepository(Connections()));
        await Assert.ThrowsAsync<ArgumentException>(
            () => runner.RunAsync(["no_such_layer"], "live-test", CancellationToken.None));
    }

    /// <summary>删自己造的运行记录（结果行随外键级联）。</summary>
    private static async Task DeleteRunAsync(DbConnectionFactory connections, int runId)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM dbo.ASSISTANT_EVAL_RUN WHERE RUN_ID = @RunId;";
        command.Parameters.Add(new Microsoft.Data.SqlClient.SqlParameter("@RunId", runId));
        await command.ExecuteNonQueryAsync();
    }
}
