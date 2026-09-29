using EOS.API.Features.Assistant.Config;
using EOS.API.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 配置面只读：三类不一致各有一个**可复现**样本（同一输入两次结论一致），
/// 且判据与既有真源同一口径（发布门 / 效果键分布 / 契约下沉的目录）。
/// </summary>
public sealed class ConfigDiagnosisTests
{
    private static ConfigActionFact Action(
        int seq, string eventCode, string effectKey, string? reverse, IReadOnlyList<BusinessActionOpDto>? ops = null) =>
        new(seq, eventCode, effectKey, true, reverse, ops ?? []);

    private static BusinessActionOpDto Op(string opCode, string targetTable, string targetField, int opSeq = 1) =>
        new(opSeq, targetTable, targetField, opCode, "MASTER");

    private static ConfigConsistencyFacts Facts(
        IReadOnlyList<ConfigActionFact> actions,
        IReadOnlyList<ConfigEffectKeyUsage>? usage = null) => new(1204, actions, usage ?? []);

    private static ConfigFinding Single(IReadOnlyList<ConfigFinding> findings, string code) =>
        Assert.Single(findings, finding => finding.Code == code);

    [Fact]
    public void 反向说明里的自由文本会被指出_同一输入两次结论一致()
    {
        var facts = Facts(
        [
            Action(1, "APPROVE_EFFECT", "field-accumulate",
                """{"kind":"auto-reverse","note":"数量 -=；日期不回退"}"""),
        ]);

        var first = ConfigConsistencyRules.Check(facts);
        var second = ConfigConsistencyRules.Check(facts);
        var finding = Single(first, ConfigConsistencyRules.ReverseNoteNotRead);

        Assert.Contains("数量 -=；日期不回退", finding.Message);
        Assert.Contains("不会被任何代码读取", finding.Message);
        Assert.Equal(first, second);
    }

    [Fact]
    public void 反向kind不受该效果键支持时会指出运行时抛错_同一输入两次结论一致()
    {
        // car-filloil-sync 的执行闭集只有 restore-previous（反向兼容矩阵的登记）
        var facts = Facts([Action(1, "DEAPPROVE", "car-filloil-sync", """{"kind":"auto-reverse"}""")]);

        var first = ConfigConsistencyRules.Check(facts);
        var second = ConfigConsistencyRules.Check(facts);
        var finding = Single(first, ConfigConsistencyRules.ReverseKindUnsupported);

        Assert.Contains("restore-previous", finding.Message);
        Assert.Contains("解批时会抛错", finding.Message);
        Assert.Equal(first, second);
    }

    [Fact]
    public void 反向kind的支持判定与发布门同一口径_真公式行与占位行分流()
    {
        // 带真公式行 ⇒ 走公式解释器 ⇒ 不接受 clear-on-deapprove
        var withFormula = ConfigConsistencyRules.Check(Facts(
        [
            Action(1, "DEAPPROVE", "payment-date-calc", """{"kind":"clear-on-deapprove"}""",
                [Op("ACCUM", "INV_PRO_DEPOT", "QTY")]),
        ]));
        Single(withFormula, ConfigConsistencyRules.ReverseKindUnsupported);

        // 占位公式行（算子/目标表/目标字段全空）不算公式行 ⇒ 该键的服务处理器恰恰接受这个 kind
        var placeholderOnly = ConfigConsistencyRules.Check(Facts(
        [
            Action(1, "DEAPPROVE", "payment-date-calc", """{"kind":"clear-on-deapprove"}""",
                [Op(string.Empty, string.Empty, string.Empty)]),
        ]));
        Assert.DoesNotContain(placeholderOnly, finding => finding.Code == ConfigConsistencyRules.ReverseKindUnsupported);
    }

    [Fact]
    public void 用户点击行没有反向语义_不参与反向kind判定()
    {
        var findings = ConfigConsistencyRules.Check(Facts(
        [
            Action(1, "MANUAL", "car-filloil-sync", """{"kind":"auto-reverse"}"""),
        ]));

        Assert.DoesNotContain(findings, finding => finding.Code == ConfigConsistencyRules.ReverseKindUnsupported);
    }

