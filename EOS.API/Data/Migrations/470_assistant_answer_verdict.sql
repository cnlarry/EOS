-- ============================================================================
-- EOS.ERP migration 470：判官 J1——结论落库 + 判官参数（ADR-044）
-- ----------------------------------------------------------------------------
--  J0 已交付 rubric v1 + 判分器 + 回放基线；本迁移补 J1 的两件持久化事实：
--   1. ASSISTANT_ANSWER_VERDICT：一条回答 × 一个条款 = 一行四态结论。
--      唯一键 (MESSAGE_ID, CLAUSE, RUBRIC_VERSION, JUDGE_MODEL) —— 同一回答可被
--      不同条款版本/不同判官模型重判，历史不覆盖（ADR-044 §4.3）。
--   2. 域 JUDGE 的 12 条参数（采样率 / 截断 / 批量 / 日次数 / 日成本 / 扫描间隔 /
--      保留期 / 时间窗）。默认值取自代码默认对象（AssistantJudgeLimitsOptions），
--      迁移只落库、不写第二份数字的解释。
--
--  刻意不建外键：删除会话会连消息一起删，而结论是"当时这条回答被判成什么"的历史观察，
--  不该被业务数据的删除动作带着消失（保留期到期由后台按 JUDGED_TIME 清理）。
--
--  刻意不用 CREATED_BY / CREATED_TIME：那是单据生命周期列，建在同一张表上会被
--  check-lifecycle-columns 要求 NOT NULL + DEFAULT。本表是观察记录，不是单据。
--
--  幂等：建表/建索引/建参数行都按存在性判；可重复执行。
-- ============================================================================
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;

IF DB_NAME() <> N'EOS.ERP'
    THROW 51970, N'本脚本只允许在 EOS.ERP 数据库上执行。', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.MODULES WHERE M_IDX = 3105)
    THROW 51971, N'模块 3105 助手设置不存在，迁移终止。', 1;

/* ---------- 1. 判定结论 ---------- */
IF OBJECT_ID(N'dbo.ASSISTANT_ANSWER_VERDICT', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.ASSISTANT_ANSWER_VERDICT
    (
        VERDICT_ID      bigint        IDENTITY(1, 1) NOT NULL,
        MESSAGE_ID      bigint        NOT NULL,
        SESSION_ID      bigint        NOT NULL,
        USER_ID         nvarchar(50)  NOT NULL,
        CLAUSE          nvarchar(2)   NOT NULL,
        VERDICT         nvarchar(6)   NOT NULL,
        EVIDENCE        nvarchar(1000) NULL,
        NOTE            nvarchar(400) NULL,
        RUBRIC_VERSION  nvarchar(16)  NOT NULL,
        JUDGE_MODEL     nvarchar(128) NOT NULL,
        JUDGE_FALLBACK  bit           NOT NULL CONSTRAINT DF_ASSISTANT_ANSWER_VERDICT_FALLBACK DEFAULT 0,
        JUDGED_TIME     datetime2(3)  NOT NULL CONSTRAINT DF_ASSISTANT_ANSWER_VERDICT_TIME DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_ASSISTANT_ANSWER_VERDICT PRIMARY KEY CLUSTERED (VERDICT_ID),
        CONSTRAINT CK_ASSISTANT_ANSWER_VERDICT_VALUE
            CHECK (VERDICT IN (N'PASS', N'FAIL', N'NA', N'UNSURE'))
    );

    PRINT N'== ASSISTANT_ANSWER_VERDICT 已创建 ==';
END
ELSE
    PRINT N'== ASSISTANT_ANSWER_VERDICT 已存在，跳过建表 ==';
GO

/* ---------- 2. 索引 ---------- */
IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE object_id = OBJECT_ID(N'dbo.ASSISTANT_ANSWER_VERDICT')
                 AND name = N'UX_ASSISTANT_ANSWER_VERDICT_KEY')
BEGIN
    -- 判重身份：同一回答 + 同一条款 + 同一版本 + 同一模型只有一条结论
    CREATE UNIQUE INDEX UX_ASSISTANT_ANSWER_VERDICT_KEY
        ON dbo.ASSISTANT_ANSWER_VERDICT (MESSAGE_ID, CLAUSE, RUBRIC_VERSION, JUDGE_MODEL);

    PRINT N'== 唯一索引 UX_ASSISTANT_ANSWER_VERDICT_KEY 已创建 ==';
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE object_id = OBJECT_ID(N'dbo.ASSISTANT_ANSWER_VERDICT')
                 AND name = N'IX_ASSISTANT_ANSWER_VERDICT_TIME')
