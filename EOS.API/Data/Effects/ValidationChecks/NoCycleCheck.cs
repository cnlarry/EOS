using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data.Effects.ValidationChecks;

/// <summary>
/// no-cycle（成环检测）：从当前单据主表行的起点值出发，按层展开"父列 → 子列"的引用关系
/// （第 N 层 = 以第 N-1 层的子项作为父项继续展开），只要某一层出现"子项 = 起点"即判为循环，
/// 回报该层父项值。逐层扫描、层内任取一行与旧过程 `P_BOM_CHECK` 一致。
/// `maxDepth`（默认 100）用于把旧过程"没有回到起点的环会无限展开、把保存挂死"的形态
/// 换成 fail-closed 拒绝；命中值经 `{ROWS}` 占位符渲染进文案。
/// </summary>
internal static class NoCycleCheck
{
    internal static async Task<string?> RunAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        ModuleEffectPlan plan,
        EffectValidationPlan rule,
        IReadOnlyList<string> masterKeyValues,
        CancellationToken token)
    {
        if (!rule.Params.TryGetProperty("checks", out var checks) || checks.ValueKind != JsonValueKind.Array)
            throw new EffectConfigException("no-cycle 缺少 checks 数组。");
        foreach (var check in checks.EnumerateArray())
        {
            if (await CheckSupport.ShouldSkipOnSwitchAsync(connection, transaction, plan, check, token))
                continue;
            var table = RequiredText(check, "table", "no-cycle");
            var parentField = RequiredText(check, "parentField", "no-cycle");
            var childField = RequiredText(check, "childField", "no-cycle");
            if (!check.TryGetProperty("start", out var start) || start.ValueKind != JsonValueKind.Object)
                throw new EffectConfigException("no-cycle.check 缺少 start。");
            var startScope = (start.TryGetProperty("scope", out var startScopeElement)
                    && startScopeElement.ValueKind == JsonValueKind.String
                        ? startScopeElement.GetString()!.Trim().ToUpperInvariant()
                        : "MASTER");
            if (startScope != "MASTER")
                throw new EffectConfigException($"no-cycle.start.scope '{startScope}' 不在闭集内（仅 MASTER）。");
            var startField = RequiredText(start, "field", "no-cycle.start");
            var maxDepth = check.TryGetProperty("maxDepth", out var depthElement)
                && depthElement.ValueKind == JsonValueKind.Number && depthElement.TryGetInt32(out var depth)
                    ? depth
                    : 100;
            if (maxDepth is < 1 or > 1000)
                throw new EffectConfigException("no-cycle.check.maxDepth 必须在 1..1000 之间。");

            // 起点取值：当前单据主表行的指定列
            var (scopeParts, parameters) = CheckSupport.BuildDocumentScopeParts(plan, masterKeyValues, "M");
            string? startValue;
            await using (var read = new SqlCommand("SELECT TOP 1 M." + EffectConditionCompiler.Identifier(startField)
                + " FROM dbo." + EffectConditionCompiler.Identifier(plan.MasterTable!) + " M WHERE "
                + string.Join(" AND ", scopeParts) + ";", connection, transaction))
            {
                foreach (var parameter in parameters)
                    read.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
                startValue = (await read.ExecuteScalarAsync(token))?.ToString()?.Trim();
            }
            if (string.IsNullOrEmpty(startValue))
                continue;

            // 逐层展开（#临时表放在嵌套作用域里，结束即释放，避免污染调用方会话）
            var cycleSql = """
                DECLARE @found NVARCHAR(100) = NULL, @step INT = 1;
                IF OBJECT_ID('tempdb..#NoCycleLevel') IS NOT NULL DROP TABLE #NoCycleLevel;
                SELECT @step AS StepNo, __PARENT__ AS ParentValue, __CHILD__ AS ChildValue
                  INTO #NoCycleLevel FROM dbo.__TABLE__ WHERE __PARENT__ = @start;
                WHILE 1 = 1
                BEGIN
                    SELECT TOP 1 @found = CONVERT(NVARCHAR(100), ParentValue) FROM #NoCycleLevel
                     WHERE ChildValue = @start AND StepNo = @step;
                    IF ISNULL(@found, N'') <> N'' BREAK;
                    SET @step = @step + 1;
                    INSERT INTO #NoCycleLevel
                    SELECT @step, b.__PARENT__, b.__CHILD__ FROM dbo.__TABLE__ b, #NoCycleLevel t
                     WHERE b.__PARENT__ = t.ChildValue AND t.StepNo = @step - 1;
                    IF @@ROWCOUNT <= 0 BREAK;
                    IF @step >= @maxDepth
                    BEGIN
                        SELECT TOP 1 @found = CONVERT(NVARCHAR(100), ParentValue) FROM #NoCycleLevel WHERE StepNo = @step;
                        BREAK;
                    END
                END
                SELECT @found;
                """
                .Replace("__TABLE__", EffectConditionCompiler.Identifier(table))
                .Replace("__PARENT__", EffectConditionCompiler.Identifier(parentField))
                .Replace("__CHILD__", EffectConditionCompiler.Identifier(childField));
            string? found;
            await using (var command = new SqlCommand(cycleSql, connection, transaction))
            {
                command.Parameters.AddWithValue("@start", startValue);
                command.Parameters.AddWithValue("@maxDepth", maxDepth);
                found = (await command.ExecuteScalarAsync(token))?.ToString()?.Trim();
            }
            if (string.IsNullOrEmpty(found)) continue;

            var message = check.TryGetProperty("message", out var messageElement)
                && messageElement.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(messageElement.GetString())
                    ? messageElement.GetString()!
                    : rule.Message ?? "{ROWS}";
            var rowSeparator = check.TryGetProperty("diagnosticRowSeparator", out var separatorElement)
                && separatorElement.ValueKind == JsonValueKind.String ? separatorElement.GetString()! : "\r\n";
            return message.Contains("{ROWS}", StringComparison.Ordinal)
                ? message.Replace("{ROWS}", string.Join(rowSeparator, found))
                : message;
        }
        return null;
    }

    /// <summary>读取校验 check 的必填字符串字段（fail-closed）。</summary>
    private static string RequiredText(JsonElement element, string name, string label)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(value.GetString())
                ? value.GetString()!.Trim()
                : throw new EffectConfigException($"{label} 缺少字符串字段 {name}。");
}
