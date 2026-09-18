namespace EOS.API.Models;

/// <summary>
/// Business-rule registration for a module (controlled equivalent of the legacy
/// update/after-save procedure configuration). Only registered modules may execute stored
/// procedures in the unified save pipeline, and the procedure names come from this table.
/// Save-time behaviour itself is carried by the validation catalog and the effect catalog;
/// there is no C# domain-rule family registry any more.
/// </summary>
public sealed record ModuleBusinessRule(
    int ModuleId,
    string? AfterSaveSproc,
    string? WorkflowSproc,
    bool AutoBillNo,
    string? BillNoField,
    string? BillTypeField,
    string? PrepayOffsetTable = null,
    bool SprocPendingPorting = false);
