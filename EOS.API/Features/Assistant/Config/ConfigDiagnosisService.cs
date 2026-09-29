using EOS.API.Data;
using EOS.API.Data.Effects;
using EOS.API.Data.ValidationRules;
using EOS.API.Security;

namespace EOS.API.Features.Assistant.Config;

/// <summary>解释请求：问哪个效果键 / 反向 kind / 校验模板（三选一，可并存）。</summary>
public sealed record ConfigExplanationRequest(
    string? EffectKey = null,
    string? ReverseKind = null,
    string? ValidationKey = null,
    string? ReverseNote = null);

/// <summary>一条参数说明（数据来自契约下沉的既有目录，不新建第二份）。</summary>
public sealed record ConfigExplanationParameter(
    string Name,
    string Type,
    bool Required,
    string Description,
    IReadOnlyList<string>? EnumValues,
    string? Default,
    string? Example);

/// <summary>解释结果：人话说明 + 参数说明 + 与之相关的不一致提示。</summary>
public sealed record ConfigExplanation(
    string Subject,
    IReadOnlyList<string> Lines,
    IReadOnlyList<ConfigExplanationParameter> Parameters,
    IReadOnlyList<ConfigFinding> Notes);

/// <summary>不一致检查结果：模块 + 检出项 + 覆盖面声明。</summary>
public sealed record ConfigInconsistencyReport(
    int ModuleId,
    IReadOnlyList<ConfigFinding> Findings,
    IReadOnlyList<string> Coverage);

