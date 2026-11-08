namespace EOS.API.Features.Assistant.Parameters;

/// <summary>
/// 域 `CHAT`（对话行为）的运行时取值。
///
/// <para>
/// **属性初始值就是参数的默认值，也是唯一一份**（同 <c>Situation</c> / <c>Diagnosis</c> 的做法）：
/// 目录只引用它们、界面上的"默认值"提示也取自它们，所以不会出现"界面显示 60、代码其实是 40"。
/// </para>
///
/// <para>
/// 这些数字此前散在 <c>ChatService</c> 的私有常量里（其中"工具摘要 160 / 审计参数片段 200"
/// 连常量名都没有，是两处**内联字面量**）。搬进参数目录之后，它们才第一次有了管理入口。
/// </para>
/// </summary>
public sealed class AssistantChatLimitsOptions
{
    /// <summary>一次请求读入的历史消息条数上限（裁剪按上下文窗口另算）。</summary>
    public int MaxHistoryMessages { get; set; } = 60;

    /// <summary>留给系统提示、记忆、处境段与工具结果的余量（token）。</summary>
    public int ContextReserveTokens { get; set; } = 4_096;

    /// <summary>模型没填上下文窗口时的保守默认（token）。宁可少带历史，也不要因为算大了被拒。</summary>
    public int DefaultContextWindow { get; set; } = 16_384;

    /// <summary>单条消息长度上限（字符）。</summary>
    public int MaxContentLength { get; set; } = 8_000;

    /// <summary>工具参数 JSON 最大长度（模型输出的 arguments 防失控）。</summary>
    public int MaxToolArgumentsLength { get; set; } = 2_000;

    /// <summary>工具轮数上限。它同时决定单请求的成本预留倍率（预留 = 单次预留 × (轮数 + 1)）。</summary>
    public int MaxToolRounds { get; set; } = 4;

    /// <summary>落库 <c>TOOL_CALLS_JSON</c> 时工具结果摘要的字符上限。</summary>
    public int ToolDigestLength { get; set; } = 160;

    /// <summary>审计用参数片段的字符上限。</summary>
    public int AuditArgumentLength { get; set; } = 200;
}

/// <summary>
/// 域 `MEMORY`（记忆）的运行时取值。默认值同 <see cref="AssistantChatLimitsOptions"/> 的口径：
/// 属性初始值即默认值，只有这一份。
///
/// <para>
/// <c>SuggestThreshold</c> 与 <c>KeepThreshold</c> **必须成对**（建议阈值 &gt; 保留阈值，见
/// <see cref="IsPairConsistent"/>）：只改一个会让候选记忆要么全留、要么全丢。
/// </para>
/// </summary>
public sealed class AssistantMemoryLimitsOptions
{
    /// <summary>一次提炼最多产出几条候选。</summary>
    public int MaxCandidates { get; set; } = 5;

    /// <summary>高于此分标记为"建议记住"（仍需用户确认才生效）。</summary>
    public int SuggestThreshold { get; set; } = 80;

    /// <summary>低于此分直接丢弃，连候选都不进。</summary>
    public int KeepThreshold { get; set; } = 40;

    /// <summary>提炼时取最近多少轮对话。</summary>
    public int DistillTurns { get; set; } = 10;

    /// <summary>每人记忆条数上限（超限按 LRU 归档最久未访问的一条，而不是拒绝写入）。</summary>
    public int MaxPerUser { get; set; } = 200;

    /// <summary>记忆标题字符上限。</summary>
    public int MaxKeyLength { get; set; } = 200;

    /// <summary>记忆内容字符上限。</summary>
    public int MaxValueLength { get; set; } = 2_000;

    /// <summary>偏好（派生画像）字符上限。</summary>
    public int MaxPreferencesLength { get; set; } = 4_000;

    /// <summary>每轮注入提示词的记忆条数上限。</summary>
    public int InjectionTopK { get; set; } = 5;

    /// <summary>两个阈值的配对关系是否成立。</summary>
    public bool IsPairConsistent() => SuggestThreshold > KeepThreshold;
}

/// <summary>
/// 域 `KB`（知识库）的运行时取值：检索侧 3 项 + 入库切块 2 项 + 端点 1 项。
///
/// <para>
/// 切块参数（块长与重叠）之所以可配：块长直接决定"检索回来的片段够不够回答一个问题"，
/// 而它与知识库的文档体裁有关——这不是实现细节，是运维会想调的东西。但**重叠必须小于块长**，
/// 否则切块会原地打转（见 <see cref="IsChunkingConsistent"/>）。
/// </para>
/// </summary>
public sealed class AssistantKbLimitsOptions
{
    /// <summary>一次检索最多返回几条命中。</summary>
    public int SearchMaxHits { get; set; } = 5;

    /// <summary>命中片段截断的字符上限（进模型前的护栏）。</summary>
    public int SearchMaxContentLength { get; set; } = 300;

