-- ============================================================================
-- EOS.ERP migration 463：助手行为规则段（可库内维护，红线锁）
-- ----------------------------------------------------------------------------
--  背景（ADR-046）：规则段的措辞此前是代码常量，改一句话就要发版；客户运维无法自助调整。
--  这里把 8 段规则搬进库：
--   · SEGMENT_TEXT    段落正文（注入系统提示词）
--   · REQUIRED_TOOLS  依赖工具（逗号分隔）：工具没发/被页面裁掉，本段不注入
--   · IS_LOCKED       红线段：文本只能通过迁移改，写接口直接拒绝（批核不可代签等）
--   · ENABLED/SORT_IDX 启停与顺序
--
--  种子文本与 EOS.API 的 PromptSegmentDefaults 逐字一致（真库用例断言，防两边漂移）。
--  库内一行都没有时，程序用代码默认值兜底（单测与全新库首启）。
-- ============================================================================
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;

IF DB_NAME() <> N'EOS.ERP'
    THROW 51980, N'本脚本只允许在 EOS.ERP 数据库上执行。', 1;

IF OBJECT_ID(N'dbo.ASSISTANT_PROMPT_SEGMENT', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.ASSISTANT_PROMPT_SEGMENT
    (
        SEGMENT_KEY    nvarchar(40)   NOT NULL,
        TITLE          nvarchar(60)   NOT NULL,
        SEGMENT_TEXT   nvarchar(2000) NOT NULL,
        REQUIRED_TOOLS nvarchar(300)  NULL,
        IS_LOCKED      bit            NOT NULL CONSTRAINT DF_ASSISTANT_PROMPT_SEGMENT_LOCKED DEFAULT 0,
        ENABLED        bit            NOT NULL CONSTRAINT DF_ASSISTANT_PROMPT_SEGMENT_ENABLED DEFAULT 1,
        SORT_IDX       int            NOT NULL CONSTRAINT DF_ASSISTANT_PROMPT_SEGMENT_SORT DEFAULT 0,
        SOURCE_REF     nvarchar(200)  NULL,
        CREATED_BY     nvarchar(20)   NOT NULL CONSTRAINT DF_ASSISTANT_PROMPT_SEGMENT_CREATED_BY DEFAULT N'',
        CREATED_TIME   datetime       NOT NULL CONSTRAINT DF_ASSISTANT_PROMPT_SEGMENT_CREATED_TIME DEFAULT GETDATE(),
        MODIFIED_BY    nvarchar(20)   NULL,
        MODIFIED_TIME  datetime       NULL,
        CONSTRAINT PK_ASSISTANT_PROMPT_SEGMENT PRIMARY KEY CLUSTERED (SEGMENT_KEY)
    );
    PRINT N'== ASSISTANT_PROMPT_SEGMENT 已创建 ==';
END
ELSE
    PRINT N'== ASSISTANT_PROMPT_SEGMENT 已存在，跳过建表 ==';
GO

IF NOT EXISTS (SELECT 1 FROM dbo.ASSISTANT_PROMPT_SEGMENT)
BEGIN
    INSERT INTO dbo.ASSISTANT_PROMPT_SEGMENT
        (SEGMENT_KEY, TITLE, SEGMENT_TEXT, REQUIRED_TOOLS, IS_LOCKED, ENABLED, SORT_IDX, SOURCE_REF, CREATED_BY, CREATED_TIME)
    VALUES
        (N'METRIC_USAGE', N'指标口径取数',
         N'回答涉及金额、数量、比率等量化指标时，必须用 resolve_metric 按系统口径取数'
         + N'（metric_id 可直接写业务词，如「营业额」；命中多条时按返回的候选改用口径标识），'
         + N'并在回答中标注所用口径名与数值；需要看口径定义或全部口径时用 enum_metrics；'
         + N'维度过滤被拒时按结果里的「可用维度」改正后重试，**不得省略该过滤条件重算**——'
         + N'那会把问题换成另一个范围、答出另一个问题的数字；确实无法按该范围取数就如实说明；'
         + N'若系统内没有对应口径，如实说明「该指标在系统内尚无定义」，禁止自行拼公式估算或心算。',
         N'resolve_metric', 1, 1, 10, N'ChatService.MetricUsageRule → ADR-046', N'463', GETDATE()),

        (N'DIAGNOSIS_USAGE', N'诊断取证',
         N'当用户问「这张单为什么存不下去 / 改不了 / 办不下去」时，用 diagnose_record 按当前模块与单据号取证，'
         + N'再按返回的证据顺序解释原因并给出下一步；'
         + N'属于权限或状态的原因必须直接说明（例如「你没有批核权限」「单据已结案，先取消结案」），不得含糊成「操作失败」；'
         + N'证据不足时如实说明缺哪些证据，不要猜测根因。',
         N'diagnose_record', 0, 1, 20, N'ChatService.DiagnosisUsageRule → ADR-046', N'463', GETDATE()),

        (N'KNOWLEDGE_CHANNEL', N'机制与知识通道',
         N'机制问题走 describe_mechanism 现算；规范、坑与设计动因走 kb_search 并标注来源与版本；冲突以服务端元数据为准。',
         N'describe_mechanism,kb_search', 0, 1, 30, N'ChatService.KnowledgeChannelRule → ADR-046', N'463', GETDATE()),

        (N'METADATA_SEARCH', N'元数据检索',
         N'不知道某个业务名词在系统里叫什么（模块名、表名、字段名、自定义动作名）时，'
         + N'先用 search_metadata 按关键字检索；拿到模块号/字段键后再 describe_module / get_form_schema。不要靠猜。',
         N'search_metadata', 0, 1, 40, N'ChatService.MetadataSearchRule → ADR-046', N'463', GETDATE()),

        (N'NAVIGATION', N'导航与取数顺序',
         N'找数据时按此顺序：先 list_my_capabilities 确认本人能看哪些模块，再 describe_module 看主表/明细表，'
         + N'get_form_schema 看可填字段（new/edit/view）；查列表用 search_records（可给 conditions、日期/状态、排序与翻页），'
         + N'读整单用 get_record_detail（含明细行与 _view/_edit 链接）。不要把模块名或字段键猜成事实。',
         N'list_my_capabilities,describe_module,get_form_schema,search_records,get_record_detail', 0, 1, 50,
         N'ChatService.NavigationRule → ADR-046', N'463', GETDATE()),

        (N'REFERENCE', N'引用字段解析',
         N'客户、供应商、物料、员工等引用字段：先 resolve_reference 用关键字取到候选与回填映射，'
         + N'再把返回值填进 values；不要臆造编码，也不要用 search_records 去猜基础资料表。',
         N'resolve_reference', 0, 1, 60, N'ChatService.ReferenceRule → ADR-046', N'463', GETDATE()),

        (N'RECORD_WRITE', N'记录写入与操作卡',
         N'新增/修改/删除单据前先 preview_record_action 预演；服务端返回操作卡时，'
         + N'逐行说明可执行/不可执行及原因，由用户在卡上确认。删除与多行批量助手不直接执行；'
         + N'单行新增/修改可以代劳，但仍以服务端预演结论为准。',
         N'preview_record_action,apply_record_action', 1, 1, 70, N'ChatService.RecordWriteRule → ADR-046', N'463', GETDATE()),

        (N'DOCUMENT_ACTION', N'自定义单据动作',
         N'模块自定义动作（取价/重算/生成/回执等）先 list_document_actions 看动作键与参数；'
         + N'confirm=true 的动作只生成确认卡，绝不能直接执行；confirm=false 的动作才可用 apply_document_action。'
         + N'动作失败时按返回的原因码说明，不要换一个动作猜着跑。',
         N'list_document_actions,preview_document_action,apply_document_action', 1, 1, 80,
         N'ChatService.DocumentActionRule → ADR-046', N'463', GETDATE());

    PRINT N'== 8 段行为规则已入库（3 段红线锁） ==';
END
ELSE
    PRINT N'== 行为规则段已存在，跳过种子 ==';
