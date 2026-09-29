using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using EOS.API.Data.DocumentActions.Handlers;

namespace EOS.API.Features.Assistant.Diagnosis;

/// <summary>
/// 字段保护的用户可见原因：**文案只此一处**。
/// 引擎维护列复用 <see cref="MasterFieldWriteHandler"/> 的拒绝文案（与保存路径同一句），
/// 其余三类是只读/虚拟/成本保密位，各自一句固定说法。
/// </summary>
public static class DiagnosisFieldGuardText
{
    /// <summary>事实来源标注：引擎维护列集合。</summary>
    public const string EngineMaintainedSource = "MasterFieldWriteHandler.EngineMaintainedColumns";

    public static bool IsEngineMaintained(string field) =>
        MasterFieldWriteHandler.EngineMaintainedColumns.Contains(field);

    /// <summary>引擎维护列：与保存路径的拒绝文案完全一致。</summary>
    public static string EngineMaintained(string field) => MasterFieldWriteHandler.EngineMaintainedMessage(field);

    public static string Readonly(string label) =>
        $"字段「{label}」在字段配置里标为只读，表单不接受人工修改。";

    public static string Virtual(string label) =>
        $"字段「{label}」是虚拟列（由表达式计算），不能人工填写。";

    public static string CostDenied(string label) =>
        $"字段「{label}」是成本字段，你没有查看权限，不能修改。";

    public static string SecrecyDenied(string label) =>
        $"字段「{label}」是保密字段，你没有查看权限，不能修改。";
}

/// <summary>诊断输出的条目上限（配置节 <c>AssistantDiagnosis</c>）：代码内不写死数值，超限一律截断并记入 caveat。</summary>
public sealed class AssistantDiagnosisOptions
{
    public const string SectionName = "AssistantDiagnosis";

    /// <summary>列出的校验规则条数上限。</summary>
    public int MaxValidationRules { get; set; } = 12;

    /// <summary>列出的字段保护条数上限。</summary>
    public int MaxFieldGuards { get; set; } = 8;

    /// <summary>列出的值来源条数上限。</summary>
    public int MaxProvenance { get; set; } = 12;

    /// <summary>列出的效果影响面条数上限。</summary>
    public int MaxEffects { get; set; } = 12;

    /// <summary>列出的阻塞原因条数上限。</summary>
    public int MaxBlockers { get; set; } = 8;

    /// <summary>取"最近一次对该记录的失败"的时间窗（天）。</summary>
    public int LastFailureDays { get; set; } = 30;

    /// <summary>取最近失败审计的条数上限（只取最新一条附上）。</summary>
    public int LastFailureLimit { get; set; } = 1;

    /// <summary>单条原因/文案的字符上限。</summary>
    public int MaxTextLength { get; set; } = 160;
}

/// <summary>
/// 诊断的事实读取入口：**唯一一处为诊断写 SQL 的地方**。
/// 记录不存在或不在数据范围内一律返回 <c>null</c>（防探测口径：不区分"不存在"与"无权限"）。
/// </summary>
public interface IRecordDiagnosisReader
{
    Task<RecordDiagnosisFacts?> LoadAsync(string userId, DiagnosisContext context, CancellationToken token);
}

/// <summary>
/// "此刻办不下去的单"的有界扫描：与对象级诊断**同一套判据**（不是另写一份口径），
/// 只回答"是否过不了校验 / 状态是否不允许"，不产出证据链。
/// </summary>
public interface IBlockedRecordProbe
{
    Task<IReadOnlyList<DiagnosisBlockedRecord>> ProbeBlockedAsync(
        string userId, Models.WorkbenchDefinition definition, Security.ModulePermission permission, CancellationToken token);
}

/// <summary>输出文档的序列化口径：空段整段省略，中文不转义，便于模型与人阅读（顺序即声明顺序）。</summary>
public static class DiagnosisJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Serialize(RecordDiagnosisDocument document) =>
        JsonSerializer.Serialize(document, Options);
}
