namespace EOS.API.Data.DocumentActions.Handlers;

/// <summary>
/// Pipeline self-check action: it touches no business table and writes nothing, so it can be configured
/// on any module to verify end to end that a button reaches the server, carries the document key and its
/// declared parameters, and comes back through the range filter / idempotency / audit envelope.
/// It doubles as the reference handler for the shape a real action takes.
/// </summary>
internal sealed class DocumentActionProbeHandler : IDocumentUserAction
{
    public const string ActionKey = "pipeline-probe";

    public string Key => ActionKey;

    public string Label => "管线探针";

    public Task<DocumentActionResult> ExecuteAsync(DocumentActionContext context, CancellationToken token)
    {
        var parameters = context.Parameters.Count == 0
            ? "无"
            : string.Join('，', context.Parameters.Select(item => $"{item.Key}={item.Value ?? "(未填)"}"));
        var mode = context.Confirm ? "执行" : "预检";
        return Task.FromResult(new DocumentActionResult(
            DocumentActionOutcome.Message,
            $"单据操作管线探针（{mode}）：模块 {context.ModuleId}，单据 {context.RecordKey}，参数 {parameters}。本次未改动任何数据。"));
    }
}
