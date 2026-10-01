-- ============================================================================
-- EOS.ERP migration 300: 助手参数第五批——**行为常量**（21 条）
-- ----------------------------------------------------------------------------
--  来源：ADR-030 §5.3.2 / §5.3.8 / §5.3.9、§10 批 E。
--
--  这一批搬的是"跑在代码里的数字"：
--    · CHAT   8 条 —— ChatService 的私有常量（其中"工具摘要 160 / 审计参数片段 200"
--                     连常量名都没有，是两处内联字面量）；
--    · MEMORY 9 条 —— MemoryDistiller 的公开常量 + AssistantMemoryStore 的公开常量；
--    · KB     4 条 —— KbSearchTool / KbChunker 的常量。
--
--  为什么值得搬：这些是**用户真想调**的数字（历史条数、工具轮数、记忆注入条数、检索命中数），
--  而此前调它们只有一条路——改代码重新部署。管理面看到的"9 项助手设置"里一个都没有。
--
--  键名一律全大写；默认值取自参数目录里那份**唯一**的代码默认值
--  （AssistantChatLimitsOptions / AssistantMemoryLimitsOptions / AssistantKbLimitsOptions
--  的属性初始值），连库门禁会逐字段比对，所以这里的数字与代码不可能各说各话。
--
--  幂等：建行按 (OWNER_MODULE, PARAM_KEY) 判存在；可重复执行。
-- ============================================================================
SET NOCOUNT ON;
SET XACT_ABORT ON;

IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, N'本脚本只能在 EOS.ERP 数据库内执行。', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.MODULES WHERE M_IDX = 3105)
    THROW 50700, N'模块 3105 助手设置不存在，迁移中止（先跑迁移 293）。', 1;

BEGIN TRANSACTION;

/* 组内序号接在既有行之后：CHAT 是新组（10 起），MEMORY 接 MEM_ENABLE_AUTO_DISTILL（其序号为 10），
   KB 也是新组。**新条目一律追加在组内末尾**——插在中间会让已在库里的行与目录对不上。 */
DECLARE @rows TABLE (
    PARAM_KEY NVARCHAR(64), GROUP_CODE NVARCHAR(32), GROUP_LABEL NVARCHAR(64),
    GROUP_SEQ INT, SEQ_NO INT, DEFAULT_VALUE NVARCHAR(4000), DESC_TEXT NVARCHAR(1000));

INSERT INTO @rows (PARAM_KEY, GROUP_CODE, GROUP_LABEL, GROUP_SEQ, SEQ_NO, DEFAULT_VALUE, DESC_TEXT) VALUES
    -- 域 CHAT（组间顺序 20）
    (N'CHAT_MAX_HISTORY_MESSAGES',    N'CHAT', N'对话行为', 20,  10, N'60',     N'一次请求读入多少条历史消息；真正的裁剪按当前模型的上下文窗口算'),
    (N'CHAT_CONTEXT_RESERVE_TOKENS',  N'CHAT', N'对话行为', 20,  20, N'4096',   N'留给系统提示、记忆、处境段与工具结果的余量；调小会多带历史，但被厂商拒绝的风险变大'),
    (N'CHAT_DEFAULT_CONTEXT_WINDOW',  N'CHAT', N'对话行为', 20,  30, N'16384',  N'模型行没填上下文窗口时的保守默认'),
    (N'CHAT_MAX_CONTENT_LENGTH',      N'CHAT', N'对话行为', 20,  40, N'8000',   N'用户单条消息的长度上限；控制器入参校验与对话编排共用同一个值'),
    (N'CHAT_MAX_TOOL_ARGUMENTS_LENGTH', N'CHAT', N'对话行为', 20, 50, N'2000',  N'模型输出的工具参数长度上限，防止把一次调用撑成一段文本'),
    (N'CHAT_MAX_TOOL_ROUNDS',         N'CHAT', N'对话行为', 20,  60, N'4',      N'一轮回答里最多做几次工具调用；它同时决定成本预留倍率（预留 = 单次预留 ×（轮数 + 1））'),
    (N'CHAT_TOOL_DIGEST_LENGTH',      N'CHAT', N'对话行为', 20,  70, N'160',    N'落库 TOOL_CALLS_JSON 时工具结果摘要保留多少字符（完整结果不落库）'),
    (N'CHAT_AUDIT_ARGUMENT_LENGTH',   N'CHAT', N'对话行为', 20,  80, N'200',    N'审计里工具参数片段保留多少字符'),

    -- 域 MEMORY（组间顺序 80；组内接在 MEM_ENABLE_AUTO_DISTILL 之后）
    (N'MEM_MAX_CANDIDATES',           N'MEMORY', N'记忆', 80,  20, N'5',    N'一次自动提炼最多产出几条候选记忆（都要用户确认才生效）'),
    (N'MEM_SUGGEST_THRESHOLD',        N'MEMORY', N'记忆', 80,  30, N'80',   N'高于此分标记为「建议记住」；必须大于保留门槛，否则候选要么全留、要么全丢'),
    (N'MEM_KEEP_THRESHOLD',           N'MEMORY', N'记忆', 80,  40, N'40',   N'低于此分直接丢弃，连候选都不进；与建议门槛成对调整'),
    (N'MEM_DISTILL_TURNS',            N'MEMORY', N'记忆', 80,  50, N'10',   N'提炼只看最近这几轮对话'),
    (N'MEM_MAX_PER_USER',             N'MEMORY', N'记忆', 80,  60, N'200',  N'每人记忆条数上限；超限时按最久未访问归档一条，而不是拒绝写入'),
    (N'MEM_MAX_KEY_LENGTH',           N'MEMORY', N'记忆', 80,  70, N'200',  N'记忆标题的长度上限'),
    (N'MEM_MAX_VALUE_LENGTH',         N'MEMORY', N'记忆', 80,  80, N'2000', N'记忆内容的长度上限'),
    (N'MEM_MAX_PREFERENCES_LENGTH',   N'MEMORY', N'记忆', 80,  90, N'4000', N'派生画像（偏好）的长度上限'),
    (N'MEM_INJECTION_TOP_K',          N'MEMORY', N'记忆', 80, 100, N'5',    N'每轮对话注入提示词的记忆条数上限；调大会挤占历史与工具结果的预算'),

    -- 域 KB（组间顺序 90）
    (N'KB_SEARCH_MAX_HITS',           N'KB', N'知识库', 90, 10, N'5',   N'一次知识库检索最多返回几条命中'),
    (N'KB_SEARCH_MAX_CONTENT_LENGTH', N'KB', N'知识库', 90, 20, N'300', N'命中片段进模型前截断到多少字符'),
    (N'KB_CHUNK_MAX_CHARS',           N'KB', N'知识库', 90, 30, N'700', N'文档入库时的切块长度；必须大于重叠长度，否则切块原地打转'),
    (N'KB_CHUNK_OVERLAP_CHARS',       N'KB', N'知识库', 90, 40, N'100', N'相邻块的重叠字符数，保证跨块句子不被切断；必须小于块长');

