-- ============================================================================
-- EOS.ERP migration 298: 助手参数第三批——**能力面工具开关**（27 条）
-- ----------------------------------------------------------------------------
--  来源：ADR-030 §10 批 D、§5.3.4。
--
--  在此之前"某个工具开不开"表达不出来：27 个工具在 Program.cs 里逐项注册，
--  **开关就是"删掉那一行代码"**——改一次要改代码、要重新部署，也没有任何留痕。
--  本次给每个工具一条 bit 参数（默认 1 = 开），关掉是双向的：
--    · 不发工具声明给模型（模型看不到它）；
--    · 模型仍然幻觉出这个名字时明确拒绝（"该能力已被管理员关闭"），而不是当成"工具不存在"。
--
--  键名由工具名**机械生成**：TOOL_ + 工具名转大写（`list_modules` → `TOOL_LIST_MODULES`）。
--  代码侧同一份映射在 AssistantToolKeys（含 27 个工具名的单一清单），
--  它与各工具类里的 ToolName 常量是否一致由离线门禁扫描断言。
--
--  顺序：SEQ_NO 必须与 AssistantToolKeys.ToolNames 的**声明顺序**一致——
--  连库门禁会逐字段比对它，排错顺序会当场判红。
--
--  默认全开，所以本次只写定义（DEFAULT_VALUE = '1'），PARAM_VALUE 一律留空：
--  "没配过"就是开，每加一个工具也不必回填历史行。
--
--  幂等：建行按 (OWNER_MODULE, PARAM_KEY) 判存在；可重复执行。
-- ============================================================================
SET NOCOUNT ON;
SET XACT_ABORT ON;

IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, N'本脚本只能在 EOS.ERP 数据库内执行。', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.MODULES WHERE M_IDX = 3105)
    THROW 50500, N'模块 3105 助手设置不存在，迁移中止（先跑迁移 293）。', 1;

BEGIN TRANSACTION;

/* 工具名、参数键与组内序号：顺序即目录里的声明顺序（连库门禁逐字段比对）。
   键名在这里**逐字写全**而不是拼接出来——门禁要能"点名"每个键，
   拼接出来的键在脚本原文里找不到，等于这条断言失去了对象 */
DECLARE @tools TABLE (SEQ_NO INT, TOOL_NAME NVARCHAR(64), PARAM_KEY NVARCHAR(64));

INSERT INTO @tools (SEQ_NO, TOOL_NAME, PARAM_KEY) VALUES
    (50,  N'list_modules',             N'TOOL_LIST_MODULES'),
    (60,  N'list_tables',              N'TOOL_LIST_TABLES'),
    (70,  N'list_views',               N'TOOL_LIST_VIEWS'),
    (80,  N'list_procedures',          N'TOOL_LIST_PROCEDURES'),
    (90,  N'describe_module',          N'TOOL_DESCRIBE_MODULE'),
    (100, N'describe_table',           N'TOOL_DESCRIBE_TABLE'),
    (110, N'get_form_schema',          N'TOOL_GET_FORM_SCHEMA'),
    (120, N'get_field_relations',      N'TOOL_GET_FIELD_RELATIONS'),
    (130, N'search_records',           N'TOOL_SEARCH_RECORDS'),
    (140, N'get_record_detail',        N'TOOL_GET_RECORD_DETAIL'),
    (150, N'get_my_digest',            N'TOOL_GET_MY_DIGEST'),
    (160, N'diagnose_record',          N'TOOL_DIAGNOSE_RECORD'),
    (170, N'diagnose_module',          N'TOOL_DIAGNOSE_MODULE'),
    (180, N'get_module_flow',          N'TOOL_GET_MODULE_FLOW'),
    (190, N'list_my_capabilities',     N'TOOL_LIST_MY_CAPABILITIES'),
    (200, N'enum_metrics',             N'TOOL_ENUM_METRICS'),
    (210, N'resolve_metric',           N'TOOL_RESOLVE_METRIC'),
    (220, N'kb_search',                N'TOOL_KB_SEARCH'),
    (230, N'describe_mechanism',       N'TOOL_DESCRIBE_MECHANISM'),
    (240, N'draft_record',             N'TOOL_DRAFT_RECORD'),
    (250, N'apply_changeset',          N'TOOL_APPLY_CHANGESET'),
    (260, N'preview_record_action',    N'TOOL_PREVIEW_RECORD_ACTION'),
    (270, N'apply_record_action',      N'TOOL_APPLY_RECORD_ACTION'),
    (280, N'preview_batch_decision',   N'TOOL_PREVIEW_BATCH_DECISION'),
    (290, N'clone_module_config',      N'TOOL_CLONE_MODULE_CONFIG'),
    (300, N'preview_config_change',    N'TOOL_PREVIEW_CONFIG_CHANGE'),
    (310, N'apply_config_change',      N'TOOL_APPLY_CONFIG_CHANGE');