BEGIN
    -- 看板按时间窗聚合、后台按时间清理，两处都从 JUDGED_TIME 进
    CREATE INDEX IX_ASSISTANT_ANSWER_VERDICT_TIME
        ON dbo.ASSISTANT_ANSWER_VERDICT (JUDGED_TIME)
        INCLUDE (MESSAGE_ID, CLAUSE, VERDICT, RUBRIC_VERSION);

    PRINT N'== 索引 IX_ASSISTANT_ANSWER_VERDICT_TIME 已创建 ==';
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE object_id = OBJECT_ID(N'dbo.ASSISTANT_ANSWER_VERDICT')
                 AND name = N'IX_ASSISTANT_ANSWER_VERDICT_SESSION')
BEGIN
    -- 「按会话看这一轮回答被判成什么」是看板的主用法之一
    CREATE INDEX IX_ASSISTANT_ANSWER_VERDICT_SESSION
        ON dbo.ASSISTANT_ANSWER_VERDICT (SESSION_ID, JUDGED_TIME);

    PRINT N'== 索引 IX_ASSISTANT_ANSWER_VERDICT_SESSION 已创建 ==';
END
GO

/* ---------- 3. 域 JUDGE 的参数行 ---------- */
BEGIN TRANSACTION;

DECLARE @items TABLE (
    SEQ_NO INT, PARAM_KEY NVARCHAR(64), GROUP_CODE NVARCHAR(32), GROUP_LABEL NVARCHAR(64),
    GROUP_SEQ INT, VALUE_TYPE NVARCHAR(16), DEFAULT_VALUE NVARCHAR(200), DESC_TEXT NVARCHAR(400));

INSERT INTO @items VALUES
    (10, N'JUDGE_ENABLED', N'JUDGE', N'判官', 100, N'bit', N'1',
     N'后台按抽样对已落库的回答做条款级判定（ADR-044）。关掉即完全不判定；已落库的结论与看板数字保留，不会因此变成「没问题」。'),
    (20, N'JUDGE_SAMPLE_RATE_PCT', N'JUDGE', N'判官', 100, N'int', N'20',
     N'金额/数量/比率类以外的回答抽多少判定。确定性抽样（按消息编号落桶），所以同一条回答重复扫描的结果一致。0 = 不判普通回答。'),
    (30, N'JUDGE_AMOUNT_SAMPLE_RATE_PCT', N'JUDGE', N'判官', 100, N'int', N'100',
     N'涉及金额/数量/比率的回答抽多少判定——口径标注与范围一致只在这类回答上才有意义，默认全判。'),
    (40, N'JUDGE_MAX_QUESTION_CHARS', N'JUDGE', N'判官', 100, N'int', N'2000',
     N'送进判官的问句截断长度。'),
    (50, N'JUDGE_MAX_ANSWER_CHARS', N'JUDGE', N'判官', 100, N'int', N'12000',
     N'送进判官的回答截断长度。超上限时提示词会明确写出「回答被截断」，判官只能记 UNSURE——调小它等于把长回答判成「无法判定」。'),
    (60, N'JUDGE_MAX_TOOL_DIGEST_CHARS', N'JUDGE', N'判官', 100, N'int', N'300',
     N'每条工具结果摘要送进判官的截断长度。截得太短会让 E 条款（不编造事实）判不出来。'),
    (70, N'JUDGE_BATCH_SIZE', N'JUDGE', N'判官', 100, N'int', N'20',
     N'一次扫描（或手动触发）最多判几条。'),
    (80, N'JUDGE_DAILY_MAX_CALLS', N'JUDGE', N'判官', 100, N'int', N'200',
     N'到顶即本轮跳过，并在看板上明示「已达上限」——不静默停判（停判会伪装成没问题）。'),
    (90, N'JUDGE_DAILY_COST_CAP_YUAN', N'JUDGE', N'判官', 100, N'decimal', N'5',
     N'判定用量的日成本上限。判定用量记在系统账户 __judge__（看得见成本），不扣任何人的日上限；到顶同样在本轮跳过并在看板明示。'),
    (100, N'JUDGE_SCAN_INTERVAL_MINUTES', N'JUDGE', N'判官', 100, N'int', N'10',
     N'后台扫描的间隔。判定是离线批处理，与对话请求无关。'),
    (110, N'JUDGE_RETENTION_DAYS', N'JUDGE', N'判官', 100, N'int', N'180',
     N'过期结论按天清理。明细长期堆积会诱导拿它做考核——那不是判官的用途（ADR-044 §2.4）。'),
    (120, N'JUDGE_WINDOW_DAYS', N'JUDGE', N'判官', 100, N'int', N'7',
     N'只看最近这些天的回答，避免一次把历史全捞进来（历史回答由人工抽样覆盖）。');

