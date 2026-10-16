using EOS.API.Features.Assistant.Situation;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 常驻处境预算：默认值可配、超限硬截断、截断可观测（不静默丢内容）。
/// 代码内不写死阈值，故这里也断言"默认值来自选项对象"。
///
/// 截断是**设计内的降级路径**（每次装配情境都可能发生），所以按 `Debug` 记——
/// 记 Warning 会让每个助手请求都往运行日志里落几条，把真正的异常淹掉
/// （判据见 `docs/guide/31-运行日志与诊断.md`「什么样的情形才配落 Warning」）。
/// </summary>
public sealed class AssistantSituationBudgetTests
{
    private sealed class RecordingLogger : ILogger<AssistantSituationBudget>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }

    [Fact]
    public void 默认预算给出可用的常驻与分段上限()
    {
        var options = new AssistantSituationBudgetOptions();

        Assert.Equal(300, options.ResidentTokenLimit);
        Assert.Equal(200, options.IdentityTokenLimit);
        Assert.Equal(80, options.PendingTokenLimit);
        Assert.Equal(10, options.MaxFilters);
        Assert.Equal(20, options.MaxSelection);
        Assert.Equal(20, options.MaxDirtyFields);
        Assert.True(options.IdentityTokenLimit + options.PendingTokenLimit <= options.ResidentTokenLimit);
    }

    [Fact]
    public void 配置可覆盖默认值()
    {
        var budget = AssistantSituationDoubles.Budget(options => options.ResidentTokenLimit = 120);

        Assert.Equal(120, budget.Limits.ResidentTokenLimit);
    }

    [Fact]
    public void 超出条数上限时截断并记降级日志()
    {
        var logger = new RecordingLogger();
        var budget = new AssistantSituationBudget(
            Options.Create(new AssistantSituationBudgetOptions { MaxFilters = 3 }),
            logger);

        var kept = budget.Take("filters", new[] { 1, 2, 3, 4, 5 }, budget.Limits.MaxFilters);

        Assert.Equal(3, kept.Count);
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Debug, entry.Level);
        Assert.Contains("filters", entry.Message);
    }

    [Fact]
    public void 超出token预算时硬截断并记降级日志()
    {
        var logger = new RecordingLogger();
        var budget = new AssistantSituationBudget(
            Options.Create(new AssistantSituationBudgetOptions()), logger);

        var truncated = budget.Truncate("resident", new string('字', 500), 100);

        Assert.Equal(100, truncated.Length);
        Assert.Equal(LogLevel.Debug, Assert.Single(logger.Entries).Level);
    }

    [Fact]
    public void 按行合并时整行丢弃而不是切半行()
    {
        var logger = new RecordingLogger();
        var budget = new AssistantSituationBudget(
            Options.Create(new AssistantSituationBudgetOptions()), logger);

        var text = budget.JoinLines("identity", ["- 第一行", "- 第二行很长很长很长", "- 第三行"], 20);

        Assert.Contains("第一行", text);
        Assert.Contains("第二行很长很长很长", text); // 整行放得下就整行保留
        Assert.DoesNotContain("第三行", text);       // 放不下的整行丢弃，不切半行
        Assert.Equal(LogLevel.Debug, Assert.Single(logger.Entries).Level);
    }

    [Fact]
    public void token估算按字符数保守计()
    {
        Assert.Equal(5, AssistantSituationBudget.EstimateTokens("12345"));
        Assert.Equal(0, AssistantSituationBudget.EstimateTokens(null));
    }
}
