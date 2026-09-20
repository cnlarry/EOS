using EOS.API.Models;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data.Effects;

/// <summary>
/// 效果参数（<c>MODULE_BUSINESS_ACTION.PARAM_STRUCT</c>）的**物理引用**校验：参数里点名的表和列
/// 是否真的存在、能否被各自的处理器解析。
///
/// 为什么单独抽出来：同一道校验有两个调用方，两侧都必须跑同一份判断——
///   · 管理端保存配置时（`ModuleBusinessConfigRepository` 的物理校验）；
///   · **发布时**（`WorkbenchDefinitionValidator` 的 `effect_params_physical`）。
/// 后者是补上的：经迁移或直写落库的配置此前没人验、发布也放过，运行期才在保存/批核那一刻炸。
///
/// 校验本身不碰数据库写路径，只读列白名单与模块主键列；每个效果键都委托给**运行期同一个解析器**，
/// 因此"能配出来"与"能跑起来"是同一个判据，不另写一套并行规则。
/// </summary>
internal static class EffectParamPhysicalGate
{
    /// <summary>
    /// 返回人类可读的问题清单；空列表表示参数引用的表与列都存在。
    /// <paramref name="actions"/> 为空时不查库，直接返回空。
    /// </summary>
    public static async Task<IReadOnlyList<string>> RunAsync(
        SqlConnection connection,
        int moduleId,
        string? masterTable,
        string? detailTable,
        IReadOnlyList<BusinessActionDto> actions,
        CancellationToken token)
    {
        if (actions.Count == 0)
        {
            return Array.Empty<string>();
        }

        var columns = await new EffectPhysicalColumns().LoadWithParametersAsync(connection, token);
        // 效果参数通过模块形态定位表，这里重建运行期计划所携带的同一份上下文。
        var masterPkOrder = masterTable is { Length: > 0 }
            ? (await WorkbenchSql.GetPrimaryKeyColumnsAsync(connection, null, masterTable, token)).ToArray()
            : Array.Empty<string>();
        var plan = new ModuleEffectPlan(
            moduleId, masterTable, detailTable, null, masterPkOrder,
            Array.Empty<EffectActionPlan>(), Array.Empty<EffectValidationPlan>());

        var issues = new List<string>();
        foreach (var action in actions)
        {
            foreach (var issue in EffectParamPhysicalValidator.Validate(action.EffectKey, action.Params, plan, columns))
            {
                issues.Add($"动作 SEQ={action.Seq} {action.EffectKey}：{issue}");
            }
        }
        return issues;
    }
}
