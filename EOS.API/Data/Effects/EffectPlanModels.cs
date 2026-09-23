using System.Text.Json;

namespace EOS.API.Data.Effects;

/// <summary>Effect pipeline trigger events (v1 closed set; ENDCASE/UNENDCASE are placeholders).</summary>
public enum EffectEvent
{
    Save,
    ApproveEffect,
    Deapprove,
    Endcase,
    Unendcase,
    /// <summary>删除前校验：删除不产生"保存后行为"，但主档里的受保护行（如哨兵行）需要在删除前拦下。</summary>
    Delete,

    /// <summary>
    /// 用户主动触发：不由任何单据事件触发，只在用户点击按钮时运行。效果链不会执行这类动作
    /// （<see cref="EffectPlanLoader"/> 加载时即跳过），映射在此只为让事件码是同一份闭集。
    /// </summary>
    Manual,
}

public static class EffectEventMapper
{
    public static bool TryParse(string code, out EffectEvent value)
    {
        switch (code.Trim().ToUpperInvariant())
        {
            case "SAVE": value = EffectEvent.Save; return true;
            case "APPROVE_EFFECT": value = EffectEvent.ApproveEffect; return true;
            case "DEAPPROVE": value = EffectEvent.Deapprove; return true;
            case "ENDCASE": value = EffectEvent.Endcase; return true;
            case "UNENDCASE": value = EffectEvent.Unendcase; return true;
            case "DELETE": value = EffectEvent.Delete; return true;
            case "MANUAL": value = EffectEvent.Manual; return true;
            default: value = default; return false;
        }
    }

    /// <summary>
    /// Whether a configured action participates in the running event. DEAPPROVE mirrors
    /// the APPROVE_EFFECT chain (each action executes its reverse semantics), so approve
    /// actions are candidates for a deapprove run as well.
    /// </summary>
    public static bool AppliesTo(string eventCode, EffectEvent executionEvent) =>
        TryParse(eventCode, out var configured)
        && (configured == executionEvent
            || (executionEvent == EffectEvent.Deapprove && configured == EffectEvent.ApproveEffect));
}

/// <summary>A formula operand reference: MASTER/DETAIL/TABLE/TARGET source or a constant.</summary>
public sealed record EffectSourceRef(string Scope, string? Table, string? Field, string? Constant);

/// <summary>A closed arithmetic term (coef is +1 / -1 only; no other operators are legal).</summary>
public sealed record EffectTerm(string Field, int Coef);

/// <summary>One match key: target column located via a registered FIELD_RELATION effect edge.</summary>
public sealed record EffectMatchItem(string TargetColumn, EffectSourceRef Source);

/// <summary>One formula row: the minimal execution unit of a formula effect.</summary>
public sealed record EffectOpPlan(
    int OpSeq,
    string TargetTable,
    string TargetField,
    string OpCode,
    EffectSourceRef Source,
    string? SourceAgg,
    IReadOnlyList<EffectTerm>? Terms,
    IReadOnlyList<EffectMatchItem>? Match,
    JsonElement? Condition,
    string? Remark);

/// <summary>One configured action: effect key + execution parameters + optional formula rows.</summary>
public sealed record EffectActionPlan(
    int Seq,
    string EventCode,
    string EffectKey,
    string? EffectName,
    bool Enabled,
    string FailMode,
    JsonElement? Condition,
    JsonElement? Params,
    JsonElement? Reverse,
    IReadOnlyList<EffectOpPlan> Ops);

/// <summary>One configured validation rule (failed validation always blocks).</summary>
public sealed record EffectValidationPlan(
    int Seq,
    string Stage,
    string ValidationKey,
    bool Enabled,
    JsonElement Params,
    string? Message);

/// <summary>Fully parsed per-module effect plan used by the pipeline for one event execution.</summary>
public sealed record ModuleEffectPlan(
    int ModuleId,
    string? MasterTable,
    string? DetailTable,
    string? DefinitionVersion,
    IReadOnlyList<string> MasterPkOrder,
    IReadOnlyList<EffectActionPlan> Actions,
    IReadOnlyList<EffectValidationPlan> Rules);

/// <summary>Outcome of executing one action step inside the pipeline.</summary>
public sealed record EffectStepResult(
    int Seq,
    string EffectKey,
    bool Success,
    string? Warning,
    int RowsAffected);
