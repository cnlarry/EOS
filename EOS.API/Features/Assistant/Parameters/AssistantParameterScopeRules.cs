using EOS.API.Data;

namespace EOS.API.Features.Assistant.Parameters;

/// <summary>作用域表（<c>dbo.ASSISTANT_PARAM_SCOPE</c>）的一行：某一层对某一条参数的覆盖。</summary>
public sealed record AssistantParameterScopeRow(
    string ScopeType,
    string ScopeKey,
    string ParamKey,
    string? Value,
    string? UpdatedBy,
    DateTimeOffset? UpdatedAt);

/// <summary>
/// 作用域的**纯规则**：分层优先级、"这条参数能不能被覆盖"、以及"收紧型只能更严"。
///
/// <para>
/// 与数据库无关，所以它既能在**保存时**用来拒绝一次越界覆盖，也能在**生效时**用来决定最终取值——
/// 两处共用同一份判断，不会出现"保存时通过、生效时被忽略"，也不会出现"保存时拒绝、库里却有效"。
/// </para>
///
/// <para>
/// 优先级固定为 <b>用户 &gt; 模块 &gt; 全局</b>：越具体的层越贴近当事人的处境。
/// 叠加时**按层级递增依次压上去**，每一层都与"它上面已经生效的值"比松紧 —— 用户层要看的是
/// 模块层（若有）而不是全局，否则"模块已收紧、用户又放宽回全局水平"这种绕开就会被漏掉。
/// </para>
/// </summary>
public static class AssistantParameterScopeRules
{
    public const string Module = "MODULE";
    public const string User = "USER";

    /// <summary>层级顺序（小 → 大）。叠加时按这个顺序依次压上去。</summary>
    public static readonly IReadOnlyList<string> Layers = [Module, User];

    /// <summary>解析作用域类型；未知一律拒绝（不猜、不回落）。</summary>
    public static bool TryParseType(string? raw, out string scopeType)
    {
        var text = raw?.Trim().ToUpperInvariant();
        if (text is Module or User)
        {
            scopeType = text;
            return true;
        }

        scopeType = string.Empty;
        return false;
    }

    /// <summary>
    /// 叠加：把作用域覆盖压到全局行上，返回**新的行集合**（交给
    /// <see cref="AssistantParameterResolver.Interpret"/> 解释，不另写一套取值逻辑）。
    ///
    /// <para>
    /// 非法覆盖不抛异常、也不静默丢弃，而是**保持上一层的值并把原因记进 <c>Problems</c>**：
    /// 库里被人手改过的越界值必须看得见，否则界面会显示一个其实没生效的值。
    /// </para>
    /// </summary>
    public static (IReadOnlyList<SystemParameterItem> Rows, IReadOnlyList<string> Problems) Layer(
        IReadOnlyList<SystemParameterItem> globalRows,
        IReadOnlyList<AssistantParameterScopeRow> scopeRows)
    {
        var problems = new List<string>();
        var effective = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in globalRows) effective[row.Key] = row.Value;

        foreach (var layer in Layers)
        {
            foreach (var scope in scopeRows)
            {
                if (!string.Equals(scope.ScopeType, layer, StringComparison.OrdinalIgnoreCase)) continue;
                if (string.IsNullOrWhiteSpace(scope.Value)) continue;

                var descriptor = AssistantParameterCatalog.Find(scope.ParamKey);
                if (descriptor is null)
                {
                    problems.Add($"作用域覆盖 {Describe(scope)} 指向的键 {scope.ParamKey} 不在参数目录里，已忽略。");
                    continue;
                }

                if (descriptor.ScopePolicy == AssistantParameterScopePolicy.None)
                {
                    problems.Add(
                        $"参数 {descriptor.Key} 不可作用域化，但 {Describe(scope)} 给它设了值（已忽略）——"
                        + "这类参数只允许全库一个值。");
                    continue;
                }

                if (!AssistantParameterCatalog.AllowsLayer(descriptor, scope.ScopeType))
                {
                    problems.Add(
                        $"参数 {descriptor.Key} 没有声明可被{LayerLabel(scope.ScopeType)}覆盖，"
                        + $"但 {Describe(scope)} 给它设了值（已忽略）——作用域层也是参数声明的一部分。");
                    continue;
                }

                if (!AssistantParameterCatalog.TryNormalize(descriptor, scope.Value, out var normalized, out var problem))
                {
                    problems.Add($"{Describe(scope)} 的 {descriptor.Key}：{problem}（已忽略）");
                    continue;
                }

                var upper = EffectiveValue(globalRows, effective, descriptor.Key);
                if (!IsTightenAllowed(descriptor, upper, normalized, out var tightenProblem))
                {
                    problems.Add($"{Describe(scope)} 的 {descriptor.Key}：{tightenProblem}（已忽略）");
                    continue;
                }

                effective[descriptor.Key] = normalized;
            }
        }

