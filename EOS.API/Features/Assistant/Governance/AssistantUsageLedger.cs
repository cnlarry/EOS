using EOS.API.Data;
using EOS.API.Features.Assistant.ModelAccess;
using EOS.API.Features.Assistant.Parameters;

namespace EOS.API.Features.Assistant.Governance;

/// <summary>
/// 一次回合的**用量账**：当事人日上限解析、预留与结算。
///
/// <para>
/// 它单独成类只为一件事：**准备段与循环段都要动同一笔账**——准备段按上限预留，
/// 循环段在取消 / 失败 / 成功三条路径上分别结算。换引擎时这两段会落到不同实现里，
/// 账如果没有单一入口，"预算不可绕过"就会因为搬家而悄悄松动（预留泄漏会锁死当日额度）。
/// </para>
///
/// <para>
/// 没有用量仓储（单测里直接构造）时全部降级为空操作：预留不成立，结算也就无需落库。
/// </para>
/// </summary>
public sealed class AssistantUsageLedger(
    IAssistantUsageRepository? usageRepository,
    IAssistantEffectiveParameters? effectiveParameters,
    ILogger logger)
{
    /// <summary>
    /// 原子预留：同一事务内建行 + 按上限条件扣减（用户行与全局行同时满足），并发请求在此串行化。
    ///
    /// <para>
    /// 没有用量仓储时视为放行（单测口径：没有账本就没有限额）。真实花费按轮数上限放大，
    /// 见调用方传入的 <paramref name="reserveMicro"/>。
    /// </para>
    /// </summary>
    public async Task<bool> TryReserveAsync(
        string userId,
        DateTimeOffset day,
        long reserveMicro,
        double userCapYuan,
        AssistantSettings settings,
        CancellationToken token)
    {
        if (usageRepository is null)
        {
            return true;
        }

        return await usageRepository.TryReserveAsync(
            userId, day, reserveMicro,
            ToMicroYuan(userCapYuan),
            ToMicroYuan(settings.Cost.GlobalDailyCapYuan),
            token);
    }

    /// <summary>
    /// 结算一次预留。<paramref name="actualMicro"/> 是真实花费（微元），
    /// 失败路径按预留额计入，取消路径按 0 释放。
    /// </summary>
    public async Task SettleAsync(
        string userId, DateTimeOffset day, long reserveMicro, long actualMicro, bool completed)
    {
        if (usageRepository is null || reserveMicro <= 0)
        {
            return;
        }

        try
        {
            // 独立 token：即使请求已取消，结算也必须落库（否则预留泄漏锁死当日额度）。
            await usageRepository.SettleAsync(
                userId, day, reserveMicro, actualMicro, completed, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "助手用量结算失败（已忽略）");
        }
    }

    /// <summary>
    /// 本轮的**用户日上限**：快照里是全库统一值，作用域表可能给某个人单独放宽过（ADR-030 §6.2）。
    ///
    /// <para>
    /// 没注入生效参数服务时（单测里直接构造）退回快照值——作用域是"在这基础上再加一层"，
    /// 不是"没有它就取不到值"。
    /// </para>
    /// </summary>
    public async Task<double> UserDailyCapYuanAsync(
        string userId, int? moduleId, AssistantSettings settings, CancellationToken token) =>
        effectiveParameters is null
            ? settings.Cost.UserDailyCapYuan
            : (await effectiveParameters.ForAsync(userId, moduleId, token)).Cost.UserDailyCapYuan;

    /// <summary>元 → 微元。用整数微元记账，避免浮点在累计与比较里漂移。</summary>
    public static long ToMicroYuan(double yuan) =>
        (long)Math.Ceiling(yuan * (double)AssistantCost.MicroYuanPerYuan);
}
