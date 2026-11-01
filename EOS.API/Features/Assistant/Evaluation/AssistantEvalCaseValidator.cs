using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using EOS.API.Data;
using EOS.API.Data.Workbench;
using EOS.API.Features.Assistant.Config;
using EOS.API.Features.Assistant.Parameters;
using EOS.API.Features.Assistant.Situation;
using EOS.API.Features.Assistant.Tools;

namespace EOS.API.Features.Assistant.Evaluation;

/// <summary>一条检查的结论。</summary>
public enum AssistantEvalOutcome
{
    /// <summary>检查跑过且通过。</summary>
    Passed,

    /// <summary>检查跑过且失败——用例与当前系统已经对不上，需要按分诊去改。</summary>
    Failed,

    /// <summary>这条检查在运行期做不了（要仓库或要模型）。**跳过不等于通过**。</summary>
    Skipped,
}

/// <summary>一条检查：稳定码 + 结论 + 给人看的一句话。</summary>
public sealed record AssistantEvalCheck(string Code, AssistantEvalOutcome Outcome, string Message);

/// <summary>
/// 运行期能拿到的"系统现状"快照：模块号集合与启用的校验规则键。
///
/// <para>
/// 它让一部分检查从"只能读源码判断"变成"可以按真库判断"（规则被删了、模块下线了，
/// 黄金集就当场对不上）。没有快照时这些检查记为跳过，而不是假装通过。
/// </para>
/// </summary>
public sealed record AssistantEvalLookups(
    IReadOnlySet<int> ModuleIds,
    IReadOnlySet<string> ValidationRules)
{
    /// <summary>校验规则键：模块号 + 阶段 + 校验键（与 `MODULE_VALIDATION_RULE` 主判据同形）。</summary>
    public static string RuleKey(int moduleId, string stage, string validationKey) =>
        string.Create(CultureInfo.InvariantCulture, $"{moduleId}|{stage}|{validationKey}");
}

/// <summary>
/// 一条评估用例在运行期的形状。库行与仓库种子都能装进来——这正是"库内用例"与
/// "离线门禁用例"能共用同一套判定的原因。
/// </summary>
public sealed record AssistantEvalCaseRecord(
    string CaseId,
    string Layer,
    string? Question,
    string? Expected,
    string? ExpectTools,
    string? PageType,
    string? PayloadJson,
    string? FixAt = null,
    string? FixSuggestion = null,
    string? FixPermission = null);

/// <summary>
/// **确定性层**判定：用例与当前系统是否已经对不上（闭集、注册表、库内元数据）。
///
/// <para>
/// 它判的不是"模型答得对不对"——那需要判官（ADR-044）。所以这里没有模型调用、没有费用、
/// 可以随时跑；跑出来 FAIL 的意思是**这条用例已经不能反映系统现状**（工具改名了、
/// 规则删了、原因码不在闭集里），运维按分诊去改就行。
/// </para>
///
/// <para>
/// 每个断言都只引用**代码里的唯一真源**（`AssistantToolKeys` / `AssistantToolSetPolicy` /
/// `SituationDigestKinds` / `WorkbenchDenialCodes` / `ConfigConsistencyRules`）：闭集改了，
/// 这个类跟着编译期一起变，不会长出第二套清单。
/// </para>
/// </summary>
public static class AssistantEvalCaseValidator
{
    /// <summary>支持的八个层（与 `EOS.API.Tests/AssistantEval/` 的子目录一一对应）。</summary>
    public static IReadOnlyList<string> Layers { get; } =
        ["situation", "diagnosis", "action", "config", "authz", "inference", "functional", "business"];

    /// <summary>处境层摘要类别（取自 <see cref="SituationDigestKinds"/>）。</summary>
    private static readonly string[] SituationKinds =
        [SituationDigestKinds.Overdue, SituationDigestKinds.BlockedNow, SituationDigestKinds.Rejected];

    /// <summary>动作层的动作枚举。</summary>
    private static readonly string[] ActionKinds = ["insert", "update", "delete"];

    /// <summary>配置层的四个配置面。</summary>
    private static readonly string[] ConfigSurfaces = ["fields", "datasource", "buttons", "effect"];

    /// <summary>`authz` 层的期望结论。</summary>
    private static readonly string[] AuthzExpectations = ["deny", "empty", "all_only", "ok"];

    /// <summary>归因样本的期望结论：要么拒绝给归因（证据冲突/不足），要么给出归因。</summary>
    private static readonly string[] AttributionExpectations = ["deny", "ok"];

