using System.Text.Json;
using EOS.API.Data;
using EOS.API.Data.DocumentActions;
using EOS.API.Data.Effects;
using EOS.API.Data.DocumentActions.Handlers;
using EOS.API.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 单据操作（自定义按钮）契约层的纯单测：注册表闭集、MANUAL 行解析、参数声明校验，
/// 以及"MANUAL 行不参与效果链"这条边界在各处的落点。
/// </summary>
public sealed class DocumentActionContractTests
{
    private sealed class StubAction(string key, string label, string? placement = null) : IDocumentUserAction, IDocumentActionPlacement
    {
        public string Key => key;
        public string Label => label;
        public string Placement => placement ?? DocumentActionPlacements.Master;
        public Task<DocumentActionResult> ExecuteAsync(DocumentActionContext context, CancellationToken token) =>
            Task.FromResult(new DocumentActionResult(DocumentActionOutcome.Message, "stub"));
    }

    private static DocumentActionRegistry Registry(params IDocumentUserAction[] actions) =>
        new(actions, NullLogger<DocumentActionRegistry>.Instance);

    // ===== 注册表（代码闭集） =====

    [Fact]
    public void Registry_IsCaseInsensitive_AndKeepsOnlyValidKeys()
    {
        var registry = Registry(new StubAction("recalc-account", "重算账面数"), new StubAction("Bad Key!", "非法"));

        Assert.True(registry.IsRegistered("RECALC-ACCOUNT"));
        Assert.False(registry.IsRegistered("Bad Key!"));
        Assert.False(registry.IsRegistered("not-registered"));
        Assert.Single(registry.Keys);
    }

    [Fact]
    public void Registry_ResolvesPlacement_DefaultingToMaster()
    {
        var registry = Registry(
            new StubAction("recalc-account", "重算账面数", DocumentActionPlacements.Detail),
            new StubAction("relocate-stock", "归位"));

        Assert.Equal(DocumentActionPlacements.Detail, registry.PlacementOf("recalc-account"));
        Assert.Equal(DocumentActionPlacements.Master, registry.PlacementOf("relocate-stock"));
    }

    [Fact]
    public void Registry_ProbeHandlerIsRegisteredUnderItsDeclaredKey()
    {
        var registry = Registry(new DocumentActionProbeHandler());

        Assert.True(registry.IsRegistered(DocumentActionProbeHandler.ActionKey));
        Assert.Equal("管线探针", registry.LabelOf(DocumentActionProbeHandler.ActionKey));
        Assert.Equal(DocumentActionPlacements.Master, registry.PlacementOf(DocumentActionProbeHandler.ActionKey));
    }

    // ===== 配置行解析（definition.businessActions 里的 MANUAL 行） =====

    private static JsonElement Actions(params object[] rows) => JsonSerializer.SerializeToElement(rows);

    [Fact]
    public void Parse_ReadsManualRows_AndIgnoresOtherEventsAndDisabledRows()
    {
        var actions = Actions(
            new { seq = 1, eventCode = "MANUAL", effectKey = "recalc-account", enabled = true, label = "重算账面数", confirmTag = true, failMode = "WARN" },
            new { seq = 2, eventCode = "SAVE", effectKey = "set-state", enabled = true },
            new { seq = 3, eventCode = "MANUAL", effectKey = "relocate-stock", enabled = false });

        var parsed = DocumentActionConfigs.Parse(actions);

        var single = Assert.Single(parsed);
        Assert.Equal("recalc-account", single.Key);
        Assert.Equal("重算账面数", single.Label);
        Assert.True(single.ConfirmTag);
        Assert.Equal("WARN", single.FailMode);
    }

    [Fact]
    public void Find_FallsBackToEffectName_ThenToNothing()
    {
        var actions = Actions(
            new { seq = 1, eventCode = "MANUAL", effectKey = "recalc-account", enabled = true, effectName = "重算账面数量" });

        Assert.Equal("重算账面数量", DocumentActionConfigs.Find(actions, "recalc-account")!.Label);
        Assert.Null(DocumentActionConfigs.Find(actions, "not-configured"));
        Assert.Null(DocumentActionConfigs.Find(null, "recalc-account"));
    }

    [Fact]
    public void Find_ParsesParameterDeclaration_CarriedAsJsonText()
    {
        var actions = Actions(new
        {
            seq = 1,
            eventCode = "MANUAL",
            effectKey = "relocate-stock",
            enabled = true,
            @params = """{"fields":[{"key":"relocateTo","label":"目标库位","type":"string","required":true}]}""",
        });

        var config = DocumentActionConfigs.Find(actions, "relocate-stock")!;

        Assert.NotNull(config.Params);
        Assert.Equal(JsonValueKind.Object, config.Params!.Value.ValueKind);
        Assert.Empty(DocumentActionParams.ValidateDeclaration(config.Params));
    }

