-- ============================================================================
-- EOS.ERP migration 303: 助手消息的**用户反馈**与**截断原因**
-- ----------------------------------------------------------------------------
--  背景（两条都是"数据本来经过手上、却没留下来"的那类缺口）：
--
--  ① 反馈：助手的质量此前只能靠人工抽样评估（正确率 / 拒答率），库内**一条真实反馈都没有**。
--     赞 / 踩是比"事后抽样"便宜得多、也真实得多的数据源：用户点一下就是一条带上下文的标注。
--     没有它，"哪类回答不可信"只能靠猜。
--
--  ② 截断：模型因输出上限结束时会给出 finish_reason = length。这个字段一直在流里被解析，
--     却从未被消费——用户看到的是一段"突然结束"的回答，而真相是它还没写完。
--     落库是为了让重新打开会话时这条提示仍在（只靠当次流式事件，刷新就没了）。
--
--  两条都属于 ASSISTANT_MESSAGE（一行 = 一条消息），所以加在同一张表上。
--
--  口径：
--   · FEEDBACK 取 -1 / 1 两个值（用 SMALLINT：TINYINT 存不下 -1；0 与 NULL 同义，
--     不设"中立"这一档——点了取消就是没反馈，不必再存一个 0）；
--   · FEEDBACK_REASON 只在踩时可能有值，是**受控短文本**（前端给固定选项，服务端限长）；
--   · 三列都可空：历史消息天然没有反馈，不是"未同步"；
--   · FINISH_REASON 原样存厂商语义（length / stop / tool_calls），不在这里解释。
--
--  幂等：按 sys.columns 判列是否存在；可重复执行。
-- ============================================================================
SET NOCOUNT ON;
SET XACT_ABORT ON;

IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, N'本脚本只能在 EOS.ERP 数据库内执行。', 1;

IF OBJECT_ID(N'dbo.ASSISTANT_MESSAGE', N'U') IS NULL
    THROW 50610, N'表 dbo.ASSISTANT_MESSAGE 不存在，迁移中止（先跑建表迁移）。', 1;

BEGIN TRANSACTION;

IF NOT EXISTS (SELECT 1 FROM sys.columns
               WHERE object_id = OBJECT_ID(N'dbo.ASSISTANT_MESSAGE') AND name = N'FEEDBACK')
BEGIN
    ALTER TABLE dbo.ASSISTANT_MESSAGE ADD FEEDBACK SMALLINT NULL;
    PRINT N'== 新增列 ASSISTANT_MESSAGE.FEEDBACK ==';
END

IF NOT EXISTS (SELECT 1 FROM sys.columns
               WHERE object_id = OBJECT_ID(N'dbo.ASSISTANT_MESSAGE') AND name = N'FEEDBACK_REASON')
BEGIN
    ALTER TABLE dbo.ASSISTANT_MESSAGE ADD FEEDBACK_REASON NVARCHAR(200) NULL;
    PRINT N'== 新增列 ASSISTANT_MESSAGE.FEEDBACK_REASON ==';
END

IF NOT EXISTS (SELECT 1 FROM sys.columns
               WHERE object_id = OBJECT_ID(N'dbo.ASSISTANT_MESSAGE') AND name = N'FEEDBACK_AT')
BEGIN
    ALTER TABLE dbo.ASSISTANT_MESSAGE ADD FEEDBACK_AT DATETIME2(3) NULL;
    PRINT N'== 新增列 ASSISTANT_MESSAGE.FEEDBACK_AT ==';
END

IF NOT EXISTS (SELECT 1 FROM sys.columns
               WHERE object_id = OBJECT_ID(N'dbo.ASSISTANT_MESSAGE') AND name = N'FINISH_REASON')
BEGIN
    ALTER TABLE dbo.ASSISTANT_MESSAGE ADD FINISH_REASON NVARCHAR(32) NULL;
    PRINT N'== 新增列 ASSISTANT_MESSAGE.FINISH_REASON ==';
END

/* 取值约束：-1 / 1 两个方向；0 与 NULL 同义，故不放进允许集。
   走动态语句：同批次里引用刚 ADD 的列，编译期解析不到（Msg 207），
   而这条约束要么放在这个批次里、要么多切一个批次——动态语句两种代价都没有。 */
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints
               WHERE parent_object_id = OBJECT_ID(N'dbo.ASSISTANT_MESSAGE')
                 AND name = N'CK_ASSISTANT_MESSAGE_FEEDBACK')
BEGIN
    EXEC(N'ALTER TABLE dbo.ASSISTANT_MESSAGE WITH CHECK
           ADD CONSTRAINT CK_ASSISTANT_MESSAGE_FEEDBACK CHECK (FEEDBACK IS NULL OR FEEDBACK IN (-1, 1));');
    PRINT N'== 新增约束 CK_ASSISTANT_MESSAGE_FEEDBACK ==';
END

/* ---------- 断言 ---------- */
IF EXISTS (SELECT c.name FROM sys.columns c
           WHERE c.object_id = OBJECT_ID(N'dbo.ASSISTANT_MESSAGE')
             AND c.name IN (N'FEEDBACK', N'FEEDBACK_REASON', N'FEEDBACK_AT', N'FINISH_REASON')
             AND c.is_nullable = 0)
    THROW 50611, N'反馈与截断列必须可空（历史消息天然没有这些值），迁移中止。', 1;

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints
               WHERE parent_object_id = OBJECT_ID(N'dbo.ASSISTANT_MESSAGE')
                 AND name = N'CK_ASSISTANT_MESSAGE_FEEDBACK')
    THROW 50612, N'反馈取值约束未建立，迁移中止。', 1;

COMMIT TRANSACTION;

PRINT N'== 收口：ASSISTANT_MESSAGE 已具备反馈（-1/1 + 原因）与截断原因 ==';
