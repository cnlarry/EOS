using System.Data;
using System.Globalization;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data.Effects.ValidationChecks;

/// <summary>
/// 各校验族共用的工具箱：单据范围片段、PARAM_STRUCT 参数读取、诊断单元格渲染、check 门控。
///
/// 为什么独立成类而不是留在 <see cref="EffectValidationExecutor"/>：这些原语被**全部 7 个族**
/// 反复引用（仅已有族就有近 30 处）。留在校验器里会让族类反向依赖自己的派发器——插件依赖宿主，
/// 概念上是错的，也让人误以为它们是校验器的对外能力。现在依赖是单向的：
/// 族 → 本类，派发器 → 本类。
///
/// 刻意**不**做成接口 + 注册：没有任何"运行时替换实现"的需求，静态工具类更简单直接。
/// </summary>
internal static class CheckSupport
{
    /// <summary>
    /// Scopes a statement to the current document by master primary-key values. The alias
    /// names the relation that carries the master key columns (master table, or the detail
    /// table which repeats them).
    /// </summary>
    internal static (List<string> Parts, List<EffectSqlParameter> Parameters) BuildDocumentScopeParts(
        ModuleEffectPlan plan,
        IReadOnlyList<string> masterKeyValues,
        string alias)
    {
        if (plan.MasterPkOrder.Count == 0 || masterKeyValues.Count == 0)
        {
            throw new EffectConfigException("校验缺少单据主键上下文，禁止跨单比较。");
        }
        var keys = Math.Min(plan.MasterPkOrder.Count, masterKeyValues.Count);
        var parts = new List<string>();
        var parameters = new List<EffectSqlParameter>();
        for (var index = 0; index < keys; index++)
        {
            var name = "@mk" + index;
            parameters.Add(new EffectSqlParameter(name, masterKeyValues[index]));
            parts.Add($"{alias}.{EffectConditionCompiler.Identifier(plan.MasterPkOrder[index])} = {name}");
        }
        return (parts, parameters);
    }

    /// <summary>读 PARAM_STRUCT 的 maxRows（1..100，缺省 10）——诊断行数上限的统一定义。</summary>
    internal static int MaxRowsOf(JsonElement root) =>
        root.TryGetProperty("maxRows", out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out var declared)
            ? Math.Clamp(declared, 1, 100)
            : 10;

    /// <summary>读必填字符串字段（缺失/空白即配置错，fail-closed）。</summary>
    internal static string RequiredString(JsonElement element, string name, string error)
    {
        return element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(value.GetString())
                ? value.GetString()!.Trim()
                : throw new EffectConfigException(error);
    }

    /// <summary>
    /// 诊断列的显示文本：日期按 yyyy-MM-dd 输出（避免把 ASP.NET 文化格式带进用户文案），
    /// 其余类型按不变文化转写并去掉首尾空白。
    /// </summary>
    internal static string FormatCell(SqlDataReader reader, int index)
    {
        var value = reader.GetValue(index);
        return value switch
        {
            DateTime date => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            string text => text.Trim(),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim() ?? string.Empty,
        };
    }

    /// <summary>读可选字符串数组（非数组或缺席返回空数组）。</summary>
    internal static string[] ParseStringArray(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();
        return array.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString()!.Trim())
            .ToArray();
    }

    /// <summary>
    /// Optional per-check gate (closed operator, same semantics as the condition compiler
    /// switch): the check only applies while every declared gate matches. Two shapes are
    /// accepted — the baseline single gate <c>{"key":"&lt;SYSSS column&gt;","expect":1}</c> and
    /// the list form <c>{"gates":[{"scope":"SYSSS|MODULE","key":"...","expect":1}, …]}</c>
    /// (all gates must hold). The MODULE scope reads the current module's own column
    /// (e.g. ERROR_NO_SAVE), which is how the baseline per-module validation switch is
    /// carried over without making the switch inert: flag on = rule applies, flag off =
    /// rule skipped, exactly as the retired C# branch behaved. Unknown scopes and unknown
    /// columns fail closed.
    /// </summary>
    internal static async Task<bool> ShouldSkipOnSwitchAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        ModuleEffectPlan plan,
        JsonElement check,
        CancellationToken token)
    {
        if (!check.TryGetProperty("switch", out var gate) || gate.ValueKind != JsonValueKind.Object)
            return false;
        foreach (var entry in EnumerateGates(gate))
        {
            if (await GateFailsAsync(connection, transaction, plan, entry, token))
                return true;
        }
        return false;
    }

    private static IEnumerable<JsonElement> EnumerateGates(JsonElement gate)
    {
        if (gate.TryGetProperty("key", out _))
            yield return gate;
        if (gate.TryGetProperty("gates", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in list.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    throw new EffectConfigException("校验 switch.gates 项必须是对象。");
                yield return item;
            }
        }
    }

    private static async Task<bool> GateFailsAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        ModuleEffectPlan plan,
        JsonElement entry,
        CancellationToken token)
    {
        var key = entry.TryGetProperty("key", out var keyElement) && keyElement.ValueKind == JsonValueKind.String
            ? keyElement.GetString()!.Trim()
            : throw new EffectConfigException("校验 switch 门控缺少 key。");
        var expect = entry.TryGetProperty("expect", out var expectElement) && expectElement.ValueKind == JsonValueKind.Number
            ? expectElement.GetInt32()
            : 1;
        var scope = entry.TryGetProperty("scope", out var scopeElement) && scopeElement.ValueKind == JsonValueKind.String
            ? scopeElement.GetString()!.Trim().ToUpperInvariant()
            : "SYSSS";
        var column = EffectConditionCompiler.Identifier(key);
        string sql;
        SqlCommand command;
        if (scope == "SYSSS")
        {
            // System switches are looked up by parameter key against the parameter table
            // (owner 110111). The key travels as a parameter; an unregistered or empty
            // key reads as 0, which keeps the gate closed rather than failing open.
            sql = $"SELECT {SystemParameterService.BoolSwitchSql(SystemParameterService.SystemOwner, "@Key")}";
            command = new SqlCommand(sql, connection, transaction);
            command.Parameters.Add("@Key", SqlDbType.NVarChar, 64).Value = key;
        }
        else if (scope == "MODULE")
        {
            sql = $"SELECT COALESCE(MAX(CAST({column} AS int)), 0) FROM dbo.MODULES WITH (NOLOCK) WHERE M_IDX = @ModuleId";
            command = new SqlCommand(sql, connection, transaction);
            command.Parameters.Add("@ModuleId", SqlDbType.Int).Value = plan.ModuleId;
        }
        else
        {
            throw new EffectConfigException($"校验 switch 门控来源域 '{scope}' 不支持（仅 SYSSS / MODULE）。");
        }
        await using (command)
        {
            var actual = await command.ExecuteScalarAsync(token);
            return Convert.ToInt32(actual) != expect;
        }
    }
}
