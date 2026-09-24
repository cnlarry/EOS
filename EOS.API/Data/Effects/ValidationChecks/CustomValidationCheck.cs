using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data.Effects.ValidationChecks;

/// <summary>
/// custom-validation（定制校验）：把表达式级跨表判据交给代码注册的实现（闭集，未注册即配置错）。
/// 实现返回违规文案即命中，返回 null 即通过；文案由实现自身渲染。
/// </summary>
internal static class CustomValidationCheck
{
    internal static async Task<string?> RunAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        ModuleEffectPlan plan,
        EffectValidationPlan rule,
        IReadOnlyList<string> masterKeyValues,
        CancellationToken token)
    {
        var handler = rule.Params.TryGetProperty("handler", out var handlerElement)
            && handlerElement.ValueKind == JsonValueKind.String
                ? handlerElement.GetString()!.Trim()
                : throw new EffectConfigException("custom-validation 缺少 handler。");
        if (!CustomValidationChecks.TryGet(handler, out var check))
            throw new EffectConfigException($"custom-validation.handler '{handler}' 未注册。");
        if (!rule.Params.TryGetProperty("check", out var checkParams) || checkParams.ValueKind != JsonValueKind.Object)
            throw new EffectConfigException($"custom-validation.handler '{handler}' 缺少 check 参数对象。");
        return await check(new CustomValidationContext(connection, transaction, plan, masterKeyValues),
            checkParams, token);
    }
}
