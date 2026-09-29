using System.Reflection;
using System.Text.Json;
using EOS.API.Data.DocumentActions.Handlers;
using EOS.API.Features.Assistant.Diagnosis;
using Xunit;
using Xunit.Abstractions;

namespace EOS.API.Tests;

/// <summary>
/// 诊断黄金集跑分（对象级诊断的核心验收）。
///
/// <para>
/// 样本来自 <c>MODULE_VALIDATION_RULE</c> 的**元数据枚举**（每条规则天然是一个"应当能被诊断出来"的
/// 用例），每条自带"标准答案"（规则消息 + 模块 + 触发条件），因此不需要推断标准答案。
/// **候选全部 <c>reviewed:false</c>**：本跑分以"未经用户审阅的候选"作**临时黄金集**，
/// 结论只在"归因与呈现"这一层成立（哪条规则、什么类别、消息对不对、证据不足时是否如实拒答）。
/// </para>
/// <para>
/// 事实由夹具提供：它扮演"引擎/只读探针给出的判据命中事实"。跑分度量的是服务端能否把
/// **判据身份**（阶段 / 键 / 序号）折回正确的登记规则——这正是错误归因会发生的环节。
/// </para>
/// <para>
/// 门槛：**错误归因 = 0（硬）**、**拒答率 ≤ 10%（硬）**；命中率 ≥95% 是目标值而非门槛。
/// </para>
/// </summary>
public sealed class DiagnosisGoldenSetTests(ITestOutputHelper output)
{
    private sealed record GoldenCase(
        string Id,
        string ModuleLabel,
        int ModuleId,
        string Stage,
        int Seq,
        string ValidationKey,
        bool Enabled,
        string Message,
        string Trigger,
        string SuggestedClass,
        bool Reviewed);

    private sealed record Score(int Total, int Hits, int Denials, IReadOnlyList<string> Errors)
    {
        public double HitRate => Total == 0 ? 0 : (double)Hits / Total;

        public double DenialRate => Total == 0 ? 0 : (double)Denials / Total;
    }

