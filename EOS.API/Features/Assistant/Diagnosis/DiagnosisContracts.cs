using System.Text.Json.Serialization;
using EOS.API.Models;
using EOS.API.Security;

namespace EOS.API.Features.Assistant.Diagnosis;

/// <summary>
/// 证据段：**声明顺序即输出顺序**，也就是证据优先级。
/// 前段是日常真实的"办不下去"（校验 / 字段保护 / 状态与权限），
/// <c>flow</c> 只在模块确实配了流程时展开，<c>lastFailure</c> 有则附上、无则略。
/// </summary>
public static class DiagnosisSegments
{
    public const string Validation = "validation";
    public const string FieldGuard = "fieldGuard";
    public const string Blockers = "blockers";
    public const string Provenance = "provenance";
    public const string Effects = "effects";
    public const string Flow = "flow";
    public const string LastFailure = "lastFailure";

    /// <summary>按优先级排列的段名（测试据此断言输出顺序）。</summary>
    public static readonly IReadOnlyList<string> Ordered =
        [Validation, FieldGuard, Blockers, Provenance, Effects, Flow, LastFailure];
}

/// <summary>归因类别：给出明确原因时必居其一；无足够证据时为 <see cref="Unknown"/>。</summary>
public static class DiagnosisClasses
{
    public const string Validation = "validation";
    public const string FieldGuard = "fieldGuard";
    public const string Blockers = "blockers";
    public const string Unknown = "unknown";
}

/// <summary>一次诊断的请求（主键可缺省：由页面处境推断，调用方负责补齐）。</summary>
public sealed record DiagnosisRequest(
    int ModuleId,
    IReadOnlyList<string> Keys,
    IReadOnlyList<string> AttemptedFields);

/// <summary>诊断上下文：定义与权限结果由调用方（工具）在同一轮里取一次，避免重复授权与重复取定义。</summary>
public sealed record DiagnosisContext(
    WorkbenchDefinition Definition,
    ModulePermission Permission,
    IReadOnlyList<string> Keys,
    IReadOnlyList<string> AttemptedFields);

/// <summary>诊断目标：这条诊断说的是哪张单。</summary>
public sealed record DiagnosisTarget(
    [property: JsonPropertyName("moduleId")] int ModuleId,
    [property: JsonPropertyName("moduleName")] string ModuleName,
    [property: JsonPropertyName("recordKey")] IReadOnlyList<string> RecordKey,
    [property: JsonPropertyName("recordLabel")] string RecordLabel);

/// <summary>
/// 模块登记的一条校验规则（来自已发布定义快照，即运行时事实源）。
/// <paramref name="Parameters"/> 只用于"一次点查即可定论"的只读复核，**不进输出**。
/// </summary>
public sealed record DiagnosisRule(
    string Stage,
    int Seq,
    string ValidationKey,
    bool Enabled,
    string? Message,
    string? Trigger,
    System.Text.Json.JsonElement? Parameters = null);

/// <summary>
/// 一次"判据命中"事实：**只给身份**（阶段 / 键 / 序号 / 命中行），
/// 消息与触发条件一律由服务端规则元数据解析——不让事实自带文案，避免第二处真源。
/// </summary>
public sealed record DiagnosisRuleSignal(
    string Stage,
    string ValidationKey,
    int Seq,
    string? Detail);

/// <summary>字段保护：这个字段为什么不让改。</summary>
public sealed record DiagnosisFieldFact(
    string Field,
    string Label,
    string Reason,
    string Source,
    bool BlocksWrite);

/// <summary>值来源：这个字段的值从哪里来。</summary>
public sealed record DiagnosisProvenanceFact(
    string Field,
    string Label,
    string Origin,
    string? SourceRef);

/// <summary>效果影响面：执行该动作会触发哪些效果行。</summary>
public sealed record DiagnosisEffectFact(
    string EffectKey,
    string? TargetTable,
    string? TargetField,
    string? OpCode);

/// <summary>流程事实（模块确实配了流程时才非空）。</summary>
public sealed record DiagnosisFlowFact(
    string State,
    string? CurrentStep,
    IReadOnlyList<string> PendingApprovers,
    string? StartUser);

/// <summary>最近一次对该记录的操作失败（审计摘要字段，明细原文不下发）。</summary>
public sealed record DiagnosisFailureFact(
    string OccurredAt,
    string? ErrorCode,
    string? CorrelationId,
    string Summary);

/// <summary>
/// 诊断所需的事实集合。<see cref="Missing"/> 记录"没取到 / 取不了"的证据项，
/// 让"证据不足"有据可查，而不是一句空话。
/// </summary>
public sealed record RecordDiagnosisFacts(
    IReadOnlyList<string> RecordKey,
    string RecordLabel,
    bool Confirmed,
    bool Finished,
    IReadOnlyList<DiagnosisRule> Rules,
    IReadOnlyList<DiagnosisRuleSignal> Signals,
    IReadOnlyList<DiagnosisFieldFact> Fields,
    IReadOnlyList<DiagnosisProvenanceFact> Provenance,
    IReadOnlyList<DiagnosisEffectFact> Effects,
    DiagnosisFlowFact? Flow,
    DiagnosisFailureFact? LastFailure,
    IReadOnlyList<string> Missing);