INSERT INTO dbo.SYSSS (
    OWNER_MODULE, PARAM_KEY, PARAM_VALUE, VALUE_TYPE, DEFAULT_VALUE,
    GROUP_CODE, GROUP_LABEL, GROUP_SEQ, SEQ_NO, DESC_TEXT, EFFECT_SCOPE, REMARK,
    CREATE_PERSON, CREATE_DATE)
SELECT
    3105, t.PARAM_KEY, NULL, N'bit', N'1',
    N'CAPABILITY', N'能力面', 40, t.SEQ_NO,
    N'关掉后模型看不到 ' + t.TOOL_NAME + N' 这个工具，也调不动它（它若仍被调用会被明确拒绝）',
    N'immediate', NULL, N'mig-298', SYSDATETIME()
FROM @tools t
WHERE NOT EXISTS (
    SELECT 1 FROM dbo.SYSSS x
    WHERE x.OWNER_MODULE = 3105 AND x.PARAM_KEY = t.PARAM_KEY);

PRINT N'== 能力面工具开关建行：' + CONVERT(NVARCHAR(10), @@ROWCOUNT) + N' 条（表内已有则跳过）==';

/* ---------- 断言 ---------- */
-- 三批合计 72 条（9 + 36 + 27）
IF NOT EXISTS (SELECT 1 FROM dbo.SYSSS WHERE OWNER_MODULE = 3105 HAVING COUNT(*) = 72)
    THROW 50501, N'助手参数条数不是 72（9 + 36 + 27），迁移中止。', 1;

IF EXISTS (SELECT 1 FROM @tools t
           WHERE NOT EXISTS (SELECT 1 FROM dbo.SYSSS s
                             WHERE s.OWNER_MODULE = 3105 AND s.PARAM_KEY = t.PARAM_KEY))
    THROW 50502, N'有工具开关未落库，迁移中止。', 1;

-- 工具开关一律 bit、默认开：写成别的类型或默认关，会让"没配过"变成"配过且关闭"
IF EXISTS (SELECT 1 FROM dbo.SYSSS
           WHERE OWNER_MODULE = 3105 AND PARAM_KEY LIKE N'TOOL[_]%'
             AND (VALUE_TYPE <> N'bit' OR DEFAULT_VALUE <> N'1' OR EFFECT_SCOPE <> N'immediate'))
    THROW 50503, N'工具开关的类型 / 默认值 / 生效范围不符（应为 bit、默认 1、immediate）。', 1;

-- 红线工具面（批核族、权限授予类）**没有注册项**，因此也不该有开关
IF EXISTS (SELECT 1 FROM dbo.SYSSS
           WHERE OWNER_MODULE = 3105
             AND (PARAM_KEY LIKE N'TOOL[_]%APPROVE%' OR PARAM_KEY LIKE N'%RIGHTS[_]%'
               OR PARAM_KEY LIKE N'%USER[_]ADMIN%' OR PARAM_KEY LIKE N'%MENU[_]ADMIN%'
               OR PARAM_KEY LIKE N'%END[_]CASE%'))
    THROW 50504, N'出现了批核族 / 权限授予类的工具开关——它们在助手侧没有注册项（ADR-030 §7.3）。', 1;

COMMIT TRANSACTION;

PRINT N'== 收口：能力面工具开关已并入 dbo.SYSSS（3105 合计 72 条）==';
