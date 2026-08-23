namespace EOS.API.Models;

/// <summary>执行者类型（ADR-005 §8）：区分用户、系统任务、Agent 代表用户和外部集成。</summary>
public enum AuditActorType : byte
{
    User = 1,
    SystemTask = 2,
    Agent = 3,
    Integration = 4,
}

/// <summary>调用方类型（ADR-005 §8）：区分 Web、API、Agent 和集成调用。</summary>
public enum AuditClientType : byte
{
    Web = 1,
    Api = 2,
    Agent = 3,
    Integration = 4,
}

/// <summary>字段级变更明细（ADR-005 §8 AUDIT_FIELD_CHANGE）。</summary>
public sealed record AuditFieldChange(string FieldName, string? OldValue, string? NewValue, string? ValueHash);

/// <summary>
/// 审计设置（ADR-005 §8 写放大降级）：FieldChangesEnabled=false 时停写 AUDIT_FIELD_CHANGE
/// 与 DETAIL_JSON，仅保留摘要级 AUDIT_EVENT + SYSDF。默认开启；关闭时启动日志与 README 显式提示。
/// </summary>
public sealed class AuditSettings
{
    public bool FieldChangesEnabled { get; set; } = true;
}