    /// <summary>诊断层归因类别（与 `DiagnosisClasses` 的公开类别同形）。</summary>
    private static readonly string[] DiagnosisClasses = ["validation", "fieldGuard", "blockers"];

    /// <summary>校验类样本可用的阶段（其余类别不来自 `MODULE_VALIDATION_RULE`）。</summary>
    private static readonly string[] ValidationStages = ["SAVE", "APPROVE", "DEAPPROVE", "DELETE"];

    /// <summary>模块级拒绝原因码（取自 <see cref="WorkbenchDenialCodes"/>）。</summary>
    private static readonly string[] ModuleDenialCodes =
    [
        WorkbenchDenialCodes.NotAuthenticated, WorkbenchDenialCodes.NotBrowsable,
        WorkbenchDenialCodes.DefinitionUnavailable, WorkbenchDenialCodes.FormNotEnabled,
        WorkbenchDenialCodes.ModeNotPermitted, WorkbenchDenialCodes.AddCapabilityMissing,
        WorkbenchDenialCodes.EditCapabilityMissing, WorkbenchDenialCodes.FormUnavailable,
        WorkbenchDenialCodes.NoWritableSurface, WorkbenchDenialCodes.NoWritablePage,
        WorkbenchDenialCodes.SetupNotAllowed, WorkbenchDenialCodes.InvalidFormMode,
        WorkbenchDenialCodes.IdempotencyKeyRequired, WorkbenchDenialCodes.DeleteNotPermitted,
    ];

    /// <summary>配置不一致原因码（取自 <see cref="ConfigConsistencyRules"/>）。</summary>
    private static readonly string[] ConfigCodes =
    [
        ConfigConsistencyRules.ReverseNoteNotRead, ConfigConsistencyRules.ReverseKindUnsupported,
        ConfigConsistencyRules.EffectKeySingleton,
    ];

    /// <summary>
    /// 批核族与权限授予类工具：它们在助手侧**没有注册项**，样本若期望它们，说明这条用例
    /// 已经越过了能力边界。与离线门禁各自持一份清单是刻意的——两份都写死，才有一份是独立见证。
    /// </summary>
    private static readonly string[] RedLineTools =
    [
        "approve_record", "deapprove_record", "end_case", "unend_case",
        "batch_approve", "grant_module_rights", "set_user_permission", "apply_permission",
    ];

    /// <summary>来源里的模块映射形态：`ModuleBusinessMap[1404]`。</summary>
    private static readonly Regex ModuleMapSource = new(@"^ModuleBusinessMap\[(\d+)\]$", RegexOptions.Compiled);

    /// <summary>来源里的工具形态：`search_records:1405`（工具 + 作用模块）。</summary>
    private static readonly Regex ToolSource = new(@"^([a-z][a-z0-9_]*):(\d+)$", RegexOptions.Compiled);

    /// <summary>
    /// 跑完一条用例的全部确定性检查。**任何一条 Failed 即整条失败**；Skipped 不计入通过。
    /// </summary>
    public static IReadOnlyList<AssistantEvalCheck> Validate(
        AssistantEvalCaseRecord record, AssistantEvalLookups? lookups = null)
    {
        var layer = (record.Layer ?? string.Empty).Trim().ToLowerInvariant();
        if (!Layers.Contains(layer, StringComparer.Ordinal))
        {
            return [Failed("CASE_LAYER_UNKNOWN", $"层「{record.Layer}」不在八层之内，运行器不知道该怎么判它。")];
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(string.IsNullOrWhiteSpace(record.PayloadJson) ? "null" : record.PayloadJson);
        }
        catch (JsonException exception)
        {
            return [Failed("CASE_PAYLOAD_INVALID", $"用例正文不是合法 JSON：{exception.Message}")];
        }

        using (document)
        {
            var payload = document.RootElement;
            if (payload.ValueKind != JsonValueKind.Object)
            {
                return [Failed("CASE_PAYLOAD_INVALID", "用例正文必须是一个 JSON 对象。")];
            }

            var checks = new List<AssistantEvalCheck>();
            CheckIdentity(record, payload, checks);

            switch (layer)
            {
                case "situation": CheckSituation(payload, lookups, checks); break;
                case "diagnosis": CheckDiagnosis(payload, lookups, checks); break;
                case "action": CheckAction(payload, lookups, checks); break;
                case "config": CheckConfig(payload, checks); break;
                case "authz": CheckAuthz(payload, checks); break;
                default: CheckCorpus(record, payload, checks); break;   // functional / business / inference
            }

            return checks;
        }
    }

