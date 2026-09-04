using System.Globalization;

namespace EOS.API.Features.Assistant.ModelAccess;

/// <summary>
/// Text embedding abstraction (RAG-选型 §3): implementations stay behind this
/// interface; index rows always record the model id, and changing models means
/// a new collection + rebuild, never mixed vector spaces.
/// </summary>
public interface IEmbeddingModel
{
    /// <summary>Model version tag recorded on every index row (e.g. "bge-m3@1").</summary>
    string ModelId { get; }

    int Dimension { get; }

    Task<float[]> EmbedAsync(string text, CancellationToken token);
}

/// <summary>
/// Placeholder until ops wires the production local model (BGE-M3 ONNX weights).
/// Fails closed with a readable code instead of silently returning junk vectors.
/// </summary>
public sealed class PendingEmbeddingModel : IEmbeddingModel
{
    public string ModelId => "unconfigured";

    public int Dimension => 1024;

    public Task<float[]> EmbedAsync(string text, CancellationToken token) =>
        throw new InvalidOperationException("向量模型未配置（KB_EMBEDDING_NOT_CONFIGURED），请先接入本地 embedding 模型。");
}

public static class EmbeddingJson
{
    public static string ToJson(IReadOnlyList<float> vector) =>
        "[" + string.Join(",", vector.Select(v => v.ToString("R", CultureInfo.InvariantCulture))) + "]";
}