/// <summary>
/// 配置面的两个只读切入点：**解释**（这是什么、参数什么意思、为什么没生效）与
/// **发现不一致**（配了但系统不读 / 配了但会运行时抛错）。
///
/// <para>
/// 全部复用已交付的白箱化能力：效果键与反向 kind 的名称、说明、参数 schema、反向兼容矩阵
/// 与校验模板参数白名单都来自既有注册表（契约下沉的那一批），**不新建第二套目录，也不新增第二份说明**。
/// 预演与行为说明书属 ADR-021 的既有交付，本类不重复实现。
/// </para>
/// <para>
/// 权限门与配置面同源：<c>CanBrowse(2301) && CanModuleConfig</c>，fail-closed。
/// </para>
/// </summary>
public sealed class ConfigDiagnosisService(
    IConfigDiagnosisReader reader,
    IPermissionService permissions,
    ILogger<ConfigDiagnosisService> logger)
{
    /// <summary>配置面事实来源：MODULE_BUSINESS_ACTION 与 _OP 的既有读取入口。</summary>
    public const string ConfigFactsSource = "ModuleBusinessConfigRepository";

    /// <summary>配置面只读检查的覆盖范围（如实声明：不包含需运行时才暴露的类别）。</summary>
    public static readonly IReadOnlyList<string> Coverage =
    [
        "反向说明 note 是否写在不被读取的位置",
        "反向 kind 是否落在该效果键的执行闭集内（与发布门同一口径）",
        "本模块用到的效果键里是否存在全库单例",
    ];

    /// <summary>检出本模块的配置不一致；无权（浏览或模块配置）时返回 <c>null</c>（fail-closed）。</summary>
    public async Task<ConfigInconsistencyReport?> DiagnoseAsync(
        string userId, int moduleId, CancellationToken token)
    {
        if (!await AllowedAsync(userId, moduleId, token)) return null;
        var facts = await reader.LoadAsync(moduleId, token);
        if (facts is null) return null;
        var findings = ConfigConsistencyRules.Check(facts);
        logger.LogInformation("助手配置诊断 module={ModuleId} findings={Count}", moduleId, findings.Count);
        return new ConfigInconsistencyReport(moduleId, findings, Coverage);
    }

    /// <summary>解释一个配置对象；无权时返回 <c>null</c>。</summary>
    public async Task<ConfigExplanation?> ExplainAsync(
        string userId, int moduleId, ConfigExplanationRequest request, CancellationToken token)
    {
        if (!await AllowedAsync(userId, moduleId, token)) return null;
        return Explain(request);
    }

    /// <summary>按对象组装解释（纯读既有目录）。</summary>
    public static ConfigExplanation Explain(ConfigExplanationRequest request)
    {
        var lines = new List<string>();
        var parameters = new List<ConfigExplanationParameter>();
        var notes = new List<ConfigFinding>();
        var subjects = new List<string>();

        if (!string.IsNullOrWhiteSpace(request.EffectKey))
        {
            var effectKey = request.EffectKey.Trim();
            subjects.Add($"效果键 {effectKey}");
            lines.Add($"效果键「{effectKey}」：{Label(BusinessActionLabels.EffectKeys, effectKey)}");
            lines.Add(BusinessActionLabels.EffectKeyDescriptions.TryGetValue(effectKey, out var description)
                ? description
                : "该效果键尚无说明：目录里有键，但没有一句人话说清它在单据上做什么（这是说明缺口，不是你的配置问题）。");
            lines.Add(DescribeStatus(effectKey));
            if (EffectStructSchemas.TryGetParamRootKeys(effectKey, out var rootKeys))
            {
                lines.Add($"允许的参数根键：{string.Join(" / ", rootKeys)}");
            }

            foreach (var descriptor in EffectParamDescriptors.For(effectKey))
            {
                parameters.Add(new ConfigExplanationParameter(
                    descriptor.Name, descriptor.Type, descriptor.Required, descriptor.Description,
                    descriptor.EnumValues, descriptor.Default, descriptor.Example));
            }

            if (parameters.Count == 0)
            {
                lines.Add("该效果键尚无字段级参数说明：只允许按根键填写，填错要到运行时才暴露。");
            }

            var allowed = EffectReverseCompatibility.AllowedKinds(effectKey, hasFormulaRows: false);
            lines.Add($"反向（解批）可用的 kind：{string.Join(" / ", allowed)}"
                + (EffectReverseCompatibility.ConstrainedKeys().Contains(effectKey, StringComparer.OrdinalIgnoreCase)
                    ? "（该键受处理器约束，名义闭集里的其它取值会被拒）"
                    : string.Empty)
                + "；带真公式行的动作走公式解释器，可用集合略小（不含 clear-on-deapprove）。");
        }

        if (!string.IsNullOrWhiteSpace(request.ReverseKind))
        {
            var kind = request.ReverseKind.Trim();
            subjects.Add($"反向 kind {kind}");
            lines.Add($"反向 kind「{kind}」：{Label(BusinessActionLabels.ReverseKinds, kind)}");
            lines.Add(BusinessActionLabels.ReverseKindDescriptions.TryGetValue(kind, out var description)
                ? description
                : "该 kind 尚无说明（说明缺口）。");
            lines.Add("名义闭集是全部取值，**不等于**每个效果键都接受它；某个键到底认哪几个，由反向兼容矩阵判定。");
        }

        if (!string.IsNullOrWhiteSpace(request.ValidationKey))
        {
            var key = request.ValidationKey.Trim();
            subjects.Add($"校验模板 {key}");
            lines.Add($"校验模板「{key}」：{Label(BusinessActionLabels.ValidationKeys, key)}");
            if (!ValidationRuleRegistry.IsKnownKey(key))
            {
                lines.Add("该键不在校验模板闭集内：保存时会被拒绝（配置校验先于运行）。");
            }
            else
            {
                var roots = ValidationRuleRegistry.ParamRootKeys(key);
                lines.Add(roots.Count == 0
                    ? "该模板不允许配置参数。"
                    : $"允许的参数根键：{string.Join(" / ", roots)}");
            }
        }

        if (!string.IsNullOrWhiteSpace(request.ReverseNote))
        {
            // 解释与"发现不一致"共用同一句判据文案：同一事实不在两处各说一遍。
            notes.Add(new ConfigFinding(
                ConfigConsistencyRules.ReverseNoteNotRead,
                "effect",
                request.EffectKey ?? "反向结构",
                $"反向说明里写了「{request.ReverseNote.Trim()}」：引擎只读 kind，这段说明不会被任何代码读取。",
                "EffectStructSchemas.ReverseRootKeys（kind/note）",
                "把真实意图写进受支持的 kind；说明性文字请放到备注（REMARK）。"));
        }

        return new ConfigExplanation(
            subjects.Count == 0 ? "未指定配置对象" : string.Join("；", subjects),
            lines,
            parameters,
            notes);
    }

    private static string DescribeStatus(string effectKey) =>
        EffectRegistry.Keys.TryGetValue(effectKey, out var status)
            ? status switch
            {
                EffectRegistry.Status.Formula => "执行方式：由公式行解释器执行（参数写在 MODULE_BUSINESS_ACTION_OP）。",
                EffectRegistry.Status.Service => "执行方式：由已注册的服务处理器执行（参数写在 PARAMS_STRUCT）。",
                EffectRegistry.Status.Pending => "执行方式：**尚未实现**——配了不会执行（目录里的占位）。",
                _ => "执行方式：目录里已登记，但库里没有配置实例（保留项）。",
            }
            : "该效果键不在效果键闭集内：发布期就会被拒绝。";

    private static string Label(IReadOnlyDictionary<string, string> labels, string key) =>
        labels.TryGetValue(key, out var label) ? label : "（目录里没有这个键）";

    private async Task<bool> AllowedAsync(string userId, int moduleId, CancellationToken token)
    {
        var permission = await permissions.GetAsync(userId, moduleId, token);
        // 与配置面同源：能看 + 能配，缺一不可（配置对象本身可能来自别的模块，故按 2301 判）。
        return permission is { CanBrowse: true, CanModuleConfig: true };
    }
}

/// <summary>配置诊断的事实读取入口：**唯一一处为配置诊断读库的地方**。</summary>
public interface IConfigDiagnosisReader
{
    Task<ConfigConsistencyFacts?> LoadAsync(int moduleId, CancellationToken token);
}

/// <summary>事实读取实现：复用 2301 编辑器的读取入口（模块动作链）与全库效果键分布。</summary>
public sealed class ConfigDiagnosisReader(ModuleBusinessConfigRepository config) : IConfigDiagnosisReader
{
    public async Task<ConfigConsistencyFacts?> LoadAsync(int moduleId, CancellationToken token)
    {
        var dto = await config.GetAsync(moduleId, token);
        if (dto is null) return null;
        var actions = dto.Actions
            .Select(action => new ConfigActionFact(
                action.Seq,
                action.EventCode,
                action.EffectKey,
                action.Enabled,
                action.Reverse,
                action.Ops ?? []))
            .ToList();
        var usage = await config.CountEffectKeyUsageAsync(token);
        return new ConfigConsistencyFacts(
            moduleId,
            actions,
            [.. usage.Select(pair => new ConfigEffectKeyUsage(pair.Key, pair.Value))
                .OrderBy(item => item.EffectKey, StringComparer.OrdinalIgnoreCase)]);
    }
}
