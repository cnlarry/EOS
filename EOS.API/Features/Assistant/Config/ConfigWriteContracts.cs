using EOS.API.Models;

namespace EOS.API.Features.Assistant.Config;

/// <summary>
/// 助手可写的四个配置面。**权限授予类配置不在这个枚举里**——它是分配权力、不是维护数据，
/// 在动作面上表达不出来（见 <c>NoPrivilegeConfigCallTests</c> 的结构断言）。
/// </summary>
public enum ConfigSurface
{
    Fields,
    DataSources,
    Buttons,
    Effects,
}

/// <summary>配置面的名字：工具参数、审计动作名与幂等键共用同一份字面量，避免两处各写一遍。</summary>
public static class ConfigSurfaceNames
{
    public const string Fields = "fields";
    public const string DataSources = "datasource";
    public const string Buttons = "buttons";
    public const string Effect = "effect";

    public static string Of(ConfigSurface surface) => surface switch
    {
        ConfigSurface.Fields => Fields,
        ConfigSurface.DataSources => DataSources,
        ConfigSurface.Buttons => Buttons,
        ConfigSurface.Effects => Effect,
        _ => throw new ArgumentOutOfRangeException(nameof(surface), surface, "未知的配置面。"),
    };

    /// <summary>工具参数里的面清单（闭集）。</summary>
    public static IReadOnlyList<string> All => [Fields, DataSources, Buttons, Effect];

    /// <summary>幂等键与审计里的动作名：按面区分，重放判重不跨面。</summary>
    public static string ActionOf(ConfigSurface surface) => surface switch
    {
        ConfigSurface.Fields => "CFG_FIELD",
        ConfigSurface.DataSources => "CFG_DS",
        ConfigSurface.Buttons => "CFG_BTN",
        ConfigSurface.Effects => "CFG_EFFECT",
        _ => throw new ArgumentOutOfRangeException(nameof(surface), surface, "未知的配置面。"),
    };

    public static bool TryParse(string? value, out ConfigSurface surface)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case Fields:
                surface = ConfigSurface.Fields;
                return true;
            case DataSources:
                surface = ConfigSurface.DataSources;
                return true;
            case Buttons:
                surface = ConfigSurface.Buttons;
                return true;
            case Effect:
                surface = ConfigSurface.Effects;
                return true;
            default:
                surface = default;
                return false;
        }
    }
}

/// <summary>
/// 一次"照 A 配 B"的请求：点名源与目标，可选点名对象集合（缺省 = 该面上的全部对象）。
///
/// <para>
/// 字段与数据源面按**表**定位（FIELDS / FIELD_DATASOURCE 的键是「表.字段」），
/// 按钮与效果面按**模块**定位（MODULE_BUSINESS_ACTION 的键是模块 + 事件 + 效果键）。
/// </para>
/// </summary>
public sealed record ConfigCloneRequest(
    ConfigSurface Surface,
    string? SourceTableId = null,
    string? TargetTableId = null,
    int? SourceModuleId = null,
    int? TargetModuleId = null,
    IReadOnlyList<string>? Objects = null,
    IReadOnlyList<string>? RecordKey = null,
    string? Event = null);

/// <summary>对照卡里的一处值变更：旧值 → 新值（两者都是人话渲染后的文本）。</summary>
public sealed record ConfigValueChange(string Field, string Label, string? OldValue, string? NewValue);

/// <summary>
/// 应用一项时的服务端载荷。它由**当前规划现场产生**、不从请求体回传：
/// 前端能提交的只有"要应用哪些 id"，值本身进不了写路径。
/// </summary>
public abstract record ConfigItemPayload;

/// <summary>字段元数据面：把源字段的显示/校验属性覆盖到目标字段（类型与表达式不随之迁移）。</summary>
public sealed record FieldMetaPayload(
    string TableId, string FieldId, FieldAdminInput Target, FieldAdminInput Overlay) : ConfigItemPayload;

/// <summary>数据源面：把源字段的数据来源整组覆盖到目标字段。</summary>
public sealed record FieldDataSourcePayload(
    string TableId, string FieldId, FieldAdminInput Target, IReadOnlyList<FieldAdminChooser> Choosers) : ConfigItemPayload;

/// <summary>动作面（按钮 / 效果）一项：源动作行 + 目标模块里按事件与效果键匹配到的既有行序号（null = 新增）。</summary>
public sealed record ModuleActionPayload(BusinessActionDto Source, int? TargetSeq) : ConfigItemPayload;

/// <summary>
/// 计划里的一项（对照卡的一行）：稳定 id + 旧值/新值 + 影响面 + 能否预演。
/// <c>Previewable=false</c> 时 <c>PreviewNote</c> 必非空——不可预演必须被说出来，不得静默。
/// </summary>
public sealed record ConfigChangeItem(
    string Id,
    ConfigSurface Surface,
    string Target,
    string Label,
    IReadOnlyList<ConfigValueChange> Changes,
    IReadOnlyList<string> Impacts,
    bool Previewable,
    string? PreviewNote,
    ConfigItemPayload Payload);

/// <summary>
/// 一次配置改动的计划：来源与目标的说明 + 逐项对照 + 覆盖面声明。
/// 空计划（没有任何差异）也是合法结果——它回答的是"要不要改"，不是"改了什么"。
/// </summary>
public sealed record ConfigChangePlan(
    ConfigSurface Surface,
    string SourceLabel,
    string TargetLabel,
    string? BlockedCode,
    string? BlockedMessage,
    IReadOnlyList<ConfigChangeItem> Items,
    IReadOnlyList<string> Notes);

