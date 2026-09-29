namespace EOS.API.Features.Assistant.Actions;

/// <summary>
/// 助手**可代理**的记录动作。只有新增 / 修改 / 删除三种：
/// 它们改的是"我自己录的数据"，而批核族是职权行使，不在助手动作面内（结构上不存在对应成员）。
/// </summary>
public enum AssistantRecordActionKind
{
    Insert,
    Update,
    Delete,
}

/// <summary>动作名（幂等键与审计文案共用同一份字面量，避免两处各写一遍）。</summary>
public static class AssistantRecordActionNames
{
    public const string Insert = "insert";
    public const string Update = "update";
    public const string Delete = "delete";

    public static string For(AssistantRecordActionKind kind) => kind switch
    {
        AssistantRecordActionKind.Insert => Insert,
        AssistantRecordActionKind.Update => Update,
        AssistantRecordActionKind.Delete => Delete,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "未知的记录动作。"),
    };

    public static bool TryParse(string? value, out AssistantRecordActionKind kind)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case Insert:
                kind = AssistantRecordActionKind.Insert;
                return true;
            case Update:
                kind = AssistantRecordActionKind.Update;
                return true;
            case Delete:
                kind = AssistantRecordActionKind.Delete;
                return true;
            default:
                kind = default;
                return false;
        }
    }
}

/// <summary>请求里的一行：新增/修改带字段值，修改/删除带主键。</summary>
public sealed record AssistantActionRow(
    IReadOnlyList<string> Keys,
    IReadOnlyDictionary<string, string?>? Values = null,
    IReadOnlyList<IReadOnlyDictionary<string, string?>>? Details = null,
    IReadOnlyList<string?>? DetailSerials = null);

/// <summary>一次动作请求：目标模块 + 动作 + 逐行数据。</summary>
public sealed record AssistantActionRequest(
    int ModuleId,
    AssistantRecordActionKind Kind,
    IReadOnlyList<AssistantActionRow> Rows);

/// <summary>影响面的一行：该模块声明的效果链会碰到的目标表 / 字段 / 算子。</summary>
public sealed record AssistantActionImpact(
    string EffectKey,
    string EventCode,
    string? EffectName,
    string TargetTable,
    string TargetField,
    string OpCode);

/// <summary>逐行的判定与预演结果。被拒时只带原因码与文案，不带任何数据。</summary>
public sealed record AssistantActionRowOutcome(
    IReadOnlyList<string> Keys,
    bool Allowed,
    string? DenialCode,
    string? DenialMessage,
    IReadOnlyList<AssistantActionImpact>? Impacts = null);

/// <summary>
/// 预演报告：模块级判定 + 逐行结论 + 影响面。
/// 模块级被拒时逐行结论为空——模块都进不去，谈不上"哪一行能做"。
/// </summary>
public sealed record AssistantActionPreview(
    int ModuleId,
    string ModuleTitle,
    AssistantRecordActionKind Kind,
    string? ModuleDenialCode,
    string? ModuleDenialMessage,
    IReadOnlyList<AssistantActionRowOutcome> Rows,
    IReadOnlyList<string> Notes);

/// <summary>执行结果：逐行的落库结论（成功携带业务主键与服务端生成的幂等键）。</summary>
public sealed record AssistantActionRowResult(
    IReadOnlyList<string> Keys,
    bool Succeeded,
    string? Code,
    string? Message,
    string IdempotencyKey,
    IReadOnlyList<string>? ResultKeys = null);

/// <summary>一次执行的汇总。</summary>
public sealed record AssistantActionExecution(
    int ModuleId,
    string ModuleTitle,
    AssistantRecordActionKind Kind,
    string? ModuleDenialCode,
    string? ModuleDenialMessage,
    IReadOnlyList<AssistantActionRowResult> Rows);
