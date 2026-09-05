namespace EOS.API.Models;

/// <summary>执行者类型：区分用户、系统任务、Agent 代表用户和外部集成。</summary>
public enum AuditActorType : byte
{
    User = 1,
    SystemTask = 2,
    Agent = 3,
    Integration = 4,
}

/// <summary>调用方类型：区分 Web、API、Agent 和集成调用。</summary>
public enum AuditClientType : byte
{
    Web = 1,
    Api = 2,
    Agent = 3,
    Integration = 4,
}

/// <summary>字段级变更明细。</summary>
public sealed record AuditFieldChange(string FieldName, string? OldValue, string? NewValue, string? ValueHash);

/// <summary>
/// Audit settings: FieldChangesEnabled=false stops AUDIT_FIELD_CHANGE
/// and DETAIL_JSON writes, keeping summary-level AUDIT_EVENT only.
/// Enabled by default; startup log and README call out when disabled.
/// </summary>
public sealed class AuditSettings
{
    public bool FieldChangesEnabled { get; set; } = true;
}

/// <summary>工作流设置（模块 2103 流程监控超时阈值）：OverdueDays 内完成视为正常，超过则标记超时。</summary>
public sealed class WorkflowSettings
{
    public int OverdueDays { get; set; } = 3;
}