INSERT INTO dbo.SYS_PARAMETER (
    OWNER_MODULE, PARAM_KEY, PARAM_VALUE, VALUE_TYPE, DEFAULT_VALUE,
    GROUP_CODE, GROUP_LABEL, GROUP_SEQ, SEQ_NO, DESC_TEXT, EFFECT_SCOPE, REMARK,
    CREATED_BY, CREATED_TIME)
SELECT
    3105, i.PARAM_KEY, NULL, i.VALUE_TYPE, i.DEFAULT_VALUE,
    i.GROUP_CODE, i.GROUP_LABEL, i.GROUP_SEQ, i.SEQ_NO, i.DESC_TEXT, N'immediate', NULL,
    N'mig-470', SYSDATETIME()
FROM @items i
WHERE NOT EXISTS (
    SELECT 1 FROM dbo.SYS_PARAMETER x WHERE x.OWNER_MODULE = 3105 AND x.PARAM_KEY = i.PARAM_KEY);

PRINT N'== 判官参数建行：' + CONVERT(NVARCHAR(10), @@ROWCOUNT) + N' 条（表内已有则跳过）==';

/* ---------- 4. 断言 ---------- */
IF OBJECT_ID(N'dbo.ASSISTANT_ANSWER_VERDICT', N'U') IS NULL
    THROW 51972, N'ASSISTANT_ANSWER_VERDICT 未建成，迁移终止。', 1;

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE object_id = OBJECT_ID(N'dbo.ASSISTANT_ANSWER_VERDICT')
                 AND name = N'UX_ASSISTANT_ANSWER_VERDICT_KEY' AND is_unique = 1)
    THROW 51973, N'判定结论的唯一索引缺失，迁移终止。', 1;

IF EXISTS (SELECT 1 FROM @items i
           WHERE NOT EXISTS (SELECT 1 FROM dbo.SYS_PARAMETER s
                             WHERE s.OWNER_MODULE = 3105 AND s.PARAM_KEY = i.PARAM_KEY))
    THROW 51974, N'有判官参数未落库，迁移终止。', 1;

IF EXISTS (SELECT 1 FROM dbo.SYS_PARAMETER
           WHERE OWNER_MODULE = 3105 AND PARAM_KEY LIKE N'JUDGE[_]%'
             AND (GROUP_CODE <> N'JUDGE' OR GROUP_SEQ <> 100 OR EFFECT_SCOPE <> N'immediate'))
    THROW 51975, N'判官参数的域/顺序/生效方式不符（应为 JUDGE / 100 / immediate），迁移终止。', 1;

IF EXISTS (SELECT 1 FROM dbo.SYS_PARAMETER
           WHERE OWNER_MODULE = 3105 AND PARAM_KEY LIKE N'JUDGE[_]SAMPLE[_]RATE[_]PCT'
             AND (TRY_CONVERT(INT, DEFAULT_VALUE) IS NULL
                  OR TRY_CONVERT(INT, DEFAULT_VALUE) NOT BETWEEN 0 AND 100))
    THROW 51976, N'判官抽样比例的类型/取值不符（应为 0–100 的整数），迁移终止。', 1;

IF EXISTS (SELECT GROUP_CODE, SEQ_NO FROM dbo.SYS_PARAMETER
           WHERE OWNER_MODULE = 3105 AND GROUP_CODE = N'JUDGE'
           GROUP BY GROUP_CODE, SEQ_NO HAVING COUNT(*) > 1)
    THROW 51977, N'判官参数的组内序号重复，迁移终止。', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.SYS_PARAMETER
               WHERE OWNER_MODULE = 3105 AND GROUP_CODE = N'JUDGE' HAVING COUNT(*) >= 12)
    THROW 51978, N'判官参数不足 12 条，迁移终止。', 1;

COMMIT TRANSACTION;

PRINT N'== 收口：判定结论表已建，判官参数（域 JUDGE，12 条）已并入 dbo.SYS_PARAMETER（3105）==';
