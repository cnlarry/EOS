-- ============================================================================
-- EOS.ERP migration 305: 助手参数第六批——**单据历史工具**的开关与输出上限（3 条）
-- ----------------------------------------------------------------------------
--  新增只读工具 `get_record_history`：一张单据的审批时间线（WF_MYTASK_LOG / WF_APPROVE，
--  经工作流引擎既有实现）+ 最近操作（AUDIT_EVENT 的摘要素）。
--
--  它补的是诊断链的另一半：`diagnose_record` 回答"现在为什么办不下去"，这个工具回答
--  "之前发生过什么"（谁改过、审批到哪一步、上次为什么被拒）。
--
--  本批交付的仍是四件套：
--    · 目录条目 —— 工具开关（由 AssistantToolKeys 机械生成）+ TOOL_LIMIT 域两条上限；
--    · 库行     —— 本脚本；
--    · 消费改造 —— RecordHistoryTool 按上限截断；AssistantToolRegistry 依参数生成声明文本；
--    · 门禁     —— 目录 ↔ 库（连库单测）、工具名 ↔ 开关与中文标题（离线单测）。
--
--  键名与序号：
--    · 工具开关 = TOOL_ + 工具名转大写；声明位置排在能力面**组末**（见 AssistantToolKeys.AppendedToolNames：
--      组内序号按声明顺序推导，插回工具清单中间会把动作族及其后所有参数的序号整体推后）；
--    · 组内序号一律**追加在末尾**：连库门禁逐字段比对 SEQ_NO。
--
--  幂等：按 (OWNER_MODULE, PARAM_KEY) 判存在；可重复执行。
-- ============================================================================
SET NOCOUNT ON;
SET XACT_ABORT ON;

IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, N'本脚本只能在 EOS.ERP 数据库内执行。', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.MODULES WHERE M_IDX = 3105)
    THROW 50630, N'模块 3105 助手设置不存在，迁移中止（先跑迁移 293）。', 1;

BEGIN TRANSACTION;

DECLARE @items TABLE (
    SEQ_NO INT, PARAM_KEY NVARCHAR(64), GROUP_CODE NVARCHAR(32), GROUP_LABEL NVARCHAR(64),
    GROUP_SEQ INT, VALUE_TYPE NVARCHAR(16), DEFAULT_VALUE NVARCHAR(200), DESC_TEXT NVARCHAR(400));

INSERT INTO @items VALUES
    /* 域 CAPABILITY：工具开关（接在既有两个报表工具开关之后） */
    (370, N'TOOL_GET_RECORD_HISTORY', N'CAPABILITY', N'能力面', 40, N'bit', N'1',
     N'关掉之后模型看不到「查看单据历史」（get_record_history）这个工具，也调不动它（幻觉出一个已关闭的工具名会被明确拒绝，而不是当成工具不存在）。'),
    /* 域 TOOL_LIMIT：输出上限（接在报表工具的四条上限之后） */
    (150, N'TOOL_LIMIT_RECORD_HISTORY_MAX', N'TOOL_LIMIT', N'工具输出', 30, N'int', N'20',
     N'get_record_history 的审批历史与最近操作各最多带回几条；同时写进发给模型的工具说明。'),
    (160, N'TOOL_LIMIT_RECORD_ACTIVITY_DAYS', N'TOOL_LIMIT', N'工具输出', 30, N'int', N'90',
     N'get_record_history 只看最近多少天的操作记录（窗口越大越慢、越贵）。');

INSERT INTO dbo.SYSSS (
    OWNER_MODULE, PARAM_KEY, PARAM_VALUE, VALUE_TYPE, DEFAULT_VALUE,
    GROUP_CODE, GROUP_LABEL, GROUP_SEQ, SEQ_NO, DESC_TEXT, EFFECT_SCOPE, REMARK,
    CREATE_PERSON, CREATE_DATE)
SELECT
    3105, i.PARAM_KEY, NULL, i.VALUE_TYPE, i.DEFAULT_VALUE,
    i.GROUP_CODE, i.GROUP_LABEL, i.GROUP_SEQ, i.SEQ_NO, i.DESC_TEXT, N'immediate', NULL,
    N'mig-305', SYSDATETIME()
FROM @items i
WHERE NOT EXISTS (
    SELECT 1 FROM dbo.SYSSS x WHERE x.OWNER_MODULE = 3105 AND x.PARAM_KEY = i.PARAM_KEY);

PRINT N'== 单据历史工具参数建行：' + CONVERT(NVARCHAR(10), @@ROWCOUNT) + N' 条（表内已有则跳过）==';

/* ---------- 断言 ---------- */
IF EXISTS (SELECT 1 FROM @items i
           WHERE NOT EXISTS (SELECT 1 FROM dbo.SYSSS s
                             WHERE s.OWNER_MODULE = 3105 AND s.PARAM_KEY = i.PARAM_KEY))
    THROW 50631, N'有单据历史工具参数未落库，迁移中止。', 1;

IF EXISTS (SELECT 1 FROM dbo.SYSSS
           WHERE OWNER_MODULE = 3105 AND PARAM_KEY = N'TOOL_GET_RECORD_HISTORY'
             AND (VALUE_TYPE <> N'bit' OR DEFAULT_VALUE <> N'1'))
    THROW 50632, N'单据历史工具开关的类型 / 默认值不符（应为 bit、默认 1），迁移中止。', 1;

IF EXISTS (SELECT 1 FROM dbo.SYSSS
           WHERE OWNER_MODULE = 3105 AND PARAM_KEY LIKE N'TOOL_LIMIT_RECORD[_]%'
             AND (VALUE_TYPE <> N'int' OR TRY_CONVERT(INT, DEFAULT_VALUE) IS NULL
                  OR TRY_CONVERT(INT, DEFAULT_VALUE) <= 0))
    THROW 50633, N'单据历史的输出上限类型 / 默认值不符（应为正整数），迁移中止。', 1;

IF EXISTS (SELECT GROUP_CODE, SEQ_NO FROM dbo.SYSSS
           WHERE OWNER_MODULE = 3105
           GROUP BY GROUP_CODE, SEQ_NO HAVING COUNT(*) > 1)
    THROW 50634, N'助手参数的组内序号重复，迁移中止。', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.SYSSS
               WHERE OWNER_MODULE = 3105 HAVING COUNT(*) = 116)
    THROW 50635, N'助手参数条数不是 116（113 + 3），迁移中止。', 1;

COMMIT TRANSACTION;

PRINT N'== 收口：单据历史工具的开关与上限已并入 dbo.SYSSS（3105 合计 116 条）==';
