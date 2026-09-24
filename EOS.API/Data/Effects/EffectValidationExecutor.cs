using System.Text.Json;
using EOS.API.Data.Effects.ValidationChecks;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data.Effects;

/// <summary>Validation chain rejected the event; message is user-facing.</summary>
public sealed class EffectValidationException(string message) : Exception(message);

/// <summary>
/// Runs the configured validation rule chain for one stage. Every failed validation
/// blocks (ADR: validation has no WARN mode). Implementations compile the closed
/// PARAM_STRUCT shapes of each template key into parameterized SQL; no user values are
/// ever concatenated into SQL.
///
/// 本类只做**派发 + 规则级适用性判定**：按 <c>ValidationKey</c> 把每个 check 交给对应的族
/// （见 <c>Data/Effects/ValidationChecks/</c>），再由 <see cref="CheckSupport"/> 提供各族共用的
/// 原语。族的实现与本类无反向依赖。这个文件曾经长到一千九百多行、无法有效 review，
/// 就是因为实现堆积在这里。
/// </summary>
public sealed class EffectValidationExecutor
{
    public async Task ValidateAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        ModuleEffectPlan plan,
        string stage,
        CancellationToken token,
        IReadOnlyList<string>? masterKeyValues = null)
    {
        var keyValues = masterKeyValues ?? Array.Empty<string>();
        foreach (var rule in plan.Rules)
        {
            if (!rule.Enabled || !rule.Stage.Equals(stage, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!await RuleAppliesAsync(connection, transaction, plan, rule, keyValues, token))
                continue;
            var violation = rule.ValidationKey.ToLowerInvariant() switch
            {
                "qty-not-exceed" => await QuantityNotExceedCheck.RunAsync(connection, transaction, plan, rule, stage, keyValues, token),
                "reference-exists" => await ReferenceExistsCheck.RunAsync(connection, transaction, plan, rule, keyValues, token),
                "duplicate-check" => await DuplicateCheck.RunAsync(connection, transaction, plan, rule, keyValues, token),
                "period-overlap" => await PeriodOverlapCheck.RunAsync(connection, transaction, rule, keyValues, token),
                "line-require" => await LineRequireCheck.RunAsync(connection, transaction, plan, rule, keyValues, token),
                "no-cycle" => await NoCycleCheck.RunAsync(connection, transaction, plan, rule, keyValues, token),
                "custom-validation" => await CustomValidationCheck.RunAsync(connection, transaction, plan, rule, keyValues, token),
                _ => throw new EffectConfigException($"校验键 '{rule.ValidationKey}' 尚未实现运行时执行。"),
            };
            if (violation is not null)
            {
                // 各校验返回的已是最终文案（含自身渲染的占位符替换）；rule.Message 的覆盖
                // 已在各校验内部完成，这里只做兜底，避免用原始模板盖掉渲染结果。
                throw new EffectValidationException(violation);
            }
        }
    }

    /// <summary>
    /// 规则级适用条件（params.when，可选）：条件不成立即跳过该校验。旧域规则普遍先读当前
    /// 单据的判据字段、为空则直接放行，这条键就是该语义的通用承载；条件走闭式条件编译器，
    /// 三值逻辑与 SQL 一致（NULL 比较为 UNKNOWN，即视为不适用）。
    /// </summary>
    private async Task<bool> RuleAppliesAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        ModuleEffectPlan plan,
        EffectValidationPlan rule,
        IReadOnlyList<string> masterKeyValues,
        CancellationToken token)
    {
        if (!rule.Params.TryGetProperty("when", out var when) || when.ValueKind != JsonValueKind.Object)
            return true;

        var compiled = new EffectConditionCompiler().Compile(
            when,
            (scope, _) => scope.ToUpperInvariant() switch
            {
                "MASTER" => "M",
                // 纯主表模块的明细表可能是空串（而非 null），按"无明细表"处理。
                "DETAIL" => string.IsNullOrWhiteSpace(plan.DetailTable)
                    ? throw new EffectConfigException("校验 when 来源域 DETAIL 不可用（模块无明细表）。")
                    : "D",
                _ => null,
            },
            _ => true,
            outerAlias: "M");
        var (documentScope, parameters) = CheckSupport.BuildDocumentScopeParts(plan, masterKeyValues, "M");
        var from = "dbo." + EffectConditionCompiler.Identifier(plan.MasterTable!) + " M"
            + (string.IsNullOrWhiteSpace(plan.DetailTable)
                ? string.Empty
                : " CROSS JOIN dbo." + EffectConditionCompiler.Identifier(plan.DetailTable) + " D");
        var sql = "SELECT TOP 1 1 FROM " + from
            + " WHERE " + string.Join(" AND ", documentScope)
            + " AND (" + compiled.Sql + ")";
        await using var command = new SqlCommand(sql, connection, transaction);
        foreach (var parameter in parameters.Concat(compiled.Parameters))
            command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        return await command.ExecuteScalarAsync(token) is not null;
    }

}
