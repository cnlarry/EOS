namespace EOS.API.Features.Assistant.Parameters;

/// <summary>
/// 一段**行为规则**：注入系统提示词的条款文本，连同它依赖的工具。
///
/// <para>
/// 为什么是数据而不是代码常量：规则措辞要能被客户侧责任人在 3105 里调整（ADR-046 的"自助改进"），
/// 而调整必须留痕、可回滚。段落随工具可用性派生（工具没发就不注入），因此"依赖工具"也是数据的一部分。
/// </para>
///
/// <para>
/// <see cref="IsLocked"/> 表示**红线条款**：批核不可代签、金额不得估算、过滤条件不得省略等。
/// 锁定行的文本只能通过迁移改（要过代码评审），写接口会直接拒绝——自动/界面改行为不能碰它们。
/// </para>
/// </summary>
public sealed record PromptSegment(
    string Key,
    string Title,
    string Text,
    IReadOnlyList<string> RequiredTools,
    bool IsLocked,
    int SortIdx);

/// <summary>
/// 规则段的**代码默认值**（迁移 463 的种子与它逐字一致）。
///
/// <para>
/// 两个用途：① 迁移种子来源；② 库内一张行都没有时的兜底（单测、全新库首启）。
/// 生产环境里"改规则"改的是库，不是这里——真库用例会断言库内文本与本地默认一致，
/// 避免两边漂移后没人发现。
/// </para>
/// </summary>
public static class PromptSegmentDefaults
{
    public static IReadOnlyList<PromptSegment> All { get; } =
    [
        new("METRIC_USAGE", "指标口径取数", MetricUsage, ["resolve_metric"], IsLocked: true, SortIdx: 10),
        new("DIAGNOSIS_USAGE", "诊断取证", DiagnosisUsage, ["diagnose_record"], IsLocked: false, SortIdx: 20),
        new("KNOWLEDGE_CHANNEL", "机制与知识通道", KnowledgeChannel, ["describe_mechanism", "kb_search"], IsLocked: false, SortIdx: 30),
        new("METADATA_SEARCH", "元数据检索", MetadataSearch, ["search_metadata"], IsLocked: false, SortIdx: 40),
        new("NAVIGATION", "导航与取数顺序", Navigation,
            ["list_my_capabilities", "describe_module", "get_form_schema", "search_records", "get_record_detail"],
            IsLocked: false, SortIdx: 50),
        new("REFERENCE", "引用字段解析", Reference, ["resolve_reference"], IsLocked: false, SortIdx: 60),
        new("RECORD_WRITE", "记录写入与操作卡", RecordWrite,
            ["preview_record_action", "apply_record_action"], IsLocked: true, SortIdx: 70),
        new("DOCUMENT_ACTION", "自定义单据动作", DocumentAction,
            ["list_document_actions", "preview_document_action", "apply_document_action"], IsLocked: true, SortIdx: 80),
    ];

    // 以下文本即迁移 463 的种子。锁定段（指标口径 / 记录写入 / 自定义动作）承载红线：
    // 金额不得自行估算、删除与批量不代劳、confirm 型动作不得直接执行。
    private const string MetricUsage =
        "回答涉及金额、数量、比率等量化指标时，必须用 resolve_metric 按系统口径取数"
        + "（metric_id 可直接写业务词，如「营业额」；命中多条时按返回的候选改用口径标识），"
        + "并在回答中标注所用口径名与数值；需要看口径定义或全部口径时用 enum_metrics；"
        + "维度过滤被拒时按结果里的「可用维度」改正后重试，**不得省略该过滤条件重算**——"
        + "那会把问题换成另一个范围、答出另一个问题的数字；确实无法按该范围取数就如实说明；"
        + "若系统内没有对应口径，如实说明「该指标在系统内尚无定义」，禁止自行拼公式估算或心算。";

    private const string DiagnosisUsage =
        "当用户问「这张单为什么存不下去 / 改不了 / 办不下去」时，用 diagnose_record 按当前模块与单据号取证，"
        + "再按返回的证据顺序解释原因并给出下一步；"
        + "属于权限或状态的原因必须直接说明（例如「你没有批核权限」「单据已结案，先取消结案」），不得含糊成「操作失败」；"
        + "证据不足时如实说明缺哪些证据，不要猜测根因。";

    private const string KnowledgeChannel =
        "机制问题走 describe_mechanism 现算；规范、坑与设计动因走 kb_search 并标注来源与版本；冲突以服务端元数据为准。";

    private const string MetadataSearch =
        "不知道某个业务名词在系统里叫什么（模块名、表名、字段名、自定义动作名）时，"
        + "先用 search_metadata 按关键字检索；拿到模块号/字段键后再 describe_module / get_form_schema。不要靠猜。";

    private const string Navigation =
        "找数据时按此顺序：先 list_my_capabilities 确认本人能看哪些模块，再 describe_module 看主表/明细表，"
        + "get_form_schema 看可填字段（new/edit/view）；查列表用 search_records（可给 conditions、日期/状态、排序与翻页），"
        + "读整单用 get_record_detail（含明细行与 _view/_edit 链接）。不要把模块名或字段键猜成事实。";

    private const string Reference =
        "客户、供应商、物料、员工等引用字段：先 resolve_reference 用关键字取到候选与回填映射，"
        + "再把返回值填进 values；不要臆造编码，也不要用 search_records 去猜基础资料表。";

    private const string RecordWrite =
        "新增/修改/删除单据前先 preview_record_action 预演；服务端返回操作卡时，"
        + "逐行说明可执行/不可执行及原因，由用户在卡上确认。删除与多行批量助手不直接执行；"
        + "单行新增/修改可以代劳，但仍以服务端预演结论为准。";

    private const string DocumentAction =
        "模块自定义动作（取价/重算/生成/回执等）先 list_document_actions 看动作键与参数；"
        + "confirm=true 的动作只生成确认卡，绝不能直接执行；confirm=false 的动作才可用 apply_document_action。"
        + "动作失败时按返回的原因码说明，不要换一个动作猜着跑。";
}