    /// <summary>用例身份：正文里的 id（若有）必须与主键一致——两份名字对不上时，改的那份不是跑的那份。</summary>
    private static void CheckIdentity(
        AssistantEvalCaseRecord record, JsonElement payload, List<AssistantEvalCheck> checks)
    {
        if (!payload.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String) return;

        var declared = id.GetString();
        checks.Add(string.Equals(declared, record.CaseId, StringComparison.Ordinal)
            ? Passed("CASE_IDENTITY", $"用例标识一致（{record.CaseId}）。")
            : Failed("CASE_ID_MISMATCH", $"正文里的 id「{declared}」与用例编号「{record.CaseId}」不一致。"));
    }

    private static void CheckSituation(
        JsonElement payload, AssistantEvalLookups? lookups, List<AssistantEvalCheck> checks)
    {
        if (!payload.TryGetProperty("situation", out var situation) || situation.ValueKind != JsonValueKind.Object)
        {
            checks.Add(Failed("SITUATION_MISSING", "处境层用例必须带 situation 段。"));
            return;
        }

        CheckModuleExists(situation, "moduleId", lookups, checks);

        if (!payload.TryGetProperty("expect", out var expect) || expect.ValueKind != JsonValueKind.Object)
        {
            checks.Add(Failed("SITUATION_EXPECT_MISSING", "处境层用例必须带 expect 段（期望的摘要类别与是否零模型）。"));
            return;
        }

        if (expect.TryGetProperty("kinds", out var kinds) && kinds.ValueKind == JsonValueKind.Array)
        {
            var declared = kinds.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString() ?? string.Empty)
                .ToList();
            var unknown = declared.Where(kind => !SituationKinds.Contains(kind, StringComparer.Ordinal)).ToList();
            checks.Add(unknown.Count > 0
                ? Failed("SITUATION_KIND_UNKNOWN",
                    $"摘要类别 {string.Join("/", unknown)} 不在闭集内（可用：{string.Join("/", SituationKinds)}）。")
                : Passed("SITUATION_KINDS", declared.Count == 0
                    ? "摘要类别为空：这条用例断言的是「打开时无可报条目」。"
                    : $"摘要类别都在闭集内（{string.Join("/", declared)}）。"));
        }
        else
        {
            checks.Add(Failed("SITUATION_KINDS_MISSING", "expect.kinds 必须是数组。"));
        }

