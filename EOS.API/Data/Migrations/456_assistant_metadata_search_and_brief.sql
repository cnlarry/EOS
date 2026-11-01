-- ============================================================================
-- EOS.ERP migration 456: 助手元数据检索 + 模块简报（3 条）
-- ----------------------------------------------------------------------------
--  配合「找得到」与「当前页面看得懂」：
--    · 新工具 search_metadata：跨模块/表/字段/自定义动作的关键字检索，结果按当前用户可浏览模块过滤；
--    · 模块简报：页面处境有 moduleId 时自动注入主表/明细表与关键字段（只读元数据快照）。
--
--  本批只加两个输出上限与一个工具开关；工具声明文本按上限生成，与执行侧同源。
--  幂等：按 (OWNER_MODULE, PARAM_KEY) 判存在；可重复执行。
-- ============================================================================
SET NOCOUNT ON;
SET XACT_ABORT ON;

IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, N'本脚本只能在 EOS.ERP 数据库内执行。', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.MODULES WHERE M_IDX = 3105)
    THROW 50830, N'模块 3105 助手设置不存在，迁移中止（先跑迁移 293）。', 1;

BEGIN TRANSACTION;

DECLARE @items TABLE (
    SEQ_NO INT, PARAM_KEY NVARCHAR(64), GROUP_CODE NVARCHAR(32), GROUP_LABEL NVARCHAR(64),
    GROUP_SEQ INT, VALUE_TYPE NVARCHAR(16), DEFAULT_VALUE NVARCHAR(200), DESC_TEXT NVARCHAR(400));

INSERT INTO @items VALUES
    (430, N'TOOL_SEARCH_METADATA', N'CAPABILITY', N'能力面', 40, N'bit', N'1',
     N'关掉之后模型看不到「检索模块/表/字段」（search_metadata），也调不动它。'),
    (220, N'TOOL_LIMIT_METADATA_SEARCH_MAX', N'TOOL_LIMIT', N'工具输出', 30, N'int', N'20',
     N'search_metadata 一次最多返回几条命中（模块/表/字段/动作合计）。'),
    (230, N'TOOL_LIMIT_BRIEF_MAX_FIELDS', N'TOOL_LIMIT', N'工具输出', 30, N'int', N'24',
     N'页面处境有模块时，自动注入的模块简报每张表最多列几个字段。');

INSERT INTO dbo.SYS_PARAMETER (
    OWNER_MODULE, PARAM_KEY, PARAM_VALUE, VALUE_TYPE, DEFAULT_VALUE,
    GROUP_CODE, GROUP_LABEL, GROUP_SEQ, SEQ_NO, DESC_TEXT, EFFECT_SCOPE, REMARK,
    CREATED_BY, CREATED_TIME)
SELECT
    3105, i.PARAM_KEY, NULL, i.VALUE_TYPE, i.DEFAULT_VALUE,
    i.GROUP_CODE, i.GROUP_LABEL, i.GROUP_SEQ, i.SEQ_NO, i.DESC_TEXT, N'immediate', NULL,
    N'mig-456', SYSDATETIME()
FROM @items i
WHERE NOT EXISTS (
    SELECT 1 FROM dbo.SYS_PARAMETER x WHERE x.OWNER_MODULE = 3105 AND x.PARAM_KEY = i.PARAM_KEY);

PRINT N'== 元数据检索与模块简报参数建行：' + CONVERT(NVARCHAR(10), @@ROWCOUNT) + N' 条（表内已有则跳过）==';

/* ---------- 断言 ---------- */
IF EXISTS (SELECT 1 FROM @items i
           WHERE NOT EXISTS (SELECT 1 FROM dbo.SYS_PARAMETER s
                             WHERE s.OWNER_MODULE = 3105 AND s.PARAM_KEY = i.PARAM_KEY))
    THROW 50831, N'有元数据检索 / 模块简报参数未落库，迁移中止。', 1;

IF EXISTS (SELECT 1 FROM dbo.SYS_PARAMETER
           WHERE OWNER_MODULE = 3105 AND PARAM_KEY = N'TOOL_SEARCH_METADATA'
             AND (VALUE_TYPE <> N'bit' OR DEFAULT_VALUE <> N'1'))
    THROW 50832, N'元数据检索工具开关的类型 / 默认值不符（应为 bit、默认 1），迁移中止。', 1;

IF EXISTS (SELECT 1 FROM dbo.SYS_PARAMETER
           WHERE OWNER_MODULE = 3105
             AND PARAM_KEY IN (N'TOOL_LIMIT_METADATA_SEARCH_MAX', N'TOOL_LIMIT_BRIEF_MAX_FIELDS')
             AND (VALUE_TYPE <> N'int' OR TRY_CONVERT(INT, DEFAULT_VALUE) IS NULL
                  OR TRY_CONVERT(INT, DEFAULT_VALUE) <= 0))
    THROW 50833, N'元数据检索 / 模块简报上限的类型或默认值不符（应为正整数），迁移中止。', 1;

IF EXISTS (SELECT GROUP_CODE, SEQ_NO FROM dbo.SYS_PARAMETER
           WHERE OWNER_MODULE = 3105
           GROUP BY GROUP_CODE, SEQ_NO HAVING COUNT(*) > 1)
    THROW 50834, N'助手参数的组内序号重复，迁移中止。', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.SYS_PARAMETER
               WHERE OWNER_MODULE = 3105 HAVING COUNT(*) = 130)
    THROW 50835, N'助手参数条数不是 130（127 + 3），迁移中止。', 1;

COMMIT TRANSACTION;

PRINT N'== 收口：元数据检索与模块简报参数已并入 dbo.SYS_PARAMETER（3105 合计 130 条）==';