    // ===== 参数声明与入参校验 =====

    [Fact]
    public void Params_Declaration_AcceptsTheDocumentedShape_AndRejectsDrift()
    {
        Assert.Empty(DocumentActionParams.Validate(
            """{"fields":[{"key":"relocateTo","label":"目标库位","type":"string","required":true,"maxLength":30}]}"""));

        Assert.NotEmpty(DocumentActionParams.Validate("""{"columns":[{"key":"a"}]}"""));
        Assert.NotEmpty(DocumentActionParams.Validate("""{"fields":[{"key":"1bad","label":"x","type":"string"}]}"""));
        Assert.NotEmpty(DocumentActionParams.Validate("""{"fields":[{"key":"a","label":"","type":"string"}]}"""));
        Assert.NotEmpty(DocumentActionParams.Validate("""{"fields":[{"key":"a","label":"x","type":"money"}]}"""));
        Assert.NotEmpty(DocumentActionParams.Validate(
            """{"fields":[{"key":"a","label":"x","type":"string"},{"key":"A","label":"y","type":"string"}]}"""));
    }

    [Fact]
    public void Params_Read_EnforcesRequiredLengthTypeAndWhitelist()
    {
        var declaration = JsonSerializer.SerializeToElement(new
        {
            fields = new object[]
            {
                new { key = "relocateTo", label = "目标库位", type = "string", required = true, maxLength = 6 },
                new { key = "force", label = "强制", type = "bool", required = false },
            },
        });

        var (values, errors) = DocumentActionParams.Read(
            JsonSerializer.SerializeToElement(new { relocateTo = "A-R1", force = "true" }), declaration);
        Assert.Empty(errors);
        Assert.Equal("A-R1", values["relocateTo"]);
        Assert.Equal("true", values["force"]);

        var (_, missing) = DocumentActionParams.Read(JsonSerializer.SerializeToElement(new { }), declaration);
        Assert.Contains(missing, error => error.Code == "PARAM_REQUIRED");

        var (_, tooLong) = DocumentActionParams.Read(
            JsonSerializer.SerializeToElement(new { relocateTo = "ABCDEFG" }), declaration);
        Assert.Contains(tooLong, error => error.Code == "PARAM_TOO_LONG");

        var (_, unknown) = DocumentActionParams.Read(
            JsonSerializer.SerializeToElement(new { relocateTo = "A", sneaky = "1" }), declaration);
        Assert.Contains(unknown, error => error.Code == "PARAM_UNKNOWN");

        var (_, badType) = DocumentActionParams.Read(
            JsonSerializer.SerializeToElement(new { relocateTo = "A", force = "perhaps" }), declaration);
        Assert.Contains(badType, error => error.Code == "PARAM_INVALID");
    }

    // ===== MANUAL 行不参与效果链 =====

    [Fact]
    public void ManualEvent_IsKnown_ButNeverAppliesToDocumentEvents()
    {
        Assert.True(BusinessActionCatalog.IsKnownEvent("MANUAL"));
        Assert.True(BusinessActionCatalog.IsManualEvent("manual"));
        Assert.True(EffectEventMapper.TryParse("MANUAL", out var parsed));
        Assert.Equal(EffectEvent.Manual, parsed);
        Assert.False(EffectEventMapper.AppliesTo("MANUAL", EffectEvent.Save));
        Assert.False(EffectEventMapper.AppliesTo("MANUAL", EffectEvent.ApproveEffect));
        Assert.NotEqual("SAVE", EffectPipeline.StageFor(EffectEvent.Manual));
    }

    private static WorkbenchDefinition Definition(JsonElement? actions) =>
        new(ModuleId: 130101, Title: "库存盘点单", MasterTable: "INV_CHECK_STOCK_M", DetailTable: "INV_CHECK_STOCK_D",
            MasterFields: [], DetailFields: [], DefaultSort: null, HasAdd: true, HasEdit: true, DetailNoSave: false,
            MasterPkOrder: ["CHECK_NO"], DetailNoFields: "", HasWorkflow: false,
            BusinessActions: actions, EffectEngine: JsonSerializer.SerializeToElement(new { enabled = true }));

    [Fact]
    public void EffectPlanLoader_SkipsManualRows_ButKeepsEffectRows()
    {
        var definition = Definition(Actions(
            new { seq = 1, eventCode = "MANUAL", effectKey = "recalc-account", enabled = true },
            new { seq = 2, eventCode = "SAVE", effectKey = "stocktake-scope-generate", enabled = true }));

        var plan = new EffectPlanLoader().Load(definition);

        var effect = Assert.Single(plan.Actions);
        Assert.Equal("stocktake-scope-generate", effect.EffectKey);
    }

