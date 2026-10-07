using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data.Effects;

/// <summary>
/// `custom-validation` 的「批核要求有明细行」闸（建议挂 <c>APPROVE</c> 阶段）。
///
/// 存在的这类单据：**内容是算出来/生成出来的，不是录进来的**。典型是月结单
/// （`INV_PRO_MONTH_M` 单头 + `INV_PRO_MONTH_D` 快照明细，明细由「生成快照」动作写入）：
/// 单头能建、能批核，明细却要另走一步动作 ⇒ "没生成快照就关账"成了一条**静默可达**的路径。
/// 关了账却没有快照的那一期，对账会把全部余额都算成差异（实测比有快照时还多）。
///
/// 闸门语义：**明细表按本单主键一行都没有，就拒绝批核**。
/// 判据只取自模块定义（主表 / 明细表 / 主键序），**不认模块号**——同一句话对任何
/// "明细靠动作生成"的模块都成立；没有明细表的模块不适用，直接放行。
///
/// 与 <c>line-require</c> 的区别：那个管的是"某明细**字段**在触发条件下必填"，
/// 管不了"一行明细都没有"；这里管的是**行数**。
/// </summary>
internal static class DetailRequiredOnApproveGuard
{
    public const string HandlerKey = "detail-required-on-approve";

    private const string DefaultMessage = "该单据没有任何明细行，不能批核：请先生成或录入明细。";

    public static async Task<string?> CheckAsync(
        CustomValidationContext context, JsonElement root, CancellationToken token)
    {
        var plan = context.Plan;
        var detailTable = plan.DetailTable?.Trim();
        // 没有明细表的模块："没有明细"对它不是缺陷，本闸不适用。
        if (string.IsNullOrEmpty(detailTable))
            return null;
        if (plan.MasterPkOrder.Count == 0)
            throw new EffectConfigException("批核明细闸缺少主键序：模块定义没有声明主键。");
        if (context.MasterKeyValues.Count < plan.MasterPkOrder.Count)
            throw new EffectConfigException("批核明细闸缺少主键值。");

        var message = root.TryGetProperty("message", out var configured)
            && configured.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(configured.GetString())
                ? configured.GetString()!.Trim()
                : DefaultMessage;

        // 表名与主键列都来自服务端模块定义（不是调用方输入），沿用与批核/解批同一套定位写法。
        var keyWhere = WorkbenchSql.BuildKeyWhere(plan.MasterPkOrder, context.MasterKeyValues);
        await using var command = new SqlCommand(
            $"SELECT COUNT_BIG(*) FROM dbo.[{detailTable}] WHERE {keyWhere};",
            context.Connection, context.Transaction);
        WorkbenchSql.AddKeyParameters(command, plan.MasterPkOrder, context.MasterKeyValues);
        var count = Convert.ToInt64(await command.ExecuteScalarAsync(token) ?? 0L, System.Globalization.CultureInfo.InvariantCulture);
        return count == 0 ? message : null;
    }
}