    /// <summary>
    /// 相关性截断幅度（百分点）：只注入与**最佳命中**的相似度相差不超过这个幅度的片段。
    ///
    /// <para>
    /// 用相对口径而不是绝对相似度下限，是因为 cosine 的分值分布逐模型逐厂商不同——写死一个绝对下限，
    /// 换嵌入模型那天会**静默失效**（表现成"某天开始答得变差"或"什么都检索不到"），不会有任何报错。
    /// 相对截断还有一条性质：**永远保留最佳命中**，所以通道不会因为阈值配错而整体失声
    /// （代价是"整批都不相关"时仍会带一条，由工具输出里那句"以下是检索结果"交给模型判断）。
    /// </para>
    ///
    /// <para>0 = 只留最佳命中（最严）；100 = 相似度差不超过整整 1.0，实践中等于不截断。</para>
    /// </summary>
    public int RelevanceMarginPct { get; set; } = 15;

    /// <summary>入库切块的块长（字符）。</summary>
    public int ChunkMaxChars { get; set; } = 700;

    /// <summary>相邻块的重叠长度（字符）。</summary>
    public int ChunkOverlapChars { get; set; } = 100;

    /// <summary>
    /// REST 检索端点（<c>POST /api/v1/assistant/kb/search</c>）的 <c>TopK</c> 上界。
    ///
    /// <para>
    /// **与 <see cref="SearchMaxHits"/> 分开是有意的**：那个管"进模型上下文几条"，这个管"界面能翻出几条"。
    /// 合成一个的话，管理员为省 token 调小工具上限，检索页会一起缩水，而界面上看不出这层关联；
    /// 反过来也一样——为了检索页能翻，就得给模型多喂几倍的片段。
    /// </para>
    /// </summary>
    public int EndpointMaxHits { get; set; } = 20;

    /// <summary>重叠必须小于块长。</summary>
    public bool IsChunkingConsistent() => ChunkOverlapChars < ChunkMaxChars;
}

/// <summary>
/// 域 `JUDGE`（判官）的运行时取值：判什么、判多少、花多少、留多久。
///
/// <para>
/// 这些数字此前一半是常量（截断长度）一半根本不存在（采样率、日次数、成本、保留期）。
/// **属性初始值就是默认值，也是唯一一份**：目录条目、迁移里的 <c>DEFAULT_VALUE</c>、界面上的
/// "默认值"提示都引用这里，不写第二遍数字。
/// </para>
///
/// <para>
/// 采样率是**确定性**的：按消息编号取模落桶，同一条回答在重复扫描里要么一直被抽中、要么一直不被抽中。
/// 用随机数会让"再跑一次看看"变成另一种结果，指标因此不可比。
/// </para>
/// </summary>
public sealed class AssistantJudgeLimitsOptions
{
    /// <summary>
    /// 总开关。默认关闭：判官是预览能力（ADR-044），供应商未连通时开着只会每轮记一次失败。
    /// 需要时在 3105 打开；关掉即完全不判定（含后台扫描与手动触发），但已落库的结论与看板数字保留。
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>普通回答的抽样比例（百分比）。金额/数量/比率类另有 <see cref="AmountSampleRatePct"/>。</summary>
    public int SampleRatePct { get; set; } = 20;

    /// <summary>
    /// 金额/数量/比率类回答的抽样比例（百分比），默认全判——口径标注与范围一致这两条
    /// 恰恰只在这类回答上才有意义（ADR-044 §4.4）。
    /// </summary>
    public int AmountSampleRatePct { get; set; } = 100;

    /// <summary>判分输入里问句的字符上限。</summary>
    public int MaxQuestionChars { get; set; } = 2_000;

    /// <summary>
    /// 判分输入里回答的字符上限。超上限时判分提示词会**明确写出被截断**，
    /// 判官因此只能记 UNSURE，而不是按片段下结论。
    /// </summary>
    public int MaxAnswerChars { get; set; } = 12_000;

    /// <summary>判分输入里每条工具结果摘要的字符上限。</summary>
    public int MaxToolDigestChars { get; set; } = 300;

    /// <summary>一次扫描最多判几条（手动触发也受它约束）。</summary>
    public int BatchSize { get; set; } = 20;

    /// <summary>每日判定调用次数上限。到顶即本轮跳过并**在看板上明示**，不静默停判。</summary>
    public int DailyMaxCalls { get; set; } = 200;

    /// <summary>每日判定成本上限（元）。判定用量记在系统账户 `__judge__`，不扣任何人的日上限。</summary>
    public decimal DailyCostCapYuan { get; set; } = 5;

    /// <summary>后台扫描间隔（分钟）。判定是离线批处理，改它不影响对话。</summary>
    public int ScanIntervalMinutes { get; set; } = 10;

    /// <summary>判定结论的保留天数（ADR-044 §8 #2：180 天，过期清理，不做长期明细堆积）。</summary>
    public int RetentionDays { get; set; } = 180;

    /// <summary>后台扫描回看的时间窗（天）：只看最近这些天的回答，避免一次把历史全捞进来。</summary>
    public int WindowDays { get; set; } = 7;