        if (expect.TryGetProperty("zeroModel", out var zeroModel) && zeroModel.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            checks.Add(zeroModel.GetBoolean()
                ? Passed("SITUATION_ZERO_MODEL", "打开即见声明为零模型调用。")
                : Failed("SITUATION_ZERO_MODEL_OFF",
                    "打开即见必须零模型调用：允许调模型的摘要会回答变慢、也变成要付费的前置。"));
        }
        else
        {
            checks.Add(Failed("SITUATION_ZERO_MODEL_MISSING", "expect.zeroModel 必须显式写 true。"));
        }
    }

    private static void CheckDiagnosis(
        JsonElement payload, AssistantEvalLookups? lookups, List<AssistantEvalCheck> checks)
    {
        // 诊断层里躺着三种形态，判定各归各家（混成一套会逼着样本削足适履）：
        //   ① 黄金集：一条真实校验规则（可追溯到 MODULE_VALIDATION_RULE）；
        //   ② 归因样本：一次"命中事实对不上规则"的负样本（hitFact），判的是服务端会不会乱套用相似规则；
        //   ③ 观测样本：生产里真实发生过的拒绝（source=AUDIT_EVENT），用于改规则/补知识库。
        if (payload.TryGetProperty("hitFact", out var hitFact) && hitFact.ValueKind == JsonValueKind.Object)
        {
            CheckDiagnosisAttribution(hitFact, payload, lookups, checks);
            return;
        }

        if (string.Equals(Text(payload, "expect"), "observed-rejection", StringComparison.Ordinal))
        {
            CheckDiagnosisObservation(payload, lookups, checks);
            return;
        }

        CheckDiagnosisGoldenSet(payload, lookups, checks);
    }

    /// <summary>
    /// 归因样本（负样本）：命中事实与登记对不上时，服务端必须报证据冲突/证据不足，而不是就近套用相似规则。
    /// 运行期只核结构；"真会不会乱套用"要跑诊断工具（它读单据、走权限门），本层不判。
    /// </summary>
    private static void CheckDiagnosisAttribution(
        JsonElement hitFact, JsonElement payload, AssistantEvalLookups? lookups, List<AssistantEvalCheck> checks)
    {
        // 只要求能认出"这是哪张单的哪条规则"：moduleId 必须有（下面要核模块是否还在）；
        // 阶段与校验键缺不缺**不由本层判**——"命中事实本来就不完整"正是其中一类负样本要考的东西
        // （服务端该报证据不足，而不是就近套用相似规则）。
        checks.Add(HasText(hitFact, "moduleId")
            ? Passed("DIAGNOSIS_HIT_FACT", "命中事实指出了模块" + DescribeHitFact(hitFact) + "。")
            : Failed("DIAGNOSIS_HIT_FACT_INCOMPLETE", "命中事实缺 moduleId：认不出这是哪张单的诊断。"));

        var reasonKind = Text(payload, "reasonKind");
        checks.Add(!string.IsNullOrWhiteSpace(reasonKind)
            ? Passed("DIAGNOSIS_REASON_KIND", $"样本声明了期望归因结果「{reasonKind}」。")
            : Failed("DIAGNOSIS_REASON_KIND_MISSING", "归因样本必须写清期望的归因结果（reasonKind）。"));

        var expect = Text(payload, "expect");
        checks.Add(expect is not null && AttributionExpectations.Contains(expect, StringComparer.Ordinal)
            ? Passed("DIAGNOSIS_EXPECT", $"期望结论「{expect}」在闭集内。")
            : Failed("DIAGNOSIS_EXPECT_UNKNOWN",
                $"期望结论「{expect}」不在 {string.Join("/", AttributionExpectations)} 之内。"));

        CheckModuleExists(hitFact, "moduleId", lookups, checks);

        checks.Add(Skipped("DIAGNOSIS_ATTRIBUTION_RUN",
            "归因正确性要真跑诊断工具（读单据、过权限门），本层只核样本自身结构与模块是否还在。"));
    }

    /// <summary>命中事实里实际写了哪几项（给人看，不做判定）。</summary>
    private static string DescribeHitFact(JsonElement hitFact)
    {
        var parts = new List<string>();
        if (HasText(hitFact, "stage")) parts.Add($"阶段 {Text(hitFact, "stage")}");
        if (HasText(hitFact, "validationKey")) parts.Add($"校验键 {Text(hitFact, "validationKey")}");
        if (HasText(hitFact, "seq")) parts.Add($"序号 {Text(hitFact, "seq")}");
        return parts.Count == 0 ? "（未写阶段/校验键）" : "：" + string.Join("、", parts);
    }

    /// <summary>
    /// 观测样本：生产里真实发生过的拒绝。它的价值是"这条拒绝应当被解释清楚"，不是"规则可追溯"。
    /// 运行期核结构 + 模块是否还在（模块下线了，这条观测就该被重写或退役）。
    /// </summary>
    private static void CheckDiagnosisObservation(
        JsonElement payload, AssistantEvalLookups? lookups, List<AssistantEvalCheck> checks)
    {
        var missing = new[] { "action", "summary", "source" }
            .Where(key => !HasText(payload, key))
            .ToList();
        checks.Add(missing.Count == 0
            ? Passed("DIAGNOSIS_OBSERVATION_KEYS", "观测样本写清了动作、原文与来源。")
            : Failed("DIAGNOSIS_OBSERVATION_INCOMPLETE", $"观测样本缺 {string.Join("/", missing)}。"));

        var occurrences = payload.TryGetProperty("occurrences", out var occurrencesElement)
                          && occurrencesElement.ValueKind == JsonValueKind.Number
            ? occurrencesElement.GetInt32()
            : 0;
        checks.Add(occurrences >= 1
            ? Passed("DIAGNOSIS_OCCURRENCES", $"实测发生 {occurrences} 次。")
            : Failed("DIAGNOSIS_OCCURRENCES_MISSING", "观测样本必须带实测发生次数（occurrences ≥ 1）。"));

        CheckModuleExists(payload, "moduleId", lookups, checks);

        checks.Add(Skipped("DIAGNOSIS_OBSERVATION_RUN",
            "观测样本用于改规则/补知识库；能否被诊断说清楚要真跑一次，本层不判。"));
    }

    /// <summary>黄金集：一条真实校验规则。库内可核的那一半正是"规则还在不在、模块还在不在"。</summary>
    private static void CheckDiagnosisGoldenSet(
        JsonElement payload, AssistantEvalLookups? lookups, List<AssistantEvalCheck> checks)
    {
        var missing = new[] { "moduleId", "stage", "validationKey", "message", "suggestedClass" }
            .Where(key => !HasText(payload, key))
            .ToList();
        checks.Add(missing.Count == 0
            ? Passed("DIAGNOSIS_KEYS", "诊断样本的五项可追溯字段齐备。")
            : Failed("DIAGNOSIS_KEYS_MISSING", $"诊断样本缺 {string.Join("/", missing)}：它必须能追溯到具体规则与文案。"));

        var suggestedClass = Text(payload, "suggestedClass");
        var stage = Text(payload, "stage");
        if (suggestedClass is not null)
        {
            if (!DiagnosisClasses.Contains(suggestedClass, StringComparer.Ordinal))
            {
                checks.Add(Failed("DIAGNOSIS_CLASS_UNKNOWN",
                    $"归因类别「{suggestedClass}」不在闭集内（可用：{string.Join("/", DiagnosisClasses)}）。"));
            }
            else if (suggestedClass == "validation" && stage is not null
                     && !ValidationStages.Contains(stage, StringComparer.Ordinal))
            {
                checks.Add(Failed("DIAGNOSIS_STAGE_INVALID",
                    $"校验类样本的阶段「{stage}」不来自 MODULE_VALIDATION_RULE（可用：{string.Join("/", ValidationStages)}）。"));
            }
            else
            {
                checks.Add(Passed("DIAGNOSIS_CLASS", $"归因类别与阶段搭配成立（{suggestedClass}/{stage}）。"));
            }
        }

        // 库内可核的那一半：规则还在不在、模块还在不在。样本用途正是"黄金集与元数据不许漂"。
        if (lookups is null)
        {
            checks.Add(Skipped("DIAGNOSIS_RULE_LOOKUP", "未取得库内元数据快照，规则是否仍在库未判。"));
            return;
        }

        var moduleText = Text(payload, "moduleId");
        if (moduleText is null || !int.TryParse(moduleText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var moduleId))
        {
            checks.Add(Skipped("DIAGNOSIS_MODULE_SCOPE",
                $"moduleId「{moduleText}」不是单模块（代码级样本），不按某个模块的规则核对。"));
            return;
        }

        checks.Add(lookups.ModuleIds.Contains(moduleId)
            ? Passed("MODULE_EXISTS", $"模块 {moduleId} 仍在 MODULES 里。")
            : Failed("MODULE_NOT_FOUND", $"模块 {moduleId} 已不在 MODULES 里：这条规则样本指向了不存在的模块。"));

        // 只有 validation 类来自 MODULE_VALIDATION_RULE；fieldGuard / blockers 是代码级守卫，
        // 拿规则表去核它们只会得到一个恒假的失败。
        if (!string.Equals(suggestedClass, "validation", StringComparison.Ordinal))
        {
            checks.Add(Skipped("DIAGNOSIS_RULE_SCOPE",
                $"归因类别「{suggestedClass}」是代码级守卫，不来自 MODULE_VALIDATION_RULE。"));
            return;
        }

        var validationKey = Text(payload, "validationKey");
        if (validationKey is null || stage is null) return;

        var key = AssistantEvalLookups.RuleKey(moduleId, stage, validationKey);
        checks.Add(lookups.ValidationRules.Contains(key)
            ? Passed("DIAGNOSIS_RULE_EXISTS", $"规则 {moduleId}/{stage}/{validationKey} 仍在库且启用。")
            : Failed("DIAGNOSIS_RULE_NOT_FOUND",
                $"规则 {moduleId}/{stage}/{validationKey} 在 MODULE_VALIDATION_RULE 里找不到（已删或已停用）："
                + "这条样本要按现状重写，否则诊断跑分会永远算它没过。"));
    }

    private static void CheckAction(
        JsonElement payload, AssistantEvalLookups? lookups, List<AssistantEvalCheck> checks)
    {
        var action = Text(payload, "action");
        checks.Add(action is not null && ActionKinds.Contains(action, StringComparer.Ordinal)
            ? Passed("ACTION_KIND", $"动作「{action}」在录入能力面内。")
            : Failed("ACTION_INVALID", $"动作「{action}」不在 {string.Join("/", ActionKinds)} 之内。"));

        if (payload.TryGetProperty("rows", out var rows) && rows.ValueKind == JsonValueKind.Array)
        {
            checks.Add(rows.GetArrayLength() > 0
                ? Passed("ACTION_ROWS", $"样本带 {rows.GetArrayLength()} 行数据。")
                : Failed("ACTION_ROWS_EMPTY", "样本没有带任何行：没有行就断言不了「改哪一个字段」。"));
        }
        else
        {
            checks.Add(Failed("ACTION_ROWS_MISSING", "样本必须带 rows 数组。"));
        }

        if (payload.TryGetProperty("expect", out var expect) && expect.ValueKind == JsonValueKind.Object
            && expect.TryGetProperty("moduleCode", out var code) && code.ValueKind == JsonValueKind.String)
        {
            var value = code.GetString() ?? string.Empty;
            checks.Add(ModuleDenialCodes.Contains(value, StringComparer.Ordinal)
                ? Passed("ACTION_CODE", $"模块级原因码「{value}」来自策略层闭集。")
                : Failed("ACTION_CODE_UNKNOWN",
                    $"模块级原因码「{value}」不在闭集内（可用：{string.Join("/", ModuleDenialCodes)}）。"));
        }
        else
        {
            checks.Add(Skipped("ACTION_CODE_NONE", "样本不期望模块级拒绝（moduleCode 为空），原因码无从核对。"));
        }

        CheckModuleExists(payload, "moduleId", lookups, checks);
    }

    private static void CheckConfig(JsonElement payload, List<AssistantEvalCheck> checks)
    {
        var surface = Text(payload, "surface");
        checks.Add(surface is not null && ConfigSurfaces.Contains(surface, StringComparer.Ordinal)
            ? Passed("CONFIG_SURFACE", $"配置面「{surface}」在四个之内。")
            : Failed("CONFIG_SURFACE_UNKNOWN",
                $"配置面「{surface}」不在 {string.Join("/", ConfigSurfaces)} 之内。"));

        var code = Text(payload, "expectCode");
        if (code is null)
        {
            checks.Add(Skipped("CONFIG_CODE_NONE", "样本不期望不一致结论（expectCode 为空）。"));
            return;
        }

        checks.Add(ConfigCodes.Contains(code, StringComparer.Ordinal)
            ? Passed("CONFIG_CODE", $"不一致原因码「{code}」来自规则闭集。")
            : Failed("CONFIG_CODE_UNKNOWN",
                $"不一致原因码「{code}」不在闭集内（可用：{string.Join("/", ConfigCodes)}）。"));
    }

    private static void CheckAuthz(JsonElement payload, List<AssistantEvalCheck> checks)
    {
        var tool = Text(payload, "tool");
        checks.Add(tool is not null && AssistantToolKeys.ToolNames.Contains(tool, StringComparer.Ordinal)
            ? Passed("AUTHZ_TOOL", $"受试工具「{tool}」在注册表里。")
            : Failed("AUTHZ_TOOL_UNKNOWN",
                $"受试工具「{tool}」不在注册表（AssistantToolKeys.ToolNames）里：越权样本必须打真实存在的工具。"));

        var expect = Text(payload, "expect");
        checks.Add(expect is not null && AuthzExpectations.Contains(expect, StringComparer.Ordinal)
            ? Passed("AUTHZ_EXPECT", $"期望结论「{expect}」在闭集内。")
            : Failed("AUTHZ_EXPECT_UNKNOWN",
                $"期望结论「{expect}」不在 {string.Join("/", AuthzExpectations)} 之内。"));

        checks.Add(Skipped("AUTHZ_EXECUTION",
            "越权样本的实际拒绝要按真实身份调用一次工具（写入面会落数据），本层只核样本自身一致性。"));
    }

    /// <summary>检索/回答/推断三层：问句、期望工具链、以及能在运行期核对的来源。</summary>
    private static void CheckCorpus(
        AssistantEvalCaseRecord record, JsonElement payload, List<AssistantEvalCheck> checks)
    {
        var layer = (record.Layer ?? string.Empty).Trim().ToLowerInvariant();
        var question = Text(payload, "question") ?? record.Question;
        var expected = Text(payload, "expected") ?? record.Expected;
        checks.Add(!string.IsNullOrWhiteSpace(question) && !string.IsNullOrWhiteSpace(expected)
            ? Passed("CASE_EXPECTATION", "问句与期望答案都在。")
            : Failed("CASE_EXPECTATION_MISSING", "检索/回答层用例必须同时写清问句与期望答案，否则抽样时无从判定。"));

        // 期望工具链有两个落点（结构化列 + 正文），两边不一致时以列为主，并把不一致本身报出来：
        // 3106 的维护者只改一处时，评估会用另一处继续判，谁也不知道自己改的那份没生效。
        var columnTools = SplitTools(record.ExpectTools);
        var payloadTools = payload.TryGetProperty("expect_tools", out var toolsElement)
                           && toolsElement.ValueKind == JsonValueKind.Array
            ? toolsElement.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString() ?? string.Empty)
                .Where(item => item.Length > 0)
                .ToList()
            : [];
        if (columnTools.Count > 0 && payloadTools.Count > 0)
        {
            var same = columnTools.Count == payloadTools.Count
                && columnTools.All(tool => payloadTools.Contains(tool, StringComparer.Ordinal));
            checks.Add(same
                ? Passed("EXPECT_TOOLS_AGREE", "期望工具链在列与正文里一致。")
                : Failed("EXPECT_TOOLS_DISAGREE",
                    $"期望工具链两处不一致：列为 {string.Join(",", columnTools)}，正文为 {string.Join(",", payloadTools)}。"));
        }

        var page = Text(payload, "page") ?? record.PageType;
        var expectTools = columnTools.Count > 0 ? columnTools : payloadTools;
        if (expectTools.Count == 0)
        {
            checks.Add(!string.IsNullOrWhiteSpace(page)
                ? Failed("EXPECT_TOOLS_MISSING", $"声明了 page「{page}」却没有期望工具链：等于什么都没断言。")
                : Skipped("EXPECT_TOOLS_NONE", "这条样本没有声明期望工具链，样本自身一致性之外无可判定。"));
        }
        else
        {
            var problems = new List<string>();
            foreach (var tool in expectTools)
            {
                if (RedLineTools.Contains(tool, StringComparer.Ordinal))
                {
                    problems.Add($"「{tool}」是批核族/权限授予类，助手侧没有注册项");
                    continue;
                }
                if (!AssistantToolKeys.ToolNames.Contains(tool, StringComparer.Ordinal))
                {
                    problems.Add($"「{tool}」不在注册表里");
                    continue;
                }
                if (!string.IsNullOrWhiteSpace(page) && !AssistantToolSetPolicy.IsOffered(tool, page))
                {
                    problems.Add($"「{tool}」在页面「{page}」下不会发给模型（被页面策略裁掉）");
                }
            }

            checks.Add(problems.Count == 0
                ? Passed("EXPECT_TOOLS", $"期望工具链可用（{string.Join(",", expectTools)}"
                    + (string.IsNullOrWhiteSpace(page) ? "）。" : $"，页面 {page}）。"))
                : Failed("EXPECT_TOOLS_UNUSABLE", string.Join("；", problems) + "。"));
        }

        if (payload.TryGetProperty("reviewed", out var reviewed) && reviewed.ValueKind == JsonValueKind.True)
        {
            var missing = new List<string>();
            if (string.IsNullOrWhiteSpace(expected)) missing.Add("expected");
            if (expectTools.Count == 0) missing.Add("expect_tools");
            checks.Add(missing.Count == 0
                ? Passed("CASE_REVIEWED", "已审阅样本写全了期望答案与工具链。")
                : Failed("CASE_REVIEWED_INCOMPLETE", $"标记 reviewed:true 却缺 {string.Join("/", missing)}。"));
        }

        if (payload.TryGetProperty("sources", out var sourcesElement) && sourcesElement.ValueKind == JsonValueKind.Array)
        {
            var sources = sourcesElement.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString() ?? string.Empty)
                .ToList();
            if (sources.Count == 0 || sources.All(string.IsNullOrWhiteSpace))
            {
                checks.Add(Failed("SOURCES_EMPTY", "样本没有声明任何依据：自造依据的样本无法核对。"));
            }
            else
            {
                var bad = new List<string>();
                var uncheckedForms = 0;
                foreach (var source in sources)
                {
                    if (string.IsNullOrWhiteSpace(source)) { bad.Add("[空来源]"); continue; }
                    var map = ModuleMapSource.Match(source);
                    if (map.Success)
                    {
                        var moduleId = int.Parse(map.Groups[1].Value, CultureInfo.InvariantCulture);
                        if (ModuleBusinessMap.Get(moduleId) is null
                            && !ModuleBusinessMap.MasterTables.ContainsKey(moduleId))
                        {
                            bad.Add($"模块 {moduleId} 未在 ModuleBusinessMap 登记");
                        }
                        continue;
                    }

                    var tool = ToolSource.Match(source);
                    if (tool.Success)
                    {
                        if (!AssistantToolKeys.ToolNames.Contains(tool.Groups[1].Value, StringComparer.Ordinal))
                        {
                            bad.Add($"工具「{tool.Groups[1].Value}」不在注册表里");
                        }
                        continue;
                    }

                    uncheckedForms++;
                }

                if (bad.Count > 0)
                {
                    checks.Add(Failed("SOURCES_UNANCHORED", $"依据对不上当前系统：{string.Join("；", bad)}。"));
                }
                else if (uncheckedForms > 0)
                {
                    checks.Add(Skipped("SOURCES_REPO_ONLY",
                        $"{uncheckedForms} 条依据是文件或代码符号形态，只有离线门禁能核对（需要仓库）。"));
                }
                else
                {
                    checks.Add(Passed("SOURCES_ANCHORED", $"依据可核对（{sources.Count} 条全部命中当前系统）。"));
                }
            }
        }
        else
        {
            checks.Add(Skipped("SOURCES_MISSING", "样本没有 sources 段，依据核对跳过。"));
        }

        if (layer == "inference")
        {
            // 推断层的设计口径是"答案跨 ≥3 源"，但**拒答样本**（口径未定义、如实说明查不到）
            // 天然只需一个工具。所以这里不数条目、只核一致性：被点名的必备来源必须真在 sources 里。
            var declared = payload.TryGetProperty("sources", out var declaredElement)
                           && declaredElement.ValueKind == JsonValueKind.Array
                ? declaredElement.EnumerateArray()
                    .Where(item => item.ValueKind == JsonValueKind.String)
                    .Select(item => item.GetString() ?? string.Empty)
                    .ToList()
                : [];
            var required = payload.TryGetProperty("required_sources", out var requiredElement)
                           && requiredElement.ValueKind == JsonValueKind.Array
                ? requiredElement.EnumerateArray()
                    .Where(item => item.ValueKind == JsonValueKind.String)
                    .Select(item => item.GetString() ?? string.Empty)
                    .ToList()
                : [];
            if (required.Count == 0)
            {
                checks.Add(Skipped("INFERENCE_SOURCES_NONE", "这条推断样本没有点名必备来源，跨源要求无从核对。"));
            }
            else
            {
                var missing = required
                    .Where(item => !declared.Contains(item, StringComparer.Ordinal))
                    .ToList();
                checks.Add(missing.Count == 0
                    ? Passed("INFERENCE_SOURCES", $"点名的 {required.Count} 条必备来源都在 sources 里。")
                    : Failed("INFERENCE_SOURCES_MISMATCH",
                        $"必备来源 {string.Join("、", missing)} 不在 sources 里：期望依据与实际声明对不上。"));
            }
        }
    }

    private static void CheckModuleExists(
        JsonElement payload, string property, AssistantEvalLookups? lookups, List<AssistantEvalCheck> checks)
    {
        var text = Text(payload, property);
        if (text is null || !int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var moduleId))
        {
            return;
        }

        if (lookups is null)
        {
            checks.Add(Skipped("MODULE_LOOKUP", $"未取得库内元数据快照，模块 {moduleId} 是否仍在库未判。"));
            return;
        }

        checks.Add(lookups.ModuleIds.Contains(moduleId)
            ? Passed("MODULE_EXISTS", $"模块 {moduleId} 仍在 MODULES 里。")
            : Failed("MODULE_NOT_FOUND", $"模块 {moduleId} 已不在 MODULES 里。"));
    }

    private static string? Text(JsonElement payload, string property)
    {
        if (!payload.TryGetProperty(property, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null,
        };
    }

    private static bool HasText(JsonElement payload, string property) =>
        !string.IsNullOrWhiteSpace(Text(payload, property));

    private static List<string> SplitTools(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(item => item.Length > 0)
                .ToList();

    private static AssistantEvalCheck Passed(string code, string message) =>
        new(code, AssistantEvalOutcome.Passed, message);

    private static AssistantEvalCheck Failed(string code, string message) =>
        new(code, AssistantEvalOutcome.Failed, message);

    private static AssistantEvalCheck Skipped(string code, string message) =>
        new(code, AssistantEvalOutcome.Skipped, message);
}