    [Fact]
    public void DeclaresDetailGenerator_IgnoresManualRows()
    {
        Assert.False(EffectEngineInvoker.DeclaresDetailGenerator(Actions(
            new { seq = 1, eventCode = "MANUAL", effectKey = "stocktake-scope-generate", enabled = true })));
        Assert.True(EffectEngineInvoker.DeclaresDetailGenerator(Actions(
            new { seq = 1, eventCode = "SAVE", effectKey = "stocktake-scope-generate", enabled = true })));
    }

    // ===== 配置校验（保存即校验 + 发布门） =====

    private static readonly IReadOnlySet<string> RegisteredActions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { DocumentActionProbeHandler.ActionKey };

    private static BusinessActionDto ManualRow(
        string effectKey = DocumentActionProbeHandler.ActionKey,
        string? reverse = null,
        string? parameters = null,
        IReadOnlyList<BusinessActionOpDto>? ops = null) =>
        new(Seq: 1, EventCode: "MANUAL", EffectKey: effectKey, EffectName: "管线探针", Enabled: true, FailMode: "BLOCK",
            Condition: null, Params: parameters, Reverse: reverse, Remark: null, SourceRef: null, Ops: ops);

    [Fact]
    public void ConfigValidator_AcceptsRegisteredButtonKey()
    {
        var request = new SaveModuleBusinessConfigRequest([ManualRow()], []);

        Assert.Empty(ModuleBusinessConfigValidator.Validate(request, RegisteredActions));
    }

    [Fact]
    public void ConfigValidator_RejectsUnregisteredButtonKey_EvenWhenItLooksLikeAnEffect()
    {
        var request = new SaveModuleBusinessConfigRequest([ManualRow(effectKey: "set-state")], []);

        var issues = ModuleBusinessConfigValidator.Validate(request, RegisteredActions);

        Assert.Contains(issues, issue => issue.Contains("未知自定义按钮键"));
    }

    [Fact]
    public void ConfigValidator_RejectsFormulaRowsAndReverseStruct_OnManualRows()
    {
        var request = new SaveModuleBusinessConfigRequest(
            [
                ManualRow(reverse: """{"kind":"none"}""",
                    ops: [new BusinessActionOpDto(1, "DEPOT", "DEPOT_NAME", "ASSIGN", "CONSTANT", null, null, null, "x", null, null, null)]),
            ], []);

        var issues = ModuleBusinessConfigValidator.Validate(request, RegisteredActions);

        Assert.Contains(issues, issue => issue.Contains("不支持反向结构"));
        Assert.Contains(issues, issue => issue.Contains("不支持公式行"));
    }

    // ===== 定义下发（userActions）=====

    [Fact]
    public void MetadataFactory_OnlyPublishesAuthorizedButtons_AndFallsBackToHandlerLabel()
    {
        var registry = Registry(
            new StubAction(DocumentActionProbeHandler.ActionKey, "管线探针", DocumentActionPlacements.Detail),
            new StubAction("recalc-account", "重算账面数"));
        var actions = Actions(
            new { seq = 1, eventCode = "MANUAL", effectKey = DocumentActionProbeHandler.ActionKey, enabled = true, confirmTag = true },
            new { seq = 2, eventCode = "MANUAL", effectKey = "recalc-account", enabled = true, label = "重算账面数量" },
            new { seq = 3, eventCode = "MANUAL", effectKey = "never-granted", enabled = true });
        var configured = DocumentActionConfigs.Parse(actions);

        var published = DocumentActionMetadataFactory.Build(
            configured,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { DocumentActionProbeHandler.ActionKey, "recalc-account" },
            registry);

        Assert.Equal(2, published.Count);
        // 未授权的按钮不下发：界面上根本不存在这个按钮，而不是渲染成禁用。
        Assert.DoesNotContain(published, item => item.Key == "never-granted");
        var probe = Assert.Single(published, item => item.Key == DocumentActionProbeHandler.ActionKey);
        Assert.Equal("管线探针", probe.Label);
        Assert.True(probe.ConfirmTag);
        Assert.Equal(DocumentActionPlacements.Detail, probe.Placement);
        var recalc = Assert.Single(published, item => item.Key == "recalc-account");
        Assert.Equal("重算账面数量", recalc.Label);
        Assert.Equal(DocumentActionPlacements.Master, recalc.Placement);
    }

    [Fact]
    public void ConfigValidator_ValidatesParameterDeclaration_OnManualRows()
    {
        var request = new SaveModuleBusinessConfigRequest([ManualRow(parameters: """{"fields":[{"key":"a","type":"string"}]}""")], []);

        var issues = ModuleBusinessConfigValidator.Validate(request, RegisteredActions);

        Assert.Contains(issues, issue => issue.Contains("缺少 label"));
    }
}
