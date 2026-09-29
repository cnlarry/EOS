namespace EOS.API.Features.Assistant.Config;

/// <summary>
/// 助手配置写能力的**逐类准入**开关：字段 / 数据源 / 按钮 / 效果键各自独立启停。
///
/// <para>
/// 不做"一个开关放开全部"：四类配置的风险与验证程度不同，某一类未经验证时不该被其它类的放开带走。
/// 关闭即该类在助手侧不可写（fail-closed）：工具会明确说"这一类尚未开放"，而不是静默跳过。
/// </para>
/// </summary>
public sealed class AssistantConfigWriteOptions
{
    public const string SectionName = "AssistantConfigWrite";

    /// <summary>字段元数据（显示名 / 可见 / 只读 / 必填等）。</summary>
    public bool Fields { get; set; } = true;

    /// <summary>字段的数据来源（FIELD_DATASOURCE：源表 / 过滤结构 / 回填映射）。</summary>
    public bool DataSources { get; set; }

    /// <summary>自定义按钮（MODULE_BUSINESS_ACTION 的 MANUAL 行；不含按钮授权）。</summary>
    public bool Buttons { get; set; }

    /// <summary>效果键与公式行（MODULE_BUSINESS_ACTION(_OP) 的非 MANUAL 行）。</summary>
    public bool Effects { get; set; }

    /// <summary>该类配置在助手侧是否可写。</summary>
    public bool IsEnabled(ConfigSurface surface) => surface switch
    {
        ConfigSurface.Fields => Fields,
        ConfigSurface.DataSources => DataSources,
        ConfigSurface.Buttons => Buttons,
        ConfigSurface.Effects => Effects,
        _ => false,
    };
}
