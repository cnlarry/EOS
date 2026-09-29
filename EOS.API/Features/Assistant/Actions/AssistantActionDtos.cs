namespace EOS.API.Features.Assistant.Actions;

/// <summary>影响面的一行（对界面下发的形状）。</summary>
public sealed record AssistantActionImpactDraft(
    string EffectKey,
    string EventCode,
    string? EffectName,
    string TargetTable,
    string TargetField,
    string OpCode);

/// <summary>预演里的一行：**带上提交的值**，界面才能就地改；被拒时给原因码与文案。</summary>
public sealed record AssistantActionRowDraft(
    IReadOnlyList<string> Keys,
    IReadOnlyDictionary<string, string?>? Values,
    bool Allowed,
    string? DenialCode,
    string? DenialMessage,
    IReadOnlyList<AssistantActionImpactDraft>? Impacts);

/// <summary>
/// 预演报告（界面下发的形状）。模型经工具拿到它、界面经端点拿到它——**同一份形状**，
/// 界面因此不需要为"重算预演"另立一套解析。
/// </summary>
public sealed record AssistantActionPreviewDraft(
    string Kind,
    int ModuleId,
    string ModuleTitle,
    string Action,
    bool Blocked,
    string? ModuleDenialCode,
    string? ModuleDenialMessage,
    IReadOnlyList<AssistantActionRowDraft> Rows,
    IReadOnlyList<string> Notes);

/// <summary>执行结果里的一行。</summary>
public sealed record AssistantActionRowOutcomeDraft(
    IReadOnlyList<string> Keys,
    bool Succeeded,
    string? Code,
    string? Message,
    IReadOnlyList<string>? ResultKeys,
    string IdempotencyKey);

/// <summary>执行结果（界面下发的形状）。</summary>
public sealed record AssistantActionResultDraft(
    string Kind,
    int ModuleId,
    string ModuleTitle,
    string Action,
    string? ModuleDenialCode,
    string? ModuleDenialMessage,
    IReadOnlyList<AssistantActionRowOutcomeDraft> Rows);

/// <summary>
/// 动作层与界面之间的线格式。工具与控制器都从这里取形状：**一处定义，两个消费者**——
/// 否则"模型看到的预演"和"界面重算出来的预演"会各自漂移。
/// </summary>
public static class AssistantActionDtos
{
    /// <summary>预演类草稿的 kind 标识。</summary>
    public const string PreviewKind = "record-action-preview";

    /// <summary>执行结果类草稿的 kind 标识。</summary>
    public const string ResultKind = "record-action-result";

    /// <summary>预演结果转线格式；<paramref name="request"/> 提供逐行提交的值（与结果逐行对齐）。</summary>
    public static AssistantActionPreviewDraft ToPreview(AssistantActionRequest request, AssistantActionPreview preview)
        => new(
            PreviewKind,
            preview.ModuleId,
            preview.ModuleTitle,
            AssistantRecordActionNames.For(preview.Kind),
            preview.ModuleDenialCode is not null,
            preview.ModuleDenialCode,
            preview.ModuleDenialMessage,
            [.. preview.Rows.Select((row, index) => new AssistantActionRowDraft(
                row.Keys,
                index < request.Rows.Count ? request.Rows[index].Values : null,
                row.Allowed,
                row.DenialCode,
                row.DenialMessage,
                row.Impacts?.Select(ToImpact).ToList()))],
            preview.Notes);

    /// <summary>执行结果转线格式。</summary>
    public static AssistantActionResultDraft ToResult(AssistantActionExecution execution)
        => new(
            ResultKind,
            execution.ModuleId,
            execution.ModuleTitle,
            AssistantRecordActionNames.For(execution.Kind),
            execution.ModuleDenialCode,
            execution.ModuleDenialMessage,
            [.. execution.Rows.Select(row => new AssistantActionRowOutcomeDraft(
                row.Keys, row.Succeeded, row.Code, row.Message, row.ResultKeys, row.IdempotencyKey))]);

    private static AssistantActionImpactDraft ToImpact(AssistantActionImpact impact)
        => new(impact.EffectKey, impact.EventCode, impact.EffectName,
            impact.TargetTable, impact.TargetField, impact.OpCode);
}
