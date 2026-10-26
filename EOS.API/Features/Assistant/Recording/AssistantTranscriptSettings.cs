namespace EOS.API.Features.Assistant.Recording;

/// <summary>
/// 进程内模型 I/O 录制开关（**部署期诊断配置**，不是业务配置）。
///
/// <para>
/// 它录的是"模型这次到底收到了什么、吐了什么"，用于把**真实回合**喂给引擎 parity
/// （见 <c>docs/guide/06</c> 的「引擎 parity」一节）。库内会话只能重建出问答正文与工具摘要，
/// 拿不到三样东西：**流式分片**（正文里混入工具调用标记时旧引擎会剥离）、
/// **多轮工具的真实轮次边界**、**系统提示词快照**。这三样只有在这里才抓得到。
/// </para>
///
/// <para>
/// 与 <c>Audit:FieldChangesEnabled</c> 同类：属运维/诊断旋钮，放 <c>appsettings</c> 是对的；
/// 助手自身的业务参数一律走库内参数目录（ADR-030 §8），不要往这里搬。
/// </para>
/// </summary>
public sealed class AssistantTranscriptSettings
{
    public const string SectionName = "AssistantTranscript";

    /// <summary>是否录制。默认关：录制会写盘，不该在无人看管时默认发生。</summary>
    public bool Enabled { get; set; }

    /// <summary>落盘目录（相对内容根）。默认落在 <c>logs/</c> 下，已被 .gitignore 排除。</summary>
    public string Directory { get; set; } = "logs/assistant-transcripts";

    /// <summary>
    /// 单进程最多录多少回合，超过即停（防"开关忘了关"把磁盘写满）。默认 100。
    /// </summary>
    public int MaxTurns { get; set; } = 100;
}
