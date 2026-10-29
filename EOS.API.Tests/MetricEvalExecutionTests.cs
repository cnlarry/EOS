using System.Data;
using System.Text.Json;
using EOS.API.Data;
using Microsoft.Extensions.Configuration;
using EOS.API.Features.Assistant.Metrics;
using Microsoft.Data.SqlClient;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// Upgrades the inference eval set from declarative source checks to real
/// execution: every resolve_metric source a sample declares must exist, be
/// business-confirmed, pass the controlled validator, and execute against the
/// live database without a definition-level refusal. This keeps the eval corpus
/// and the semantic layer from drifting apart (a ghost-column metric referenced
/// by a sample fails here instead of at answer time).
/// </summary>
[Trait("Category", "Integration")]
public sealed class MetricEvalExecutionTests
{
    private static string? TestConnection() =>
        Environment.GetEnvironmentVariable("MSSQL_ERP_CONN");

    private static string RequireConnection() =>
        TestConnection()
        ?? throw new InvalidOperationException(
            "评估集真实执行校验需要 MSSQL_ERP_CONN；未配置即失败（无连接跳过≠已验证）。");

    private static string EvalSeedPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "AssistantEval", "inference", "seed-01.jsonl");
            if (File.Exists(candidate)) return candidate;
            var nested = Path.Combine(directory.FullName, "EOS.API.Tests", "AssistantEval", "inference", "seed-01.jsonl");
            if (File.Exists(nested)) return nested;
            directory = directory.Parent;
        }
        throw new FileNotFoundException("评估集缺失：inference/seed-01.jsonl");
    }

    [Fact]
    public async Task Inference_Corpus_Metric_Sources_Are_Confirmed_And_Executable()
    {
        var connectionString = RequireConnection();
        var factory = ConnectionFactory(connectionString);
        var repository = new MetricRepository(factory);
        var probe = new SysMetricSchemaProbe(factory);
        var validator = new MetricDefinitionValidator(probe);
        var executor = new MetricExecutor(factory);

        var failures = new List<string>();
        var checkedSources = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in await File.ReadAllLinesAsync(EvalSeedPath()))
        {
            using var document = JsonDocument.Parse(line);
            var question = document.RootElement.GetProperty("question").GetString()!;
            foreach (var source in document.RootElement.GetProperty("required_sources").EnumerateArray()
                         .Select(e => e.GetString()!)
                         .Where(s => s.StartsWith("resolve_metric:", StringComparison.Ordinal)))
            {
                if (!checkedSources.Add(source))
                {
                    continue;
                }
                var metricId = source["resolve_metric:".Length..];
                var metric = await repository.GetAsync(metricId, CancellationToken.None);
                if (metric is null)
                {
                    failures.Add($"{source}: 口径不存在（样本：{question}）");
                    continue;
                }
                if (!string.Equals(metric.ConfirmStatus, "CONFIRMED", StringComparison.OrdinalIgnoreCase))
                {
                    failures.Add($"{source}: 口径未确认（{metric.ConfirmStatus}），评估集不得引用不可算口径");
                    continue;
                }

                var parse = MetricExpressionParser.Parse(metric.Definition);
                if (!parse.Ok || parse.Expression is null)
                {
                    failures.Add($"{source}: 定义解析失败（{parse.Error}）");
                    continue;
                }

                // 开发者视角全列可见：此处验证口径本身可编译可执行；
                // 字段级权限路由由 ResolveMetricTool 单测覆盖。
                var sourceColumns = await probe.GetColumnsAsync(metric.SourceTable, CancellationToken.None);
                string? masterTable = null;
                IReadOnlySet<string>? masterColumns = null;
                var joinColumns = new List<string>();
                if (!string.IsNullOrWhiteSpace(metric.RowFilter))
                {
                    using var filterDoc = JsonDocument.Parse(metric.RowFilter);
                    if (filterDoc.RootElement.TryGetProperty("table", out var filterTable)
                        && filterTable.GetString() is { Length: > 0 } tableName
                        && !tableName.Equals(metric.SourceTable, StringComparison.OrdinalIgnoreCase))
                    {
                        masterTable = tableName;
                        masterColumns = await probe.GetColumnsAsync(tableName, CancellationToken.None);
                    }
                    if (filterDoc.RootElement.TryGetProperty("on", out var onArray)
                        && onArray.ValueKind == JsonValueKind.Array)
                    {
                        joinColumns.AddRange(onArray.EnumerateArray()
                            .Select(e => e.GetString() ?? string.Empty)
                            .Where(name => name.Length > 0));
                    }
                }
                var validation = await validator.ValidateAsync(new MetricValidationInput(
                    parse.Expression, metric.SourceTable, sourceColumns, metric.DimensionKeys,
                    metric.RowFilter, masterTable, masterColumns), CancellationToken.None);
                if (!validation.Ok)
                {
                    failures.Add($"{source}: 校验未通过（{validation.Error}）");
                    continue;
                }

                var plan = MetricPlanCompiler.Compile(parse.Expression, metric.SourceTable,
                    validation.RowFilter, [], masterTable, joinColumns, null, null);
                try
                {
                    await executor.ExecuteAsync(plan, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    failures.Add($"{source}: 执行失败（{ex.Message}）");
                }
            }
        }
        Assert.True(checkedSources.Count > 0, "推断集未引用任何 resolve_metric 来源，校验失去意义。");
        Assert.True(failures.Count == 0, "评估集来源与语义层实态漂移：" + string.Join("; ", failures));
    }

    private static DbConnectionFactory ConnectionFactory(string connectionStringValue) =>
        new(new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:ErpDatabase"] = connectionStringValue,
            })
            .Build());
}
