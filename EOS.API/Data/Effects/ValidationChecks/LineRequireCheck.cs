using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data.Effects.ValidationChecks;

/// <summary>
/// line-require: 命中所配置触发条件的行必须满足字段断言——默认断言是"字段已填"
/// （如批管品明细必填批号），给出 assert 时改为数值比较（如盘点数不得小于零、
/// 本位币汇率必须为一）。scope=MASTER 时断言行是当前主表行，供无明细表的模块使用。
/// </summary>
internal static class LineRequireCheck
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
            throw new EffectConfigException("line-require 缺少 checks 数组。");
        foreach (var check in checks.EnumerateArray())
        {
            var field = check.TryGetProperty("field", out var fieldElement) && fieldElement.ValueKind == JsonValueKind.String
                ? fieldElement.GetString()!.Trim()
                : throw new EffectConfigException("line-require.check 缺少 field。");
            // 断言行默认是当前明细行；scope=MASTER 时改为主表行，供"无明细表"的模块（如货币资料）
            // 表达行字段约束。
            var masterScope = (check.TryGetProperty("scope", out var scopeElement)
                    && scopeElement.ValueKind == JsonValueKind.String
                        ? scopeElement.GetString()!.Trim().ToUpperInvariant()
                        : "DETAIL") switch
            {
                "DETAIL" => false,
                "MASTER" => true,
                var other => throw new EffectConfigException(
                    $"line-require.check.scope '{other}' 不在闭集内（DETAIL/MASTER）。"),
            };
            var rowTable = masterScope ? plan.MasterTable : plan.DetailTable;
            if (rowTable is null)
                throw new EffectConfigException(masterScope
                    ? "line-require 需要主表（模块形态不足）。"
                    : "line-require 需要明细表（模块形态不足）。");
            var rowAlias = masterScope ? "M" : "S";
            var documentScopeParts = CheckSupport.BuildDocumentScopeParts(plan, masterKeyValues, rowAlias);
            var parameters = documentScopeParts.Parameters;
            var documentScope = string.Join(" AND ", documentScopeParts.Parts);
            // 触发条件二选一或并用：triggers（明细列与常量的数值比较）与 condition
            // （结构化条件，走闭式条件编译器；DETAIL 域即本行别名 S，可表达"产品为批管"这类
            // 跨表存在性判据）。两者取 OR，任一成立即要求 field 已填。
            var triggerSql = new List<string>();
            if (check.TryGetProperty("triggers", out var triggers) && triggers.ValueKind == JsonValueKind.Array)
            {
                foreach (var trigger in triggers.EnumerateArray())
                {
                    var triggerColumn = trigger.TryGetProperty("field", out var triggerFieldElement) && triggerFieldElement.ValueKind == JsonValueKind.String
                        ? triggerFieldElement.GetString()!.Trim()
                        : throw new EffectConfigException("line-require.trigger 缺少 field。");
                    // 触发器作用域必须与断言行一致：两者作用在不同的关系上会把判据写成无意义的笛卡尔比较。
                    var triggerScope = trigger.TryGetProperty("scope", out var triggerScopeElement)
                        && triggerScopeElement.ValueKind == JsonValueKind.String
                            ? triggerScopeElement.GetString()!.Trim().ToUpperInvariant()
                            : "DETAIL";
                    if (triggerScope != (masterScope ? "MASTER" : "DETAIL"))
                        throw new EffectConfigException(
                            "line-require.trigger.scope 必须与同一 check 的 scope 一致。");
                    var op = trigger.TryGetProperty("op", out var opElement) && opElement.ValueKind == JsonValueKind.String
                        ? opElement.GetString()!.Trim().ToUpperInvariant()
                        : throw new EffectConfigException("line-require.trigger 缺少 op。");
                    var sqlOp = op switch
                    {
                        "GT" => ">",
                        "GE" => ">=",
                        "LT" => "<",
                        "LE" => "<=",
                        "EQ" => "=",
                        "NEQ" => "<>",
                        _ => throw new EffectConfigException($"line-require.trigger 比较符 '{op}' 不在封闭集内。"),
                    };
                    var value = trigger.TryGetProperty("value", out var valueElement) && valueElement.ValueKind == JsonValueKind.Number
                        ? valueElement.GetDouble()
                        : 0;
                    triggerSql.Add($"COALESCE({rowAlias}.{EffectConditionCompiler.Identifier(triggerColumn)}, 0) {sqlOp} {value.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
                }
            }
            if (check.TryGetProperty("condition", out var conditionElement) && conditionElement.ValueKind == JsonValueKind.Object)
            {
                var compiled = new EffectConditionCompiler().Compile(
                    conditionElement,
                    (scope, _) => scope.ToUpperInvariant() switch
                    {
                        // 条件里的作用域名必须与断言行同域：明细行只认 DETAIL/TARGET，
                        // 主表行只认 MASTER/TARGET，错域即配置失败（不静默取错别名）。
                        "DETAIL" => masterScope ? null : rowAlias,
                        "MASTER" => masterScope ? rowAlias : null,
                        "TARGET" => rowAlias,
                        _ => null,
                    },
                    _ => true,
                    outerAlias: rowAlias);
                triggerSql.Add("(" + compiled.Sql + ")");
                parameters.AddRange(compiled.Parameters);
            }
            // 断言形态二选一：默认"字段已填"；给出 assert 时改为数值比较（未命中即违规），
            // 此时触发器/条件可省略（无条件生效）。旧实现里的 `ISNULL(x,0)` 语义由 COALESCE 复刻。
            string? assertSql = null;
            if (check.TryGetProperty("assert", out var assertElement) && assertElement.ValueKind == JsonValueKind.Object)
            {
                var assertOp = assertElement.TryGetProperty("op", out var assertOpElement)
                    && assertOpElement.ValueKind == JsonValueKind.String
                        ? assertOpElement.GetString()!.Trim().ToUpperInvariant()
                        : throw new EffectConfigException("line-require.assert 缺少 op。");
                var assertSqlOp = assertOp switch
                {
                    "GT" => ">",
                    "GE" => ">=",
                    "LT" => "<",
                    "LE" => "<=",
                    "EQ" => "=",
                    "NEQ" => "<>",
                    _ => throw new EffectConfigException($"line-require.assert.op '{assertOp}' 不在封闭集内。"),
                };
                var assertCompareField = assertElement.TryGetProperty("compareField", out var compareFieldElement)
                    && compareFieldElement.ValueKind == JsonValueKind.String
                        ? compareFieldElement.GetString()!.Trim()
                        : null;
                if (!string.IsNullOrWhiteSpace(assertCompareField))
                {
                    // 与同行另一列比较：任一为空都不算违规（复刻旧实现"可空日期比较为 false"），
                    // 因此断言形态写成"空 或 关系成立"，违规谓词取其反。
                    var compareIdentifier = EffectConditionCompiler.Identifier(assertCompareField);
                    assertSql = "(" + rowAlias + "." + EffectConditionCompiler.Identifier(field) + " IS NULL"
                        + " OR " + rowAlias + "." + compareIdentifier + " IS NULL"
                        + " OR " + rowAlias + "." + EffectConditionCompiler.Identifier(field)
                        + " " + assertSqlOp + " " + rowAlias + "." + compareIdentifier + ")";
                }
                else
                {
                    if (!assertElement.TryGetProperty("value", out var assertValue) || assertValue.ValueKind != JsonValueKind.Number)
                        throw new EffectConfigException("line-require.assert 缺少数值 value 或字符串 compareField。");
                    // 默认把空值当 0（复刻旧实现的 ISNULL(x,0)）；`nullSkips=true` 改为按原值比较，
                    // 于是空值行为 UNKNOWN ⇒ 不算违规——用于旧实现写成 `col <= 0` 这类"空值放行"的判据
                    // （把空值当 0 会把它误判成违规）。
                    var assertNullSkips = assertElement.TryGetProperty("nullSkips", out var nullSkipsElement)
                        && nullSkipsElement.ValueKind == JsonValueKind.True;
                    var assertValueSql = assertNullSkips
                        ? rowAlias + "." + EffectConditionCompiler.Identifier(field)
                        : "COALESCE(" + rowAlias + "." + EffectConditionCompiler.Identifier(field) + ", 0)";
                    assertSql = assertValueSql + " " + assertSqlOp + " "
                        + assertValue.GetDouble().ToString(System.Globalization.CultureInfo.InvariantCulture);
                }
            }
            if (triggerSql.Count == 0 && assertSql is null)
                throw new EffectConfigException("line-require.check 缺少 triggers 或 condition。");
            // 违规谓词：断言形态取反（未命中即违规），默认形态为字段空值。
            var violationSql = assertSql is null
                ? $"({rowAlias}.{EffectConditionCompiler.Identifier(field)} IS NULL OR {rowAlias}.{EffectConditionCompiler.Identifier(field)} = '')"
                : "NOT (" + assertSql + ")";
            var triggerPredicate = triggerSql.Count == 0 ? string.Empty : "(" + string.Join(" OR ", triggerSql) + ") AND ";
            var sql = "SELECT TOP 1 1 FROM dbo." + EffectConditionCompiler.Identifier(rowTable)
                + " " + rowAlias + " WHERE " + documentScope
                + " AND " + triggerPredicate + violationSql;
            await using var command = new SqlCommand(sql, connection, transaction);
            foreach (var parameter in parameters)
            {
                command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
            }
            var hit = await command.ExecuteScalarAsync(token);
            if (hit is not null)
            {
                var message = rule.Message
                    ?? (check.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.String
                        ? msg.GetString()
                        : assertSql is null ? "明细行必填字段缺失。" : "明细行字段取值不符合要求。");
                // 逐行诊断：配置 diagnosticFields（明细行列名）时，把命中行按"每行一列值、行间 \r\n"
                // 追加到消息后（与既有实现的消息形态一致，最多 10 行）。
                var diagnostics = CheckSupport.ParseStringArray(check, "diagnosticFields");
                if (diagnostics.Length > 0)
                {
                    var selectList = string.Join(", ", diagnostics.Select(name => rowAlias + "." + EffectConditionCompiler.Identifier(name)));
                    var diagnosticSql = "SELECT TOP 10 " + selectList
                        + " FROM dbo." + EffectConditionCompiler.Identifier(rowTable)
                        + " " + rowAlias + " WHERE " + documentScope
                        + " AND " + triggerPredicate + violationSql
                        + " ORDER BY " + rowAlias + "." + EffectConditionCompiler.Identifier(diagnostics[0]);
                    await using var diagnosticCommand = new SqlCommand(diagnosticSql, connection, transaction);
                    foreach (var parameter in parameters)
                    {
                        diagnosticCommand.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
                    }
                    var rows = new List<string>();
                    await using var reader = await diagnosticCommand.ExecuteReaderAsync(token);
                    while (await reader.ReadAsync(token))
                    {
                        var cells = new List<string>();
                        for (var index = 0; index < diagnostics.Length; index++)
                        {
                            cells.Add(reader.IsDBNull(index) ? string.Empty : Convert.ToString(reader.GetValue(index))!.Trim());
                        }
                        rows.Add(string.Join("    ", cells));
                    }
                    if (rows.Count > 0)
                        message = message + "\r\n" + string.Join("\r\n", rows);
                }
                return message;
            }
        }
        return null;
    }
}