/// <summary>应用一项的结论：成功或带原因码的拒绝。</summary>
public sealed record ConfigApplyItemResult(
    string Id,
    string Target,
    bool Applied,
    string? Code,
    string? Message,
    string IdempotencyKey);

/// <summary>一次应用的汇总。</summary>
public sealed record ConfigApplyResult(
    ConfigSurface Surface,
    string TargetLabel,
    string? BlockedCode,
    string? BlockedMessage,
    IReadOnlyList<ConfigApplyItemResult> Items,
    IReadOnlyList<string> Notes);

/// <summary>预演/自检里的一项：能不能预演，以及不能预演的原话。</summary>
public sealed record ConfigDryRunStep(string ItemId, string Label, bool Previewable, string Note);

/// <summary>
/// 预演/自检报告。计数与实跑的报告一起给出——用户既要看"哪些改动能预演"，
/// 也要看"预演出来会发生什么"。
/// </summary>
public sealed record ConfigDryRunReport(
    IReadOnlyList<ConfigDryRunStep> Steps,
    IReadOnlyList<EffectSimulationOutcome> Simulations,
    IReadOnlyList<string> Notes,
    int PreviewableCount,
    int NotPreviewableCount);

/// <summary>一次真实预演的结果：报告原文（形状与配置面预演端点同一份）+ 一句人话摘要。</summary>
public sealed record EffectSimulationOutcome(
    string ItemId,
    string Label,
    bool Ok,
    string Summary,
    EffectSimulationReportDto? Report);

// ===== 对界面下发的线格式（工具与端点共用，界面因此不需要第二套解析）=====

/// <summary>对照卡里的一处值变更（下发形状）。</summary>
public sealed record ConfigValueChangeDraft(
    string Field,
    string Label,
    string? OldValue,
    string? NewValue);

/// <summary>对照卡里的一项。</summary>
public sealed record ConfigDiffItemDraft(
    string Id,
    string Surface,
    string Target,
    string Label,
    IReadOnlyList<ConfigValueChangeDraft> Changes,
    IReadOnlyList<string> Impacts,
    bool Previewable,
    string? PreviewNote,
    string? PreviewSummary);

/// <summary>
/// 配置改动对照卡（下发形状）：旧值/新值/影响面逐项列出，逐项可勾选。
/// <c>Request</c> 是本次计划的意图参数（源/目标/对象集合），应用时原样回传——
/// 服务端据此**重新规划**，因此回传的内容不是"要写什么"，而是"要对哪两个对象做比对"。
/// </summary>
public sealed record ConfigDiffDraft(
    string Kind,
    string Surface,
    string SourceLabel,
    string TargetLabel,
    bool Blocked,
    string? BlockedCode,
    string? BlockedMessage,
    IReadOnlyList<ConfigDiffItemDraft> Items,
    IReadOnlyList<string> Notes,
    object Request);

/// <summary>应用结果里的一项（下发形状）。</summary>
public sealed record ConfigApplyItemDraft(
    string Id,
    string Target,
    bool Applied,
    string? Code,
    string? Message);

/// <summary>应用结果（下发形状）。</summary>
public sealed record ConfigApplyDraft(
    string Kind,
    string Surface,
    string TargetLabel,
    string? BlockedCode,
    string? BlockedMessage,
    IReadOnlyList<ConfigApplyItemDraft> Items,
    IReadOnlyList<string> Notes);

/// <summary>线格式的形与 kind 标识：一处定义，模型侧与界面侧两个消费者。</summary>
public static class ConfigWriteDtos
{
    public const string DiffKind = "config-diff";
    public const string ApplyKind = "config-apply-result";

    /// <summary>计划转对照卡；预演摘要按项合并（不可预演项在卡上单独标注）。</summary>
    public static ConfigDiffDraft ToDiff(
        ConfigChangePlan plan, ConfigCloneRequest request, ConfigDryRunReport? dryRun)
    {
        var summaries = dryRun?.Simulations.ToDictionary(item => item.ItemId, item => item.Summary)
            ?? new Dictionary<string, string>(StringComparer.Ordinal);
        return new ConfigDiffDraft(
            DiffKind,
            ConfigSurfaceNames.Of(plan.Surface),
            plan.SourceLabel,
            plan.TargetLabel,
            plan.BlockedCode is not null,
            plan.BlockedCode,
            plan.BlockedMessage,
            [.. plan.Items.Select(item => new ConfigDiffItemDraft(
                item.Id,
                ConfigSurfaceNames.Of(item.Surface),
                item.Target,
                item.Label,
                [.. item.Changes.Select(change => new ConfigValueChangeDraft(
                    change.Field, change.Label, change.OldValue, change.NewValue))],
                item.Impacts,
                item.Previewable,
                item.PreviewNote,
                summaries.TryGetValue(item.Id, out var summary) ? summary : null))],
            plan.Notes,
            request);
    }

    /// <summary>应用结果转线格式。</summary>
    public static ConfigApplyDraft ToApply(ConfigApplyResult result) => new(
        ApplyKind,
        ConfigSurfaceNames.Of(result.Surface),
        result.TargetLabel,
        result.BlockedCode,
        result.BlockedMessage,
        [.. result.Items.Select(item => new ConfigApplyItemDraft(
            item.Id, item.Target, item.Applied, item.Code, item.Message))],
        result.Notes);
}
