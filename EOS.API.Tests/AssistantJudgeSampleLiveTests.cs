using System.Text.Json;
using EOS.API.Data;
using EOS.API.Data.Assistant;
using EOS.API.Features.Assistant.Evaluation;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 库内 `judge` 层校准样本的**结构门禁**：样本本身坏了的话，一致率就是在拿坏尺子量坏尺子。
///
/// <para>
/// 分两档判，与"草稿可以存、但不计入校准"的流程一致：
/// <list type="bullet">
/// <item>**已审阅**（`reviewed:true`）的样本：一条 FAIL 都不许有（六条都标、PASS/FAIL 带引文、审阅人+日期+messageId）；</item>
/// <item>**草稿**：允许还没标完，但不许有结构性错误（JSON 坏、条款编号/四态不认识、PASS/FAIL 没引文）。</item>
/// </list>
/// 没有样本时直接返回——"用户还没出题"是合法状态，不是缺前置。
/// </para>
///
/// <para>
/// 跑法：<c>dotnet test … --filter FullyQualifiedName~AssistantJudgeSampleLiveTests</c>，前置 <c>$env:MSSQL_ERP_CONN</c>。
/// </para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class AssistantJudgeSampleLiveTests
{
    /// <summary>草稿也不许出现的结构性错误（其余检查项允许草稿未完成）。</summary>
    private static readonly string[] StructuralCodes =
    [
        "CASE_LAYER_UNKNOWN", "CASE_PAYLOAD_INVALID", "CASE_ID_MISMATCH",
        "JUDGE_SAMPLE_MISSING", "JUDGE_LABELS_MISSING", "JUDGE_LABELS_INVALID",
    ];

    [Fact]
    public async Task 库内判官校准样本结构完整()
    {
        var connectionString = Environment.GetEnvironmentVariable("MSSQL_ERP_CONN");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("本用例需要 MSSQL_ERP_CONN（真库）；未配置即失败，不静默跳过。");
        }

        var connections = PolicyServiceFactory.Connections(connectionString);
        var repository = new AssistantEvalRepository(connections);
        var cases = await repository.LoadCasesAsync(["judge"], CancellationToken.None);
        if (cases.Count == 0) return;

        var lookups = await repository.LoadLookupsAsync(CancellationToken.None);
        var problems = new List<string>();

        foreach (var record in cases)
        {
            var reviewed = IsReviewed(record.PayloadJson);
            var checks = AssistantEvalCaseValidator.Validate(record, lookups);

            foreach (var check in checks)
            {
                if (check.Outcome != AssistantEvalOutcome.Failed) continue;
                if (reviewed || StructuralCodes.Contains(check.Code, StringComparer.Ordinal))
                {
                    problems.Add($"{record.CaseId}（{(reviewed ? "已审阅" : "草稿")}）[{check.Code}] {check.Message}");
                }
            }
        }

        Assert.True(problems.Count == 0,
            "库内 judge 校准样本有结构问题：\n  " + string.Join("\n  ", problems));
    }

    private static bool IsReviewed(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload)) return false;
        try
        {
            using var document = JsonDocument.Parse(payload);
            return document.RootElement.ValueKind == JsonValueKind.Object
                   && document.RootElement.TryGetProperty("reviewed", out var reviewed)
                   && reviewed.ValueKind == JsonValueKind.True;
        }
        catch (JsonException)
        {
            // 坏 JSON 由判定器报 CASE_PAYLOAD_INVALID，这里不重复判
            return false;
        }
    }
}