    private static string GoldenPath()
    {
        // 输出目录可能是仓库外的临时路径，故优先用编译期注入的仓库根（与其它真库用例同一机制）。
        var root = typeof(DiagnosisGoldenSetTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(item => item.Key == "RepoRoot")?.Value;
        if (!string.IsNullOrWhiteSpace(root))
        {
            var injected = Path.Combine(root, "EOS.API.Tests", "AssistantEval", "diagnosis", "golden.jsonl");
            if (File.Exists(injected)) return injected;
        }

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            foreach (var candidate in new[]
                {
                    Path.Combine(directory.FullName, "AssistantEval", "diagnosis", "golden.jsonl"),
                    Path.Combine(directory.FullName, "EOS.API.Tests", "AssistantEval", "diagnosis", "golden.jsonl"),
                })
            {
                if (File.Exists(candidate)) return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException("诊断黄金集缺失：AssistantEval/diagnosis/golden.jsonl");
    }

    private static IReadOnlyList<GoldenCase> LoadCases()
    {
        var cases = new List<GoldenCase>();
        foreach (var line in File.ReadAllLines(GoldenPath()))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            var moduleLabel = root.GetProperty("moduleId").GetString() ?? "-";
            cases.Add(new GoldenCase(
                root.GetProperty("id").GetString() ?? string.Empty,
                moduleLabel,
                int.TryParse(moduleLabel, out var moduleId) ? moduleId : 0,
                root.GetProperty("stage").GetString() ?? string.Empty,
                root.GetProperty("seq").GetInt32(),
                root.GetProperty("validationKey").GetString() ?? string.Empty,
                root.GetProperty("enabled").GetBoolean(),
                root.GetProperty("message").GetString() ?? string.Empty,
                root.GetProperty("triggerSummary").GetString() ?? string.Empty,
                root.GetProperty("suggestedClass").GetString() ?? string.Empty,
                root.GetProperty("reviewed").GetBoolean()));
        }

        return cases;
    }

    [Fact]
    public void 黄金集是未经用户审阅的候选且条数为三十()
    {
        var cases = LoadCases();

        Assert.Equal(30, cases.Count);
        // 审阅权在用户手里：候选状态不得被代码改成已审阅（改了这里会拦下）。
        Assert.All(cases, item => Assert.False(item.Reviewed));
    }

    [Fact]
    public async Task 跑分_错误归因为零且拒答率不超过一成()
    {
        var cases = LoadCases();
        var score = await ScoreAsync(cases);
        Report("基线", score);

        Assert.Empty(score.Errors);
        Assert.True(score.DenialRate <= 0.10,
            $"拒答率 {score.DenialRate:P0} 超过 10%（{score.Denials}/{score.Total}）");
        // 目标值，不是门槛：低于它要解释，但不阻断。
        output.WriteLine($"命中率目标 ≥95%，实测 {score.HitRate:P0}");
    }

    [Fact]
    public async Task 判别力注入_把一条答成似是而非的原因时跑分必须变红()
    {
        var cases = LoadCases();
        var baseline = await ScoreAsync(cases);
        Assert.Empty(baseline.Errors);

        // 注入：对 validation 类固定返回一条"听起来合理"的其它规则消息（放过第一条）。
        // 判分器若察觉不到，说明这道门槛是空的。
        var injected = await ScoreAsync(cases, (current, document) =>
            current.SuggestedClass == "validation"
                ? document with
                {
                    Verdict = document.Verdict with { Message = "以下序号项需要输入批号" },
                }
                : document);
        Report("注入后", injected);

        Assert.NotEmpty(injected.Errors);
    }

    /// <summary>
    /// 判据身份对不上时必须**拒答**而不是"找一个像的规则"：
    /// 这是"错误归因 = 0"在最容易出错环节上的守卫（放宽匹配 = 把别人的原因安在这张单上）。
    /// </summary>
    [Fact]
    public async Task 判据身份对不上时报证据冲突而不是套用相似规则()
    {
        var reader = new DiagnosisDoubles.FakeReader
        {
            Facts = DiagnosisDoubles.Facts(
                rules: [new DiagnosisRule("APPROVE", 1, "qty-not-exceed", true, "退货数量超过已送货数量", "数量不得超过被引用表 V_SEND 的可用量（模式 usage-not-exceed）")],
                signals: [new DiagnosisRuleSignal("SAVE", "qty-not-exceed", 1, "命中行 SERIAL_NO=1")],
                missing: []),
        };
        var service = DiagnosisDoubles.Service(reader);

        var outcome = await service.DiagnoseAsync("u1", DiagnosisDoubles.Context(), CancellationToken.None);

        var document = Assert.IsType<RecordDiagnosisDocument>(outcome.Document);
        Assert.Equal(DiagnosisClasses.Unknown, document.Verdict.CauseClass);
        Assert.Contains(document.Evidence.Missing, item => item.Contains("证据冲突"));
        Assert.Contains(document.Validation!, entry => entry.RuleKey == "qty-not-exceed" && !entry.Hit);
    }

    private async Task<Score> ScoreAsync(
        IReadOnlyList<GoldenCase> cases,
        Func<GoldenCase, RecordDiagnosisDocument, RecordDiagnosisDocument>? mutate = null)
    {
        var hits = 0;
        var denials = 0;
        var errors = new List<string>();
        foreach (var current in cases)
        {
            var document = await DiagnoseAsync(current, cases);
            if (mutate is not null) document = mutate(current, document);
            var verdict = document.Verdict;
            if (verdict.CauseClass == DiagnosisClasses.Unknown)
            {
                denials++;
                continue;
            }

            if (IsHit(current, verdict))
            {
                hits++;
                continue;
            }

            errors.Add($"{current.Id} 期望 {current.SuggestedClass}/{current.Message}，实得 "
                + $"{verdict.CauseClass}/{verdict.Message}");
        }

        return new Score(cases.Count, hits, denials, errors);
    }

    private static bool IsHit(GoldenCase current, DiagnosisVerdictEntry verdict) => current.SuggestedClass switch
    {
        "validation" => verdict.CauseClass == DiagnosisClasses.Validation
            && verdict.Message == current.Message
            && verdict.RuleKey == current.ValidationKey
            && string.Equals(verdict.Stage, current.Stage, StringComparison.OrdinalIgnoreCase),
        "fieldGuard" => verdict.CauseClass == DiagnosisClasses.FieldGuard && verdict.Message == current.Message,
        "blockers" => verdict.CauseClass == DiagnosisClasses.Blockers && verdict.Message == current.Message,
        _ => false,
    };

    private static async Task<RecordDiagnosisDocument> DiagnoseAsync(
        GoldenCase current, IReadOnlyList<GoldenCase> all)
    {
        var sameModule = all.Where(item => item.ModuleLabel == current.ModuleLabel).ToList();
        var rules = new List<DiagnosisRule>();
        var signals = new List<DiagnosisRuleSignal>();
        var fields = new List<DiagnosisFieldFact>();
        var attempted = new List<string>();
        var confirmed = false;
        var finished = false;

        switch (current.SuggestedClass)
        {
            case "validation":
                // 同模块的**全部**规则都在场（跨阶段、同键不同序号），因此"选对一条"才是真判别力。
                rules.AddRange(sameModule
                    .Where(item => item.SuggestedClass == "validation")
                    .Select(item => new DiagnosisRule(
                        item.Stage, item.Seq, item.ValidationKey, item.Enabled, item.Message, item.Trigger)));
                signals.Add(new DiagnosisRuleSignal(current.Stage, current.ValidationKey, current.Seq, "命中行 SERIAL_NO=1"));
                break;
            case "fieldGuard":
                // 事实只给"这个列由引擎维护"（取自代码里的真实维护列集合 + 条目记录的触发事实），
                // 用户可见文案由服务端自己组织——期望消息不参与事实构造。
                // 触发事实里**首个**被点名的维护列就是这条用例的主列（可能顺带提到同类列）。
                var column = MasterFieldWriteHandler.EngineMaintainedColumns
                    .Select(candidate => (Column: candidate, Position: current.Trigger.IndexOf(candidate, StringComparison.Ordinal)))
                    .Where(item => item.Position >= 0)
                    .OrderBy(item => item.Position)
                    .Select(item => item.Column)
                    .FirstOrDefault();
                Assert.False(string.IsNullOrEmpty(column), $"{current.Id} 的触发事实里没有可识别的引擎维护列");
                fields.Add(new DiagnosisFieldFact(
                    column!, column!, DiagnosisFieldGuardText.EngineMaintained(column!),
                    DiagnosisFieldGuardText.EngineMaintainedSource, true));
                attempted.Add(column!);
                break;
            case "blockers":
                confirmed = current.Trigger.Contains("CONFIRM_TAG=1", StringComparison.Ordinal);
                finished = current.Trigger.Contains("FINISHED_TAG=1", StringComparison.Ordinal);
                break;
        }

        var reader = new DiagnosisDoubles.FakeReader
        {
            Facts = DiagnosisDoubles.Facts(
                rules: rules,
                signals: signals,
                fields: fields,
                confirmed: confirmed,
                finished: finished,
                recordKey: ["KEY-1"]),
        };
        var service = DiagnosisDoubles.Service(reader);
        var context = DiagnosisDoubles.Context(
            DiagnosisDoubles.Definition(current.ModuleId, current.ModuleLabel),
            DiagnosisDoubles.Permission(),
            ["KEY-1"],
            attempted);

        var outcome = await service.DiagnoseAsync("u1", context, CancellationToken.None);
        return outcome.Document ?? throw new InvalidOperationException($"{current.Id} 未产出诊断文档");
    }

    private void Report(string label, Score score)
    {
        output.WriteLine($"[{label}] 总数 {score.Total}；命中 {score.Hits}（{score.HitRate:P1}）；"
            + $"拒答 {score.Denials}（{score.DenialRate:P1}）；错误归因 {score.Errors.Count}");
        foreach (var error in score.Errors)
        {
            output.WriteLine($"  错误归因：{error}");
        }
    }
}
