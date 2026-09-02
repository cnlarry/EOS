namespace EOS.API.Models;

/// <summary>
/// Domain rule registration for a business module (controlled equivalent of the module
/// update/after-save procedure configuration). Only registered modules may execute stored
/// procedures in the unified save pipeline, and the procedure names come from this table.
/// </summary>
public sealed record ModuleBusinessRule(
    int ModuleId,
    string? AfterSaveSproc,
    string? WorkflowSproc,
    bool AutoBillNo,
    string? BillNoField,
    string? BillTypeField,
    string? PrepayOffsetTable = null,
    string? DomainRule = null,
    bool SprocPendingPorting = false);