        var rows = globalRows
            .Select(row => effective.TryGetValue(row.Key, out var value) && !string.Equals(value, row.Value, StringComparison.Ordinal)
                ? row with { Value = value }
                : row)
            .ToList();
        return (rows, problems);
    }

    /// <summary>
    /// 收紧方向检查。<see cref="AssistantParameterScopePolicy.Override"/> 一律放行；
    /// <see cref="AssistantParameterScopePolicy.Tighten"/> 只允许"更严或相等"。
    /// </summary>
    public static bool IsTightenAllowed(
        AssistantParameterDescriptor descriptor, string? upperValue, string? value, out string problem)
    {
        problem = string.Empty;
        if (descriptor.ScopePolicy != AssistantParameterScopePolicy.Tighten) return true;
        if (string.IsNullOrWhiteSpace(upperValue) || string.IsNullOrWhiteSpace(value)) return true;

        switch (descriptor.ValueType)
        {
            case "bit":
                // 只能关：上层是开、下层想开也一样，想"关掉上层已关的"没有意义（等价）
                if (value == "1" && upperValue == "1") return true;
                if (value == "0") return true;
                problem = "收紧型参数只能关、不能开——上层已关闭时下层不能把它打开";
                return false;

            case "int":
            case "decimal":
                if (decimal.TryParse(value, System.Globalization.NumberStyles.Number,
                        System.Globalization.CultureInfo.InvariantCulture, out var lower)
                    && decimal.TryParse(upperValue, System.Globalization.NumberStyles.Number,
                        System.Globalization.CultureInfo.InvariantCulture, out var upper))
                {
                    if (lower <= upper) return true;
                    problem = $"收紧型参数只能取更小的值：上层为 {upper:0.####}，不能设为 {lower:0.####}";
                    return false;
                }

                problem = "收紧型参数应当是数值型——请检查参数目录的类型声明";
                return false;

            default:
                problem = "收紧型参数只支持开关与数值：文本类参数无法判断松紧，应声明为不可作用域化";
                return false;
        }
    }

    /// <summary>把一行作用域覆盖说成人话，用于 Problems 与日志。</summary>
    public static string Describe(AssistantParameterScopeRow scope) =>
        string.Equals(scope.ScopeType, User, StringComparison.OrdinalIgnoreCase)
            ? $"用户 {scope.ScopeKey}"
            : $"模块 {scope.ScopeKey}";

    /// <summary>
    /// 目录自身的作用域声明是否自洽。**离线门禁逐条断言它为空**：
    /// 声明了层就得有策略（反之亦然）、收紧型只能声明一层（"上层的上层"必须唯一确定）。
    /// </summary>
    public static IReadOnlyList<string> ValidateCatalog()
    {
        var problems = new List<string>();
        foreach (var descriptor in AssistantParameterCatalog.All)
        {
            if (descriptor.Layers == AssistantParameterScopeLayers.None)
            {
                if (descriptor.ScopePolicy != AssistantParameterScopePolicy.None)
                {
                    problems.Add($"{descriptor.Key}：声明了作用域策略 {descriptor.ScopePolicy} 却没声明层"
                        + "——策略写在目录里、层写在库里的参数，没有任何东西会去读它。");
                }

                continue;
            }

            if (descriptor.ScopePolicy == AssistantParameterScopePolicy.None)
            {
                problems.Add($"{descriptor.Key}：声明了作用域层却没声明策略（收紧 / 覆盖）");
            }

            if (descriptor.ScopePolicy == AssistantParameterScopePolicy.Tighten
                && descriptor.Layers is not (AssistantParameterScopeLayers.Module or AssistantParameterScopeLayers.User))
            {
                problems.Add($"{descriptor.Key}：收紧型只能声明一层（模块或用户），当前是 {descriptor.Layers}"
                    + "——两层都声明时\"比谁更严\"没有唯一答案，保存与生效会给出不同结果。");
            }
        }

        return problems;
    }

    private static string LayerLabel(string scopeType) =>
        string.Equals(scopeType, Module, StringComparison.OrdinalIgnoreCase) ? "模块" : "用户";

    private static string? EffectiveValue(
        IReadOnlyList<SystemParameterItem> globalRows, IReadOnlyDictionary<string, string?> effective, string key)
    {
        if (effective.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)) return value;

        // 本层之前还没有人覆盖过：拿全局限定值（取值列 → DEFAULT_VALUE）
        var row = globalRows.FirstOrDefault(item => string.Equals(item.Key, key, StringComparison.OrdinalIgnoreCase));
        return row?.EffectiveValue;
    }
}
