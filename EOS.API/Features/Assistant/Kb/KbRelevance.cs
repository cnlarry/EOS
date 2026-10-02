using EOS.API.Data;

namespace EOS.API.Features.Assistant.Kb;

/// <summary>
/// 检索命中的**相关性截断**（ADR-031 §4.1）：只保留与最佳命中的相似度相差不超过给定幅度的片段。
///
/// <para>
/// 为什么是相对口径：`VECTOR_DISTANCE('cosine')` 的分值分布逐模型逐厂商不同，写死一个绝对相似度下限，
/// 换嵌入模型那天会**静默失效**——表现成"某天开始答得变差"或"什么都检索不到"，而不会有任何报错。
/// </para>
///
/// <para>
/// 两条性质值得记住：① **永远保留最佳命中**（所以阈值配错也不会让知识通道整体失声，代价是"整批
/// 都不相关"时仍会带一条——由工具输出开头那句"未必都相关"交给模型判断）；② **同分同留**——
/// 相似度并列的片段一起进或一起出，不按顺序切成半截（否则同一批数据在两次检索里表现不一致）。
/// </para>
///
/// <para>纯函数：不碰库、不出网，所以边界值都能离线断言。</para>
/// </summary>
public static class KbRelevance
{
    /// <summary>
    /// 按"与最佳命中的相似度差"截断。<paramref name="marginPercent"/> 取 0–100（越界会被夹住）：
    /// 0 = 只留最佳命中（含并列），100 = 相差不超过整 1.0，实践中等于不截断。
    /// </summary>
    public static IReadOnlyList<KbHit> Cut(IReadOnlyList<KbHit> hits, int marginPercent)
    {
        if (hits.Count <= 1)
        {
            return hits;
        }

        // 相似度 = 1 - 余弦距离。取全体的最大值而不是"第一条"，于是即便上游顺序变了也不会切错
        var best = hits.Max(Similarity);
        var margin = Math.Clamp(marginPercent, 0, 100) / 100.0;
        return [.. hits.Where(hit => best - Similarity(hit) <= margin)];
    }

    /// <summary>余弦相似度（距离的反面）。距离可能因浮点误差略微越界，这里不夹——保持单调即可。</summary>
    private static double Similarity(KbHit hit) => 1 - hit.Distance;
}
