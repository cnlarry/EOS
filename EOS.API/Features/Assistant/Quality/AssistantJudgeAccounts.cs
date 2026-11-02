namespace EOS.API.Features.Assistant.Quality;

/// <summary>
/// 判分的**系统账户**：判定用量的记账对象（ADR-044 §4.5 / status J1）。
///
/// <para>
/// 为什么要有它：判定是一次真实的模型调用，成本必须看得见；但它不属于任何人的对话预算——
/// 记到某个真人头上等于让"系统在后台跑了判分"吃掉那个人的日额度。所以记在
/// <c>__judge__</c> 这一行上，同时按全局口径计入总账（总额看得见），**不扣任何人的日上限**。
/// </para>
///
/// <para>
/// 这个标识同时是"谁能查到判分成本"的锚点：看板的成本数字按它聚合，不混进用户用量。
/// </para>
/// </summary>
public static class AssistantJudgeAccounts
{
    /// <summary>系统账户标识（不是真实用户，登录与权限体系里都不存在这个账号）。</summary>
    public const string SystemUserId = "__judge__";
}
