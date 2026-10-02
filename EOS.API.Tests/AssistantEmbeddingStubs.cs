using EOS.API.Features.Assistant.ModelAccess;

namespace EOS.API.Tests;

/// <summary>
/// 假嵌入模型：固定维度的固定向量。离线用例（工具、编码器、控制器）只关心"向量有没有被用上、
/// 维度是不是传下去了"，不关心它的数值——真实数值只有真库检索用例才需要。
/// </summary>
internal sealed class FakeEmbeddingModel(string modelId = "test@1", int dimension = 4) : IEmbeddingModel
{
    private static float[] Vector => [1, 0, 0, 0];

    public string ModelId => modelId;

    public int Dimension => dimension;

    public Task<float[]> EmbedAsync(string text, CancellationToken token) =>
        Task.FromResult(Vector);

    public Task<IReadOnlyList<float[]>> EmbedManyAsync(IReadOnlyList<string> texts, CancellationToken token) =>
        Task.FromResult<IReadOnlyList<float[]>>([.. texts.Select(_ => Vector)]);
}

/// <summary>
/// 固定解析结果的解析器替身。之所以需要它：消费方拿到的不再是"一个嵌入模型"，
/// 而是"**当前该用哪一个**"这个问题的入口（ADR-031 §3.1）——离线用例直接给出答案，
/// 不去伪造库里的"当前嵌入模型"那一行。
/// </summary>
internal sealed class StubEmbeddingResolver(IEmbeddingModel model) : IAssistantEmbeddingResolver
{
    public Task<IEmbeddingModel> ResolveAsync(CancellationToken token) => Task.FromResult(model);
}

/// <summary>没配嵌入模型时的解析器：抛与真实实现同一个异常，用来钉"fail-closed 的文案与类型"。</summary>
internal sealed class NotConfiguredEmbeddingResolver(string message) : IAssistantEmbeddingResolver
{
    public Task<IEmbeddingModel> ResolveAsync(CancellationToken token) =>
        throw new EmbeddingNotConfiguredException(message);
}

/// <summary>调用即失败的嵌入模型：用来钉"厂商侧失败"与"没配"在调用方是**两种处置**。</summary>
internal sealed class FailingEmbeddingModel(AssistantModelException error, int dimension = 4) : IEmbeddingModel
{
    public string ModelId => "test@1";

    public int Dimension => dimension;

    public Task<float[]> EmbedAsync(string text, CancellationToken token) => throw error;

    public Task<IReadOnlyList<float[]>> EmbedManyAsync(IReadOnlyList<string> texts, CancellationToken token) =>
        throw error;
}
