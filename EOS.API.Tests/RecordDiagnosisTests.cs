using System.Text.Json;
using EOS.API.Data.Workbench;
using EOS.API.Features.Assistant.Diagnosis;
using EOS.API.Features.Assistant.Tools;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 对象级诊断的契约与边界：越权 = 0（且不泄露存在性）、权限问题必须明说、
/// 不下发原文、证据段顺序、证据不足时如实拒答。
/// </summary>
public sealed class RecordDiagnosisTests
{
    private static async Task<RecordDiagnosisDocument> DiagnoseAsync(
        DiagnosisDoubles.FakeReader reader, DiagnosisContext? context = null)
    {
        var service = DiagnosisDoubles.Service(reader);
        var outcome = await service.DiagnoseAsync(
            "u1", context ?? DiagnosisDoubles.Context(), CancellationToken.None);
        return outcome.Document ?? throw new InvalidOperationException("未产出诊断文档");
    }

    [Fact]
    public async Task 记录不可见时不区分不存在与不在数据范围内()
    {
        var reader = new DiagnosisDoubles.FakeReader { Facts = null };

        var outcome = await DiagnosisDoubles.Service(reader)
            .DiagnoseAsync("u1", DiagnosisDoubles.Context(), CancellationToken.None);

        Assert.Null(outcome.Document);
        Assert.Equal("记录不存在或不在你的数据范围内。", AssistantToolExtensions.NotFoundMessage);
    }