// ── 输出契约（字段顺序即证据优先级；空段整段省略） ──

public sealed record DiagnosisValidationEntry(
    [property: JsonPropertyName("ruleKey")] string RuleKey,
    [property: JsonPropertyName("stage")] string Stage,
    [property: JsonPropertyName("seq")] int Seq,
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("message")] string? Message,
    [property: JsonPropertyName("trigger")] string? Trigger,
    [property: JsonPropertyName("hit")] bool Hit,
    [property: JsonPropertyName("detail")] string? Detail);

public sealed record DiagnosisFieldGuardEntry(
    [property: JsonPropertyName("field")] string Field,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("source")] string Source);

public sealed record DiagnosisBlockerEntry(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("source")] string Source);

public sealed record DiagnosisProvenanceEntry(
    [property: JsonPropertyName("field")] string Field,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("origin")] string Origin,
    [property: JsonPropertyName("sourceRef")] string? SourceRef);

public sealed record DiagnosisEffectEntry(
    [property: JsonPropertyName("effectKey")] string EffectKey,
    [property: JsonPropertyName("targetTable")] string? TargetTable,
    [property: JsonPropertyName("targetField")] string? TargetField,
    [property: JsonPropertyName("opCode")] string? OpCode);

public sealed record DiagnosisFlowEntry(
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("currentStep")] string? CurrentStep,
    [property: JsonPropertyName("pendingApprovers")] IReadOnlyList<string> PendingApprovers);

public sealed record DiagnosisFailureEntry(
    [property: JsonPropertyName("occurredAt")] string OccurredAt,
    [property: JsonPropertyName("errorCode")] string? ErrorCode,
    [property: JsonPropertyName("correlationId")] string? CorrelationId,
    [property: JsonPropertyName("summary")] string Summary);

/// <summary>
/// 归因结论：<c>causeClass</c> 为 <c>unknown</c> 时 <c>message</c> 固定为"证据不足"，
/// 且 <c>nextStep</c> 指明还缺什么——**不给猜测性根因**。
/// </summary>
public sealed record DiagnosisVerdictEntry(
    [property: JsonPropertyName("causeClass")] string CauseClass,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("ruleKey")] string? RuleKey,
    [property: JsonPropertyName("stage")] string? Stage,
    [property: JsonPropertyName("seq")] int? Seq,
    [property: JsonPropertyName("detail")] string? Detail,
    [property: JsonPropertyName("nextStep")] string NextStep);

public sealed record DiagnosisEvidenceEntry(
    [property: JsonPropertyName("collected")] IReadOnlyList<string> Collected,
    [property: JsonPropertyName("missing")] IReadOnlyList<string> Missing);

/// <summary>
/// <c>diagnose_record</c> 的输出文档。属性声明顺序 = 附录 B 的证据优先级顺序；
/// 空段由 <see cref="JsonIgnoreCondition.WhenWritingNull"/> 整段省略，因此顺序始终可断言。
/// </summary>
public sealed record RecordDiagnosisDocument(
    [property: JsonPropertyName("target")] DiagnosisTarget Target,
    [property: JsonPropertyName("validation")] IReadOnlyList<DiagnosisValidationEntry>? Validation,
    [property: JsonPropertyName("fieldGuard")] IReadOnlyList<DiagnosisFieldGuardEntry>? FieldGuard,
    [property: JsonPropertyName("blockers")] IReadOnlyList<DiagnosisBlockerEntry>? Blockers,
    [property: JsonPropertyName("provenance")] IReadOnlyList<DiagnosisProvenanceEntry>? Provenance,
    [property: JsonPropertyName("effects")] IReadOnlyList<DiagnosisEffectEntry>? Effects,
    [property: JsonPropertyName("flow")] DiagnosisFlowEntry? Flow,
    [property: JsonPropertyName("lastFailure")] DiagnosisFailureEntry? LastFailure,
    [property: JsonPropertyName("verdict")] DiagnosisVerdictEntry Verdict,
    [property: JsonPropertyName("evidence")] DiagnosisEvidenceEntry Evidence,
    [property: JsonPropertyName("caveat")] string? Caveat);

/// <summary>诊断输出：<c>null</c> 表示记录不存在或不在数据范围内（防探测口径，不区分两者）。</summary>
public sealed record RecordDiagnosisOutcome(RecordDiagnosisDocument? Document)
{
    public static RecordDiagnosisOutcome NotFound { get; } = new((RecordDiagnosisDocument?)null);

    public static RecordDiagnosisOutcome Of(RecordDiagnosisDocument document) => new(document);
}

/// <summary>诊断的固定文案：与既有拒绝口径一致，测试锁定不得漂移。</summary>
public static class DiagnosisMessages
{
    /// <summary>"证据不足"结论的固定说明。</summary>
    public const string InsufficientEvidence = "证据不足，无法定位";

    /// <summary>证据不足时的下一步说明（列出还缺什么）。</summary>
    public const string InsufficientNextStep = "请补充上表 evidence.missing 中的证据后重试，或按单据界面提示操作。";

    public const string NotFound = "记录不存在或不在你的数据范围内。";
}
