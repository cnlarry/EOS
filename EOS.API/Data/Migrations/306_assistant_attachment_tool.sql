-- ============================================================================
-- EOS.ERP migration 306: 助手参数第七批——**附件清单工具**的开关与输出上限（2 条）
-- ----------------------------------------------------------------------------
--  新增只读工具 `list_attachments`：一张单据的附件**元数据**清单
--  （文件名 / 大小 / 上传人 / 上传时间 / 备注），复用附件仓储的既有查询。
--
--  它补的是"这张单有没有附件、附的是什么"这一格：`ATTACHMENT` 表与上传下载链路早已齐备，
--  而助手此前无从知道它们的存在。
--
--  权限口径与附件端点一致：CanBrowse + FILE_VIEW（能看单据不等于能看它的附件）；
--  只给元数据，**不下发文件内容**（内容要走附件对话框，那条路径有自己的权限与审计）。
--
--  本批交付的仍是四件套：
--    · 目录条目 —— 工具开关（由 AssistantToolKeys 机械生成）+ TOOL_LIMIT 域一条上限；
--    · 库行     —— 本脚本；
--    · 消费改造 —— AttachmentListTool 按上限截断；AssistantToolRegistry 依参数生成声明文本；
--    · 门禁     —— 目录 ↔ 库（连库单测）、工具名 ↔ 开关与中文标题（离线单测）。
--
--  键名与序号：工具开关 = TOOL_ + 工具名转大写，声明位置排在能力面**组末**；
--  组内序号一律**追加在末尾**（连库门禁逐字段比对 SEQ_NO，重排会让已有行与目录对不上）。
--
--  幂等：按 (OWNER_MODULE, PARAM_KEY) 判存在；可重复执行。
-- ============================================================================
SET NOCOUNT ON;
SET XACT_ABORT ON;

IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, N'本脚本只能在 EOS.ERP 数据库内执行。', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.MODULES WHERE M_IDX = 3105)
    THROW 50640, N'模块 3105 助手设置不存在，迁移中止（先跑迁移 293）。', 1;

BEGIN TRANSACTION;

DECLARE @items TABLE (
    SEQ_NO INT, PARAM_KEY NVARCHAR(64), GROUP_CODE NVARCHAR(32), GROUP_LABEL NVARCHAR(64),
    GROUP_SEQ INT, VALUE_TYPE NVARCHAR(16), DEFAULT_VALUE NVARCHAR(200), DESC_TEXT NVARCHAR(400));

INSERT INTO @items VALUES
    /* 域 CAPABILITY：工具开关（接在既有的单据历史开关之后） */
    (380, N'TOOL_LIST_ATTACHMENTS', N'CAPABILITY', N'能力面', 40, N'bit', N'1',
     N'关掉之后模型看不到「查看单据附件清单」（list_attachments）这个工具，也调不动它（幻觉出一个已关闭的工具名会被明确拒绝，而不是当成工具不存在）。'),
    /* 域 TOOL_LIMIT：输出上限（接在既有上限之后） */
    (170, N'TOOL_LIMIT_ATTACHMENT_LIST_MAX', N'TOOL_LIMIT', N'工具输出', 30, N'int', N'20',
     N'list_attachments 一次最多列出几个附件；同时写进发给模型的工具说明。');

INSERT INTO dbo.SYSSS (
    OWNER_MODULE, PARAM_KEY, PARAM_VALUE, VALUE_TYPE, DEFAULT_VALUE,
    GROUP_CODE, GROUP_LABEL, GROUP_SEQ, SEQ_NO, DESC_TEXT, EFFECT_SCOPE, REMARK,
    CREATE_PERSON, CREATE_DATE)
SELECT
    3105, i.PARAM_KEY, NULL, i.VALUE_TYPE, i.DEFAULT_VALUE,
    i.GROUP_CODE, i.GROUP_LABEL, i.GROUP_SEQ, i.SEQ_NO, i.DESC_TEXT, N'immediate', NULL,
    N'mig-306', SYSDATETIME()
FROM @items i
WHERE NOT EXISTS (
    SELECT 1 FROM dbo.SYSSS x WHERE x.OWNER_MODULE = 3105 AND x.PARAM_KEY = i.PARAM_KEY);

PRINT N'== 附件清单工具参数建行：' + CONVERT(NVARCHAR(10), @@ROWCOUNT) + N' 条（表内已有则跳过）==';

/* ---------- 断言 ---------- */
IF EXISTS (SELECT 1 FROM @items i
           WHERE NOT EXISTS (SELECT 1 FROM dbo.SYSSS s
                             WHERE s.OWNER_MODULE = 3105 AND s.PARAM_KEY = i.PARAM_KEY))
    THROW 50641, N'有附件清单工具参数未落库，迁移中止。', 1;

IF EXISTS (SELECT 1 FROM dbo.SYSSS
           WHERE OWNER_MODULE = 3105 AND PARAM_KEY = N'TOOL_LIST_ATTACHMENTS'
             AND (VALUE_TYPE <> N'bit' OR DEFAULT_VALUE <> N'1'))
    THROW 50642, N'附件清单工具开关的类型 / 默认值不符（应为 bit、默认 1），迁移中止。', 1;

IF EXISTS (SELECT 1 FROM dbo.SYSSS
           WHERE OWNER_MODULE = 3105 AND PARAM_KEY = N'TOOL_LIMIT_ATTACHMENT_LIST_MAX'
             AND (VALUE_TYPE <> N'int' OR TRY_CONVERT(INT, DEFAULT_VALUE) IS NULL
                  OR TRY_CONVERT(INT, DEFAULT_VALUE) <= 0))
    THROW 50643, N'附件清单输出上限的类型 / 默认值不符（应为正整数），迁移中止。', 1;

IF EXISTS (SELECT GROUP_CODE, SEQ_NO FROM dbo.SYSSS
           WHERE OWNER_MODULE = 3105
           GROUP BY GROUP_CODE, SEQ_NO HAVING COUNT(*) > 1)
    THROW 50644, N'助手参数的组内序号重复，迁移中止。', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.SYSSS
               WHERE OWNER_MODULE = 3105 HAVING COUNT(*) = 118)
    THROW 50645, N'助手参数条数不是 118（116 + 2），迁移中止。', 1;

COMMIT TRANSACTION;

PRINT N'== 收口：附件清单工具的开关与上限已并入 dbo.SYSSS（3105 合计 118 条）==';
