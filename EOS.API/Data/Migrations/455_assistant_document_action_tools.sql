-- ============================================================================
-- EOS.ERP migration 455: 助手接入**自定义单据动作**——3 个工具开关
-- ----------------------------------------------------------------------------
--  新增工具（列动作 / 预演 / 执行）走既有 DocumentActionExecutor：
--    · 按钮级授权 fail-closed（没授权的动作连名字都不下发）；
--    · CONFIRM_TAG=1 的动作由服务端探路（真实事务内执行后回滚），助手只出确认卡；
--    · CONFIRM_TAG=0 的动作按工具调用身份推导幂等键后执行；
--    · 每个动作一条审计，与用户手点按钮同一段代码。
--
--  因此这一批只落"能力面开关"：三个工具各自可被管理员关闭，与既有工具同一机制。
--  幂等：按 (OWNER_MODULE, PARAM_KEY) 判存在；可重复执行。
-- ============================================================================
SET NOCOUNT ON;
SET XACT_ABORT ON;

IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, N'本脚本只能在 EOS.ERP 数据库内执行。', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.MODULES WHERE M_IDX = 3105)
    THROW 50820, N'模块 3105 助手设置不存在，迁移中止（先跑迁移 293）。', 1;

BEGIN TRANSACTION;

DECLARE @items TABLE (
    SEQ_NO INT, PARAM_KEY NVARCHAR(64), VALUE_TYPE NVARCHAR(16), DEFAULT_VALUE NVARCHAR(200), DESC_TEXT NVARCHAR(400));

INSERT INTO @items VALUES
    (400, N'TOOL_LIST_DOCUMENT_ACTIONS', N'bit', N'1',
     N'关掉之后模型看不到「列出单据动作」（list_document_actions），也调不动它。'),
    (410, N'TOOL_PREVIEW_DOCUMENT_ACTION', N'bit', N'1',
     N'关掉之后模型不能预演自定义单据动作（preview_document_action）。'),
    (420, N'TOOL_APPLY_DOCUMENT_ACTION', N'bit', N'1',
     N'关掉之后模型不能执行自定义单据动作（apply_document_action）；确认型动作仍可由用户自行点击。');

INSERT INTO dbo.SYS_PARAMETER (
    OWNER_MODULE, PARAM_KEY, PARAM_VALUE, VALUE_TYPE, DEFAULT_VALUE,
    GROUP_CODE, GROUP_LABEL, GROUP_SEQ, SEQ_NO, DESC_TEXT, EFFECT_SCOPE, REMARK,
    CREATED_BY, CREATED_TIME)
SELECT
    3105, i.PARAM_KEY, NULL, i.VALUE_TYPE, i.DEFAULT_VALUE,
    N'CAPABILITY', N'能力面', 40, i.SEQ_NO, i.DESC_TEXT, N'immediate', NULL,
    N'mig-455', SYSDATETIME()
FROM @items i
WHERE NOT EXISTS (
    SELECT 1 FROM dbo.SYS_PARAMETER x WHERE x.OWNER_MODULE = 3105 AND x.PARAM_KEY = i.PARAM_KEY);

PRINT N'== 自定义单据动作工具开关建行：' + CONVERT(NVARCHAR(10), @@ROWCOUNT) + N' 条（表内已有则跳过）==';

/* ---------- 断言 ---------- */
IF EXISTS (SELECT 1 FROM @items i
           WHERE NOT EXISTS (SELECT 1 FROM dbo.SYS_PARAMETER s
                             WHERE s.OWNER_MODULE = 3105 AND s.PARAM_KEY = i.PARAM_KEY))
    THROW 50821, N'有自定义单据动作工具开关未落库，迁移中止。', 1;

IF EXISTS (SELECT 1 FROM dbo.SYS_PARAMETER
           WHERE OWNER_MODULE = 3105 AND PARAM_KEY IN (
                 N'TOOL_LIST_DOCUMENT_ACTIONS', N'TOOL_PREVIEW_DOCUMENT_ACTION', N'TOOL_APPLY_DOCUMENT_ACTION')
             AND (VALUE_TYPE <> N'bit' OR DEFAULT_VALUE <> N'1'))
    THROW 50822, N'自定义单据动作工具开关的类型 / 默认值不符（应为 bit、默认 1），迁移中止。', 1;

IF EXISTS (SELECT GROUP_CODE, SEQ_NO FROM dbo.SYS_PARAMETER
           WHERE OWNER_MODULE = 3105
           GROUP BY GROUP_CODE, SEQ_NO HAVING COUNT(*) > 1)
    THROW 50823, N'助手参数的组内序号重复，迁移中止。', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.SYS_PARAMETER
               WHERE OWNER_MODULE = 3105 HAVING COUNT(*) = 127)
    THROW 50824, N'助手参数条数不是 127（124 + 3），迁移中止。', 1;

COMMIT TRANSACTION;

PRINT N'== 收口：自定义单据动作工具开关已并入 dbo.SYS_PARAMETER（3105 合计 127 条）==';
