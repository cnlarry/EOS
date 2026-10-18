using System.Globalization;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data.Effects.ValidationChecks;

/// <summary>
/// require-when-referenced：**有条件必填 + 组合存在性**。
///
/// 语义：对每条明细行，若 <c>targetTable</c> 中存在 <c>targetKeyField = sourceField</c> 的行，
/// 则 <c>requiredField</c> 必须非空，且 <c>(targetKeyField, targetValueField) =
/// (sourceField, requiredField)</c> 必须命中一行；否则违规。目标表里没有这个来源值时整行跳过。
///
/// 为什么需要它：BOM 的子件版本对**半成品**必填（它有自己的 BOM），对**材料**必须留空
/// （它没有 BOM，填任何版本都不成立）。`line-require` 只能做无条件必填与数值比较，
/// `reference-exists` 对空值一律放行——两者都表达不了"先看目标表有没有，再决定要不要填"。
///
/// 它同时覆盖了"填了就必须存在"：非空但组合查不到，同样算违规。
/// </summary>
internal static class RequireWhenReferencedCheck
{
    /// <summary>执行断言；返回违规文案（含 <c>{ROWS}</c> 渲染），null 即全部通过。</summary>
    internal static async Task<string?> RunAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        ModuleEffectPlan plan,
        EffectValidationPlan rule,
        IReadOnlyList<string> masterKeyValues,
        CancellationToken token)
    {
        if (!rule.Params.TryGetProperty("checks", out var checks) || checks.ValueKind != JsonValueKind.Array)
            throw new EffectConfigException("require-when-referenced 缺少 checks 数组。");
        if (plan.DetailTable is null)
            throw new EffectConfigException("require-when-referenced 需要模块明细表（模块形态不足）。");

        foreach (var check in checks.EnumerateArray())
        {
            var compiled = Build(plan, check, masterKeyValues);
            await using (var command = new SqlCommand(compiled.Sql, connection, transaction))
            {
                foreach (var parameter in compiled.Parameters)
                    command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
                if (await command.ExecuteScalarAsync(token) is null)
                    continue;
            }

            var message = check.TryGetProperty("message", out var messageElement)
                && messageElement.ValueKind == JsonValueKind.String
                    ? messageElement.GetString()!
                    : rule.Message ?? "以下明细行的引用不完整。";
            if (compiled.LineSql is null)
                return message;

            var lines = new List<string>();
            await using var lineCommand = new SqlCommand(compiled.LineSql, connection, transaction);
            foreach (var parameter in compiled.Parameters)
                lineCommand.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
            await using var reader = await lineCommand.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
                lines.Add(reader.IsDBNull(0) ? string.Empty : CheckSupport.FormatCell(reader, 0));
            return message.Replace("{ROWS}", string.Join("\r\n", lines));
        }
        return null;
    }

    private static ReferenceExistsCheck.ReferenceExistsSql Build(
        ModuleEffectPlan plan,
        JsonElement check,
        IReadOnlyList<string> masterKeyValues)
    {
        var sourceField = CheckSupport.RequiredString(check, "sourceField", "require-when-referenced 缺少 sourceField。");
        var requiredField = CheckSupport.RequiredString(check, "requiredField", "require-when-referenced 缺少 requiredField。");
        var targetTable = CheckSupport.RequiredString(check, "targetTable", "require-when-referenced 缺少 targetTable。");
        var keyField = CheckSupport.RequiredString(check, "targetKeyField", "require-when-referenced 缺少 targetKeyField。");
        var valueField = CheckSupport.RequiredString(check, "targetValueField", "require-when-referenced 缺少 targetValueField。");

        // 作用域：主表按主键参数、明细按同一单据关联——他单的历史坏引用不该拦下本次保存
        var (documentScope, scopeParameters) = CheckSupport.BuildDocumentScopeParts(plan, masterKeyValues, "M");
        var scopeParts = new List<string>(documentScope);
        scopeParts.AddRange(CheckSupport.BuildDocumentScopeParts(plan, masterKeyValues, "D").Parts);

        var target = "dbo." + EffectConditionCompiler.Identifier(targetTable);
        var sourceId = "D." + EffectConditionCompiler.Identifier(sourceField);
        var requiredId = "D." + EffectConditionCompiler.Identifier(requiredField);
        var keyId = EffectConditionCompiler.Identifier(keyField);
        var valueId = EffectConditionCompiler.Identifier(valueField);

        // ① 目标表里有这个来源值（否则整行不参与）
        var hasTarget = "EXISTS (SELECT 1 FROM " + target + " T"
            + " WHERE LTRIM(RTRIM(T." + keyId + ")) = LTRIM(RTRIM(" + sourceId + ")))";
        // ② 必填字段为空（trim 后）
        var blank = "(" + requiredId + " IS NULL OR LTRIM(RTRIM(" + requiredId + ")) = '')";
        // ③ 非空但组合查不到（"填了就必须存在"）
        var missingMatch = "NOT EXISTS (SELECT 1 FROM " + target + " T2"
            + " WHERE LTRIM(RTRIM(T2." + keyId + ")) = LTRIM(RTRIM(" + sourceId + "))"
            + " AND LTRIM(RTRIM(T2." + valueId + ")) = LTRIM(RTRIM(" + requiredId + ")))";

        var predicate = string.Join(" AND ",
            scopeParts.Append(hasTarget).Append("(" + blank + " OR " + missingMatch + ")"));

        var from = "dbo." + EffectConditionCompiler.Identifier(plan.MasterTable!)
            + " M CROSS JOIN dbo." + EffectConditionCompiler.Identifier(plan.DetailTable!) + " D";
        var sql = "SELECT TOP 1 1 FROM " + from + " WHERE " + predicate;

        // 行号：显式配 lineField 才渲染 {ROWS}，否则只给固定文案
        var lineField = check.TryGetProperty("lineField", out var lineElement)
            && lineElement.ValueKind == JsonValueKind.String
                ? "D." + EffectConditionCompiler.Identifier(lineElement.GetString()!.Trim())
                : null;
        var lineSql = lineField is null
            ? null
            : "SELECT TOP (" + CheckSupport.MaxRowsOf(check).ToString(CultureInfo.InvariantCulture) + ") "
                + lineField + " FROM " + from + " WHERE " + predicate + " ORDER BY " + lineField;

        return new ReferenceExistsCheck.ReferenceExistsSql(sql, [.. scopeParameters], null, lineSql);
    }
}
