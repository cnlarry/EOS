using System.Globalization;

namespace EOS.API.Features.Assistant.ModelAccess;

/// <summary>
/// 向量模型抽象（RAG 选型）：实现留在接口之后；集合行记录**向量空间标签**，
/// 换模型意味着重建集合，绝不把两套向量混在一个空间里。
///
/// <para>
/// 消费方拿到的是"当前该用的那个模型"（由 <see cref="IAssistantEmbeddingResolver"/> 解析），
/// 而不是某个具体厂商的客户端——所以换供应商是改数据，不是改调用点。
/// </para>
/// </summary>
public interface IEmbeddingModel
{
    /// <summary>向量空间标签，记在集合行上（如 <c>text-embedding-v4</c>）。</summary>
    string ModelId { get; }

    /// <summary>向量维度。**必须与集合登记一致**，否则入库被拒（ADR-031 §1）。</summary>
    int Dimension { get; }

    Task<float[]> EmbedAsync(string text, CancellationToken token);

    /// <summary>
    /// 批量嵌入。入库一篇文档要处理几十上百块，**分批是这一层的职责**而非调用方的：
    /// 厂商对单请求条数有上限（百炼 10 条、智谱 64 条），放进调用方就等于每个调用点各抄一遍，
    /// 抄漏的那个会在第一次真入库时撞上限流。
    ///
    /// <para>返回顺序与入参一一对应（厂商可能重排，由实现按 <c>index</c> 校正）。</para>
    /// </summary>
    Task<IReadOnlyList<float[]>> EmbedManyAsync(IReadOnlyList<string> texts, CancellationToken token);
}

/// <summary>
/// 嵌入**没有配好**（不是"服务坏了"）。单独一个类型是为了让调用方能分开处置：
///
/// <para>
/// 没配要**指路**（去 3102 配模型 / 设密钥），服务坏了要**说等待**。两者都说成一句"知识库不可用"时，
/// 用户唯一能做的就是来找我们——而这恰好是那种"用户自己就能修"的问题。
/// 继承 <see cref="InvalidOperationException"/>，历史上按它兜底的调用点（含既有测试替身）不受影响。
/// </para>
/// </summary>
public sealed class EmbeddingNotConfiguredException(string message) : InvalidOperationException(message);

public static class EmbeddingJson
{
    public static string ToJson(IReadOnlyList<float> vector) =>
        "[" + string.Join(",", vector.Select(v => v.ToString("R", CultureInfo.InvariantCulture))) + "]";
}