    [Fact]
    public void 本模块用到的单例效果键会建议复用()
    {
        var facts = Facts(
            [Action(1, "APPROVE_EFFECT", "car-filloil-sync", null)],
            [
                new ConfigEffectKeyUsage("car-filloil-sync", 1),
                new ConfigEffectKeyUsage("inventory-move", 44),
            ]);

        var first = ConfigConsistencyRules.Check(facts);
        var second = ConfigConsistencyRules.Check(facts);
        var finding = Single(first, ConfigConsistencyRules.EffectKeySingleton);

        Assert.Contains("只用过 1 次", finding.Message);
        Assert.Contains("在用键 2 个", finding.Message);
        Assert.Equal(first, second);
    }

    [Fact]
    public void 解释效果键时参数说明来自既有的契约下沉目录()
    {
        var explanation = ConfigDiagnosisService.Explain(new ConfigExplanationRequest("inventory-move"));

        Assert.Contains("inventory-move", explanation.Subject);
        Assert.Contains(explanation.Parameters, parameter =>
            parameter.Name == "direction" && parameter.EnumValues is not null && parameter.EnumValues.Contains("OUT"));
        Assert.Contains(explanation.Lines, line => line.Contains("执行方式"));
        Assert.Contains(explanation.Lines, line => line.Contains("反向（解批）可用的 kind"));
    }

    [Fact]
    public void 没有参数说明的效果键会如实说缺口而不是编一段()
    {
        // 目录里有键、但还没人为它写字段级参数说明：如实说缺口，比编一段好
        var undescribed = EOS.API.Data.Effects.EffectRegistry.Keys.Keys
            .FirstOrDefault(key => EOS.API.Data.Effects.EffectParamDescriptors.For(key).Count == 0);
        Assert.NotNull(undescribed);

        var explanation = ConfigDiagnosisService.Explain(new ConfigExplanationRequest(EffectKey: undescribed));

        Assert.Empty(explanation.Parameters);
        Assert.Contains(explanation.Lines, line => line.Contains("尚无字段级参数说明"));
    }

    [Fact]
    public void 解释反向kind时明确名义闭集不等于执行闭集()
    {
        var explanation = ConfigDiagnosisService.Explain(new ConfigExplanationRequest(ReverseKind: "restore-previous"));

        Assert.Contains("restore-previous", explanation.Subject);
        Assert.Contains(explanation.Lines, line => line.Contains("不等于"));
    }

    [Fact]
    public void 解释里写在不被读取位置的反向说明会同时给出不一致提示()
    {
        var explanation = ConfigDiagnosisService.Explain(new ConfigExplanationRequest(
            EffectKey: "field-accumulate", ReverseNote: "日期不回退"));

        var note = Assert.Single(explanation.Notes);
        Assert.Equal(ConfigConsistencyRules.ReverseNoteNotRead, note.Code);
    }

    [Fact]
    public async Task 无配置权限时配置面只读诊断failclosed()
    {
        var reader = new FakeConfigReader();
        var permissions = new AssistantSituationDoubles.FakePermissions();
        permissions.Browsable.Add(2301); // 能看，但不能配
        var service = new ConfigDiagnosisService(
            reader, permissions, NullLogger<ConfigDiagnosisService>.Instance);

        Assert.Null(await service.DiagnoseAsync("u1", 2301, CancellationToken.None));
        Assert.Null(await service.ExplainAsync("u1", 2301, new ConfigExplanationRequest("inventory-move"), CancellationToken.None));
        Assert.Equal(0, reader.Calls);
    }

    [Fact]
    public async Task 有配置权限时返回检出项与覆盖面声明()
    {
        var reader = new FakeConfigReader
        {
            Facts = Facts([Action(1, "DEAPPROVE", "car-filloil-sync", """{"kind":"auto-reverse"}""")]),
        };
        var permissions = new AssistantSituationDoubles.FakePermissions();
        permissions.Browsable.Add(2301);
        permissions.ModuleConfigModules.Add(2301);
        var service = new ConfigDiagnosisService(
            reader, permissions, NullLogger<ConfigDiagnosisService>.Instance);

        var report = await service.DiagnoseAsync("u1", 2301, CancellationToken.None);

        Assert.NotNull(report);
        Assert.Contains(report!.Findings, finding => finding.Code == ConfigConsistencyRules.ReverseKindUnsupported);
        Assert.Equal(ConfigDiagnosisService.Coverage, report.Coverage);
    }

    private sealed class FakeConfigReader : IConfigDiagnosisReader
    {
        public ConfigConsistencyFacts? Facts { get; set; }

        public int Calls { get; private set; }

        public Task<ConfigConsistencyFacts?> LoadAsync(int moduleId, CancellationToken token)
        {
            Calls++;
            return Task.FromResult(Facts);
        }
    }
}
