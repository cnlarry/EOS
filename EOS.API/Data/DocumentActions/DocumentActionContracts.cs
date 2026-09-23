using System.Text.Json;
using EOS.API.Models;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data.DocumentActions;

/// <summary>
/// Frozen contract for a document action (user action): the user triggers it on one document and
/// the server performs one action on that document. This file declares shapes only — execution
/// lives in the endpoint and the executor.
///
/// Boundary with the effect engine: the effect chain runs as a side effect of a fixed event
/// (SAVE/APPROVE/DELETE/...); a document action runs because the user asked for it now. The two
/// never share an entry point: "also do this on save" is a second configuration row, not a
/// second caller.
///
/// Code declares capability (key/label/execute); configuration decides the switch
/// (ENABLED / FAIL_MODE / CONFIRM_TAG / LABEL / CONDITION_STRUCT / PARAM_STRUCT), exactly as the
/// effect registry and MODULE_BUSINESS_ACTION already split the two.
/// </summary>
public interface IDocumentUserAction
{
    /// <summary>Action key: unique inside the closed registry and the value stored in MODULE_BUSINESS_ACTION.EFFECT_KEY for a MANUAL row.</summary>
    string Key { get; }

    /// <summary>Button text used when the configuration row carries no LABEL.</summary>
    string Label { get; }

    /// <summary>
    /// Executes the action on the document identified by the context. Runs inside the caller's
    /// transaction: throwing (or the caller rolling back) leaves no partial write. Handlers must
    /// reuse the existing write path (WorkbenchCommandHandler / CreateRecordAsync / registered
    /// service handlers) and must never build SQL against business tables themselves.
    /// </summary>
    Task<DocumentActionResult> ExecuteAsync(DocumentActionContext context, CancellationToken token);
}

/// <summary>
/// Execution context of one document action. The document state comes from the database inside the
/// caller's transaction — never from the request body, whose only trusted parts are the primary key
/// (already range-filtered) and the declared parameters.
/// </summary>
public sealed record DocumentActionContext(
    int ModuleId,
    WorkbenchDefinition Definition,
    FormDefinition Form,
    SqlConnection Connection,
    SqlTransaction Transaction,
    string RecordKey,
    IReadOnlyList<string> KeyValues,
    IReadOnlyList<string> MasterPkOrder,
    JsonElement? Params,
    string Executor,
    string ExecutorUserId,
    string? DataFilter,
    string IdempotencyKey,
    bool Confirm);

/// <summary>What the caller should do with the document after the action: reload, navigate, or just report.</summary>
public enum DocumentActionOutcome
{
    /// <summary>Document changed in place; the client reloads it.</summary>
    Refreshed,

    /// <summary>Action produced another document; the client opens it.</summary>
    Navigated,

    /// <summary>Nothing to reload; the message tells the user what happened.</summary>
    Message,
}

/// <summary>A non-blocking problem reported by an action (FAIL_MODE=WARN paths report here).</summary>
public sealed record DocumentActionWarning(string Code, string Message);

/// <summary>
/// Outcome of one action execution. When CONFIRM_TAG=1 and the request carried confirm=false the
/// executor runs the handler inside the transaction and then rolls the transaction back, so
/// Message describes what would happen and nothing is written.
/// </summary>
public sealed record DocumentActionResult(
    DocumentActionOutcome Outcome,
    string? Message = null,
    int? TargetModuleId = null,
    IReadOnlyList<string>? TargetKey = null,
    IReadOnlyList<DocumentActionWarning>? Warnings = null);

/// <summary>Request body of POST /api/v1/document-workbench/{moduleId}/action/{actionKey}.</summary>
public sealed record DocumentActionRequest(
    [property: System.Text.Json.Serialization.JsonPropertyName("key")] IReadOnlyList<string> Key,
    [property: System.Text.Json.Serialization.JsonPropertyName("params")] JsonElement? Params = null,
    [property: System.Text.Json.Serialization.JsonPropertyName("confirm")] bool Confirm = false);

/// <summary>
/// Button metadata published with the definition snapshot (definition.userActions[]). The client
/// renders buttons from this list only, so an action the user is not authorized for never appears;
/// the server re-authorizes every request regardless.
/// </summary>
public sealed record DocumentActionMetadata(
    [property: System.Text.Json.Serialization.JsonPropertyName("key")] string Key,
    [property: System.Text.Json.Serialization.JsonPropertyName("label")] string Label,
    [property: System.Text.Json.Serialization.JsonPropertyName("confirmTag")] bool ConfirmTag,
    [property: System.Text.Json.Serialization.JsonPropertyName("failMode")] string FailMode,
    [property: System.Text.Json.Serialization.JsonPropertyName("params")] JsonElement? Params = null,
    [property: System.Text.Json.Serialization.JsonPropertyName("placement")] string Placement = DocumentActionPlacements.Master);

/// <summary>Where a configured action is rendered. Detail-level actions sit in the detail grid header, master-level ones at the tail of the document toolbar.</summary>
public static class DocumentActionPlacements
{
    /// <summary>Master level: acts on the document as a whole.</summary>
    public const string Master = "master";

    /// <summary>Detail level: acts on the detail grid (recalculate lines, generate downstream document from the differences).</summary>
    public const string Detail = "detail";
}

/// <summary>
/// Error codes added by this mechanism (RFC7807 problem.code). Everything else reuses the existing
/// write-path codes: IDEMPOTENCY_KEY_REQUIRED, INVALID_RECORD_KEY, RECORD_OUT_OF_SCOPE,
/// DATA_FILTER_UNSUPPORTED, CONCURRENT_MODIFIED.
/// </summary>
public static class DocumentActionErrorCodes
{
    /// <summary>Action key not registered in code, or the module has no enabled MANUAL row for it.</summary>
    public const string NotFound = "ACTION_NOT_FOUND";

    /// <summary>The user holds no button authorization for this action (fail-closed list), or the document is outside the user's data range.</summary>
    public const string Forbidden = "ACTION_FORBIDDEN";

    /// <summary>The action ran and failed with FAIL_MODE=BLOCK; the transaction was rolled back.</summary>
    public const string Failed = "ACTION_FAILED";
}