    /// <summary>
    /// 口径标注率（A 条款）的**告警下限**（百分比）：低于它就在看板上告警。
    ///
    /// <para>
    /// 它只是**告警线**，不是"改判官的门槛"：低于它要查条款是否写不清或模型是否换了，
    /// 而不是把这条线调低（那正是 ADR-044 §6 要防的"把指标优化成好看"）。
    /// </para>
    /// </summary>
    public int AlertCitationMinPct { get; set; } = 80;

    /// <summary>无法判定率的**告警上限**（百分比）：高于它说明条款写不清或在拿不准时乱判。</summary>
    public int AlertUnsureMaxPct { get; set; } = 40;

    /// <summary>
    /// 告警生效的最小样本数：窗口内判过的回答少于它就不告警。
    ///
    /// <para>
    /// 没有这条，3 条里 1 条不通过就会报"标注率 67%"，运维很快就学会无视这个告警。
    /// </para>
    /// </summary>
    public int AlertMinSamples { get; set; } = 20;
}

/// <summary>
/// 域 `TOOL_LIMIT`（工具输出上限）的运行时取值：7 个"读"工具各自能吐回多少东西。
///
/// <para>
/// 这些数字有两类读者，**缺一不可**：
/// <list type="number">
/// <item>工具本身（执行时截断）——<c>SearchRecordsTool</c> 等 7 个工具；</item>
/// <item><c>AssistantToolRegistry</c>（**生成发给模型的声明文本**）——把上限写进工具说明里。</item>
/// </list>
/// 之所以要让注册表也读它们：这些上限原先同时写在工具的 <c>Description</c> 里
/// （"返回前 5 行""每行 8 列"）。只接上执行侧而不动声明文本，会造出一类新漂移——
/// **参数改成 10 行、模型看到的说明还是 5 行**，于是模型按 5 行的预期规划，而系统给了它 10 行。
/// </para>
///
/// <para>
/// 工具里的 <c>Description</c> 因此**不再写具体数字**：数字只出现在注册表按参数生成的那一句里，
/// "说得出的上限"与"做得到的上限"从此同源。
/// </para>
/// </summary>
public sealed class AssistantToolLimitsOptions
{
    /// <summary>search_records 单次返回的行数上限。</summary>
    public int SearchMaxRows { get; set; } = 5;

    /// <summary>search_records 每行最多带几列。</summary>
    public int SearchMaxColumns { get; set; } = 8;

    /// <summary>search_records 每个字段值截断到多少字符。</summary>
    public int SearchMaxValueLength { get; set; } = 40;

    /// <summary>get_record_detail 单行最多带几列。</summary>
    public int DetailMaxColumns { get; set; } = 24;

    /// <summary>get_record_detail 每个字段值截断到多少字符。</summary>
    public int DetailMaxValueLength { get; set; } = 200;

    /// <summary>get_record_detail 明细最多带回几行。</summary>
    public int DetailMaxRows { get; set; } = 50;

    /// <summary>get_record_detail 明细每行最多带几列。</summary>
    public int DetailRowMaxColumns { get; set; } = 8;

    /// <summary>get_record_detail 明细每个字段值截断到多少字符。</summary>
    public int DetailRowMaxValueLength { get; set; } = 40;

    /// <summary>draft_record 试算时单个字段值截断到多少字符。</summary>
    public int DraftMaxValueLength { get; set; } = 500;

    /// <summary>describe_module 每张表最多列出几个字段。</summary>
    public int DescribeMaxFields { get; set; } = 80;

    /// <summary>list_modules 一次最多返回几个模块。</summary>
    public int ListModulesMax { get; set; } = 50;

    /// <summary>list_my_capabilities 一次最多返回几个模块。</summary>
    public int ListCapabilitiesMax { get; set; } = 50;

    /// <summary>get_field_relations 一次最多返回几条关系。</summary>
    public int FieldRelationsMax { get; set; } = 50;

    /// <summary>list_reports 一次最多列出几个报表。</summary>
    public int ReportListMax { get; set; } = 20;

    /// <summary>run_report 一次最多带回几行。</summary>
    public int ReportMaxRows { get; set; } = 20;

    /// <summary>run_report 每行最多带几列。</summary>
    public int ReportMaxColumns { get; set; } = 12;

    /// <summary>run_report 每个单元格截断到多少字符。</summary>
    public int ReportMaxValueLength { get; set; } = 40;

    /// <summary>get_record_history 审批历史与最近操作各最多带回几条。</summary>
    public int RecordHistoryMax { get; set; } = 20;

    /// <summary>get_record_history 最近操作的时间窗（天）。</summary>
    public int RecordActivityDays { get; set; } = 90;

    /// <summary>list_attachments 一次最多列出几个附件。</summary>
    public int AttachmentListMax { get; set; } = 20;

    /// <summary>resolve_reference 一次最多返回几个候选。</summary>
    public int ReferenceMaxRows { get; set; } = 10;

    /// <summary>search_metadata 一次最多返回几条命中。</summary>
    public int MetadataSearchMax { get; set; } = 20;

    /// <summary>模块简报每张表最多列几个字段。</summary>
    public int BriefMaxFields { get; set; } = 24;
}