INSERT INTO dbo.SYSSS (
    OWNER_MODULE, PARAM_KEY, PARAM_VALUE, VALUE_TYPE, DEFAULT_VALUE,
    GROUP_CODE, GROUP_LABEL, GROUP_SEQ, SEQ_NO, DESC_TEXT, EFFECT_SCOPE, REMARK,
    CREATE_PERSON, CREATE_DATE)
SELECT
    3105, r.PARAM_KEY, NULL, N'int', r.DEFAULT_VALUE,
    r.GROUP_CODE, r.GROUP_LABEL, r.GROUP_SEQ, r.SEQ_NO, r.DESC_TEXT, N'immediate', NULL,
    N'mig-300', SYSDATETIME()
FROM @rows r
WHERE NOT EXISTS (
    SELECT 1 FROM dbo.SYSSS x WHERE x.OWNER_MODULE = 3105 AND x.PARAM_KEY = r.PARAM_KEY);

PRINT N'== CHAT / MEMORY / KB 行为常量建行：' + CONVERT(NVARCHAR(10), @@ROWCOUNT) + N' 条（表内已有则跳过）==';

/* ---------- 断言 ---------- */
-- 五批合计 96 条（9 + 36 + 27 + 3 + 21）
IF NOT EXISTS (SELECT 1 FROM dbo.SYSSS WHERE OWNER_MODULE = 3105 HAVING COUNT(*) = 96)
    THROW 50701, N'助手参数条数不是 96（9 + 36 + 27 + 3 + 21），迁移中止。', 1;

IF EXISTS (SELECT 1 FROM @rows r
           WHERE NOT EXISTS (SELECT 1 FROM dbo.SYSSS s
                             WHERE s.OWNER_MODULE = 3105 AND s.PARAM_KEY = r.PARAM_KEY))
    THROW 50702, N'有行为参数未落库，迁移中止。', 1;

-- 一律 int、立即生效、取值列留空（= 用默认值）
IF EXISTS (SELECT 1 FROM @rows r
           JOIN dbo.SYSSS s ON s.OWNER_MODULE = 3105 AND s.PARAM_KEY = r.PARAM_KEY
           WHERE s.VALUE_TYPE <> N'int' OR s.EFFECT_SCOPE <> N'immediate'
              OR s.DEFAULT_VALUE IS NULL OR s.PARAM_VALUE IS NOT NULL)
    THROW 50703, N'行为参数的类型 / 生效范围 / 默认值形态不符（应为 int、immediate、取值留空）。', 1;

-- 阈值型参数的配对关系在库里也必须成立（校验器与解析器都会拦，这里拦的是"脚本自己写错"）
IF EXISTS (SELECT 1 FROM dbo.SYSSS
           WHERE OWNER_MODULE = 3105 AND PARAM_KEY = N'MEM_SUGGEST_THRESHOLD'
             AND CAST(PARAM_VALUE AS INT) <= (SELECT CAST(s2.PARAM_VALUE AS INT) FROM dbo.SYSSS s2
                                              WHERE s2.OWNER_MODULE = 3105 AND s2.PARAM_KEY = N'MEM_KEEP_THRESHOLD'))
    THROW 50704, N'MEM_SUGGEST_THRESHOLD 必须大于 MEM_KEEP_THRESHOLD，迁移中止。', 1;

COMMIT TRANSACTION;

PRINT N'== 收口：行为常量已并入 dbo.SYSSS（3105 合计 96 条）==';