    [Fact]
    public async Task 无浏览权限的模块在工具层就被拒且不进入诊断()
    {
        var reader = new DiagnosisDoubles.FakeReader { Facts = DiagnosisDoubles.Facts() };
        var gateway = new AssistantSituationDoubles.FakeGateway();
        gateway.Definitions[1204] = DiagnosisDoubles.Definition();
        var permissions = new AssistantSituationDoubles.FakePermissions(); // 无权浏览
        var tool = new DiagnoseRecordTool(gateway, permissions, DiagnosisDoubles.Service(reader));

        var result = await tool.ExecuteAsync("u1",
            JsonDocument.Parse("""{"module_id":1204,"_keys":["BOM-1"]}""").RootElement.Clone(),
            CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("没有模块", result.ContentForModel);
        Assert.Equal(0, reader.Calls);
    }

    [Fact]
    public async Task 数据范围外的记录在工具层回答不存在且不泄露存在性()
    {
        var reader = new DiagnosisDoubles.FakeReader { Facts = null };
        var gateway = new AssistantSituationDoubles.FakeGateway();
        gateway.Definitions[1204] = DiagnosisDoubles.Definition();
        var permissions = new AssistantSituationDoubles.FakePermissions();
        permissions.Browsable.Add(1204);
        var tool = new DiagnoseRecordTool(gateway, permissions, DiagnosisDoubles.Service(reader));

        var result = await tool.ExecuteAsync("u1",
            JsonDocument.Parse("""{"module_id":1204,"_keys":["BOM-9"]}""").RootElement.Clone(),
            CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(AssistantToolExtensions.NotFoundMessage, result.ContentForModel);
    }

    [Fact]
    public async Task 用户不必报主键_单据号与选中行都能推断()
    {
        var reader = new DiagnosisDoubles.FakeReader { Facts = DiagnosisDoubles.Facts() };
        var gateway = new AssistantSituationDoubles.FakeGateway();
        gateway.Definitions[1204] = DiagnosisDoubles.Definition();
        var permissions = new AssistantSituationDoubles.FakePermissions();
        permissions.Browsable.Add(1204);
        var tool = new DiagnoseRecordTool(gateway, permissions, DiagnosisDoubles.Service(reader));

        // 打开着某张单（处境里有 docNo）：不发 _keys 也能诊断
        tool.UsePageContext(new EOS.API.Features.Assistant.PageContext(1204, "产品BOM表", "edit", "BOM-7"));
        var byDocNo = await tool.ExecuteAsync("u1",
            JsonDocument.Parse("""{"module_id":1204}""").RootElement.Clone(), CancellationToken.None);

        Assert.True(byDocNo.Ok);
        Assert.Equal(1, reader.Calls);

        // 列表里选中一行：同样不必报主键
        tool.UsePageContext(new EOS.API.Features.Assistant.PageContext(
            1204, "产品BOM表", "list", null, Selection: ["BOM-8"]));
        var bySelection = await tool.ExecuteAsync("u1",
            JsonDocument.Parse("""{"module_id":1204}""").RootElement.Clone(), CancellationToken.None);

        Assert.True(bySelection.Ok);
        Assert.Equal(2, reader.Calls);
    }

    [Fact]
    public async Task 缺权限时必须明说而不是含糊()
    {
        // 有浏览权、没批核权：回答"我为什么批不了"必须直说"你没有批核权限"。
        var document = await DiagnoseAsync(
            new DiagnosisDoubles.FakeReader { Facts = DiagnosisDoubles.Facts() },
            DiagnosisDoubles.Context(permission: DiagnosisDoubles.Permission(canApprove: false)));

        Assert.Equal(DiagnosisClasses.Blockers, document.Verdict.CauseClass);
        Assert.Contains("你没有「批核」权限。", document.Verdict.Message);
        Assert.Contains(document.Blockers!, entry => entry.Code == "NO_APPROVE_RIGHT");
    }

    [Fact]
    public async Task 已结案与已批核给出服务端同一句话()
    {
        var finished = await DiagnoseAsync(new DiagnosisDoubles.FakeReader
        {
            Facts = DiagnosisDoubles.Facts(finished: true, confirmed: true),
        });
        Assert.Equal(LifecycleEditGuards.FinishedMessage, finished.Verdict.Message);

        var confirmed = await DiagnoseAsync(new DiagnosisDoubles.FakeReader
        {
            Facts = DiagnosisDoubles.Facts(confirmed: true),
        });
        Assert.Equal(LifecycleEditGuards.ConfirmedMessage, confirmed.Verdict.Message);
    }

    [Fact]
    public async Task 引擎维护字段被人工修改时给出与保存路径同一句话()
    {
        var document = await DiagnoseAsync(
            new DiagnosisDoubles.FakeReader
            {
                Facts = DiagnosisDoubles.Facts(fields:
                [
                    new DiagnosisFieldFact("IN_SUM", "累计入库", DiagnosisFieldGuardText.EngineMaintained("IN_SUM"),
                        DiagnosisFieldGuardText.EngineMaintainedSource, true),
                ]),
            },
            DiagnosisDoubles.Context(attemptedFields: ["IN_SUM"]));

        Assert.Equal(DiagnosisClasses.FieldGuard, document.Verdict.CauseClass);
        Assert.Equal("字段“IN_SUM”由引擎维护，不允许人工修改。", document.Verdict.Message);
    }

    [Fact]
    public async Task 证据不足时如实拒答并列出缺失证据()
    {
        var document = await DiagnoseAsync(new DiagnosisDoubles.FakeReader
        {
            Facts = DiagnosisDoubles.Facts(rules: [new DiagnosisRule("SAVE", 1, "duplicate-check", true, "客户订单号重复。", "重复检查（模式 entity）")]),
        }, DiagnosisDoubles.Context(permission: DiagnosisDoubles.Permission(
            canApprove: true, canDelete: true, canEndCase: true)));

        Assert.Equal(DiagnosisClasses.Unknown, document.Verdict.CauseClass);
        Assert.Equal(DiagnosisMessages.InsufficientEvidence, document.Verdict.Message);
        Assert.Contains(document.Evidence.Missing, item => item.Contains("校验判据的命中事实"));
        Assert.NotNull(document.Caveat);
        Assert.Contains("不猜测根因", document.Caveat!);
    }

    [Fact]
    public async Task 输出不含表达式与审计明细原文()
    {
        var document = await DiagnoseAsync(new DiagnosisDoubles.FakeReader
        {
            Facts = DiagnosisDoubles.Facts(
                provenance:
                [
                    new DiagnosisProvenanceFact("QTY", "数量", "virtual", null),
                    new DiagnosisProvenanceFact("CUST_ID", "客户", "chooser", "CLIENT"),
                ],
                lastFailure: new DiagnosisFailureFact("2026-09-29 10:00", "VALIDATION_FAILED", "corr-1", "保存被拒"),
                signals: [new DiagnosisRuleSignal("SAVE", "line-require", 1, "命中行 SERIAL_NO=1")],
                rules: [new DiagnosisRule("SAVE", 1, "line-require", true, "以下序号项需要输入批号", "DETAIL 域字段 BATCH_NO 必须非空或满足断言")]),
        });

        var json = DiagnosisJson.Serialize(document);

        Assert.DoesNotContain("VIRTUAL_EXP", json, StringComparison.Ordinal);
        Assert.DoesNotContain("DETAIL_JSON", json, StringComparison.Ordinal);
        // 虚拟列只报"是虚拟列"，来源引用留空——表达式原文没有进入输出契约的通道
        Assert.Contains(document.Provenance!, entry => entry.Origin == "virtual" && entry.SourceRef is null);
        Assert.DoesNotContain("ISNULL", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 证据段顺序与附录契约一致且按需段与降级段可缺席()
    {
        var document = await DiagnoseAsync(new DiagnosisDoubles.FakeReader
        {
            Facts = DiagnosisDoubles.Facts(
                rules: [new DiagnosisRule("SAVE", 1, "line-require", true, "以下序号项需要输入批号", "DETAIL 域字段 BATCH_NO 必须非空或满足断言")],
                signals: [new DiagnosisRuleSignal("SAVE", "line-require", 1, null)],
                fields: [new DiagnosisFieldFact("IN_SUM", "累计入库", DiagnosisFieldGuardText.EngineMaintained("IN_SUM"), "tests", true)],
                provenance: [new DiagnosisProvenanceFact("QTY", "数量", "virtual", null)],
                effects: [new DiagnosisEffectFact("inventory-move", "INV_PRO_DEPOT", "QTY", "DEACCUM")],
                flow: new DiagnosisFlowFact("0", "二级审批", ["u2"], "u1"),
                lastFailure: new DiagnosisFailureFact("2026-09-29 10:00", null, "corr-2", "保存被拒")),
        });

        var json = DiagnosisJson.Serialize(document);
        var order = new[]
        {
            "\"target\"", "\"validation\"", "\"fieldGuard\"", "\"blockers\"", "\"provenance\"",
            "\"effects\"", "\"flow\"", "\"lastFailure\"", "\"verdict\"", "\"evidence\"",
        };
        var positions = order.Select(key => json.IndexOf(key, StringComparison.Ordinal)).ToArray();
        Assert.All(positions, position => Assert.True(position >= 0, $"缺少段：{json}"));
        Assert.Equal(positions.OrderBy(position => position), positions);

        // 模块没配流程 / 没有失败审计时：按需段与降级段整段缺席（不是空对象）
        var lean = await DiagnoseAsync(new DiagnosisDoubles.FakeReader
        {
            Facts = DiagnosisDoubles.Facts(
                rules: [new DiagnosisRule("SAVE", 1, "no-cycle", true, "以下元件在BOM结构中循环使用 {ROWS}", "引用关系不得成环（关系表 PRODUCT）")]),
        }, DiagnosisDoubles.Context(permission: DiagnosisDoubles.Permission(
            canApprove: true, canDelete: true, canEndCase: true)));
        var leanJson = DiagnosisJson.Serialize(lean);
        Assert.DoesNotContain("\"flow\"", leanJson, StringComparison.Ordinal);
        Assert.DoesNotContain("\"lastFailure\"", leanJson, StringComparison.Ordinal);
        Assert.DoesNotContain("\"fieldGuard\"", leanJson, StringComparison.Ordinal);
        // 证据不足时的 caveat 排在 evidence 之后（说明是结论的附注，不改证据优先级）
        Assert.Contains("不猜测根因", leanJson);
        Assert.True(leanJson.IndexOf("\"caveat\"", StringComparison.Ordinal)
            > leanJson.IndexOf("\"evidence\"", StringComparison.Ordinal));
    }

    [Fact]
    public void 动作原因到原因码的映射覆盖判定的全部阻塞原因()
    {
        // EvaluateActions 的文案是中文自由文本，这里把"文案 → 原因码"的映射钉住：
        // 谁改了判定文案而没同步映射，这条用例会拦下（否则诊断会静默少报阻塞）。
        bool[] flags = [true, false];
        foreach (var hasFlow in flags)
        {
            foreach (var state in new[] { "0", "1", null })
            {
                foreach (var confirmed in flags)
                {
                    foreach (var finished in flags)
                    {
                        foreach (var canApprove in flags)
                        {
                            foreach (var canEdit in flags)
                            {
                                var input = new GetModuleFlowTool.FlowActionInput(
                                    canApprove, canEdit, CanDelete: canEdit, CanEndCase: false, CanUnEndCase: false,
                                    UserId: "u1", HasFlow: hasFlow, InstanceState: state,
                                    IsStarter: true, IsCurrentApprover: false, Confirmed: confirmed, Finished: finished);
                                foreach (var (action, allowed, reason) in GetModuleFlowTool.EvaluateActions(input))
                                {
                                    if (allowed) continue;
                                    Assert.True(
                                        DiagnosisActionEvaluator.CodeFor(reason) is not null
                                        || DiagnosisActionEvaluator.NonBlockerReasons.Contains(reason),
                                        $"动作 {action} 的原因「{reason}」未登记：新增判定文案时必须同步原因码映射");
                                }
                            }
                        }
                    }
                }
            }
        }
    }

    [Fact]
    public void 阻塞清单里状态类原因排在动作权限之前()
    {
        var blockers = DiagnosisActionEvaluator.Evaluate(new DiagnosisActionFacts(
            DiagnosisDoubles.Permission(canApprove: false),
            "u1",
            Confirmed: false,
            Finished: true,
            HasFlow: false,
            FlowState: null,
            IsStarter: false,
            IsCurrentApprover: false));

        Assert.Equal(LifecycleEditGuards.FinishedMessage, blockers[0].Message);
        Assert.True(blockers.Count > 1, "状态类阻塞之后应继续列出动作权限类阻塞");
        Assert.Contains(blockers, blocker => blocker.Source == "get_module_flow.EvaluateActions");
    }
}
