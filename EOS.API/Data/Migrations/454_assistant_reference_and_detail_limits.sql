-- ============================================================================
-- EOS.ERP migration 454: 助手读面补强——**明细行**与**引用解析**的输出上限/开关（5 条）
-- ----------------------------------------------------------------------------
--  配合 P0「读得全、查得准」：
--    · get_record_detail 带回单据明细行（行数 / 每行列数 / 单值长度三个上限）；
--    · 新增只读工具 resolve_reference（模块字段的选择器来源 → 候选编码/名称）。
--
--  与既有批次同一套口径：
--    · 工具开关键名 = TOOL_ + 工具名转大写，声明位置排在能力面组末；
--    · 组内序号一律追加在末尾——已有行与目录的 SEQ_NO 不得因新增而整体后移；
--    · 默认值取自代码默认值（AssistantToolLimitsOptions 属性初始值），不写第二份数字。
--
--  幂等：按 (OWNER_MODULE, PARAM_KEY) 判存在；可重复执行。
-- ============================================================================
SET NOCOUNT ON;
SET XACT_ABORT ON;

IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, N'本脚本只能在 EOS.ERP 数据库内执行。', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.MODULES WHERE M_IDX = 3105)
    THROW 50810, N'模块 3105 助手设置不存在，迁移中止（先跑迁移 293）。', 1;

BEGIN TRANSACTION;

DECLARE @items TABLE (
    SEQ_NO INT, PARAM_KEY NVARCHAR(64), GROUP_CODE NVARCHAR(32), GROUP_LABEL NVARCHAR(64),
    GROUP_SEQ INT, VALUE_TYPE NVARCHAR(16), DEFAULT_VALUE NVARCHAR(200), DESC_TEXT NVARCHAR(400));

INSERT INTO @items VALUES
    /* 域 CAPABILITY：新工具开关（接在既有的能力面组末） */
    (390, N'TOOL_RESOLVE_REFERENCE', N'CAPABILITY', N'能力面', 40, N'bit', N'1',
     N'关掉之后模型看不到「解析引用值」（resolve_reference）这个工具，也调不动它（幻觉出一个已关闭的工具名会被明确拒绝，而不是当成工具不存在）。'),
    /* 域 TOOL_LIMIT：明细行与引用候选的输出上限（接在既有上限之后） */
    (180, N'TOOL_LIMIT_DETAIL_MAX_ROWS', N'TOOL_LIMIT', N'工具输出', 30, N'int', N'50',
     N'get_record_detail 明细最多带回几行；超出时如实说明还有多少行未展示。'),
    (190, N'TOOL_LIMIT_DETAIL_ROW_COLUMNS', N'TOOL_LIMIT', N'工具输出', 30, N'int', N'8',
     N'get_record_detail 明细每行最多带几列。'),
    (200, N'TOOL_LIMIT_DETAIL_ROW_VALUE_LENGTH', N'TOOL_LIMIT', N'工具输出', 30, N'int', N'40',
     N'get_record_detail 明细每个字段值截断到多少字符。'),
    (210, N'TOOL_LIMIT_REFERENCE_MAX', N'TOOL_LIMIT', N'工具输出', 30, N'int', N'10',
     N'resolve_reference 一次最多返回几个候选；超出时提示用户补充关键字。');

INSERT INTO dbo.SYS_PARAMETER (
    OWNER_MODULE, PARAM_KEY, PARAM_VALUE, VALUE_TYPE, DEFAULT_VALUE,
    GROUP_CODE, GROUP_LABEL, GROUP_SEQ, SEQ_NO, DESC_TEXT, EFFECT_SCOPE, REMARK,
    CREATED_BY, CREATED_TIME)
SELECT
    3105, i.PARAM_KEY, NULL, i.VALUE_TYPE, i.DEFAULT_VALUE,
    i.GROUP_CODE, i.GROUP_LABEL, i.GROUP_SEQ, i.SEQ_NO, i.DESC_TEXT, N'immediate', NULL,
    N'mig-454', SYSDATETIME()
FROM @items i
WHERE NOT EXISTS (
    SELECT 1 FROM dbo.SYS_PARAMETER x WHERE x.OWNER_MODULE = 3105 AND x.PARAM_KEY = i.PARAM_KEY);

PRINT N'== 明细行与引用解析参数建行：' + CONVERT(NVARCHAR(10), @@ROWCOUNT) + N' 条（表内已有则跳过）==';

/* ---------- 断言 ---------- */
IF EXISTS (SELECT 1 FROM @items i
           WHERE NOT EXISTS (SELECT 1 FROM dbo.SYS_PARAMETER s
                             WHERE s.OWNER_MODULE = 3105 AND s.PARAM_KEY = i.PARAM_KEY))
    THROW 50811, N'有明细行 / 引用解析参数未落库，迁移中止。', 1;

IF EXISTS (SELECT 1 FROM dbo.SYS_PARAMETER
           WHERE OWNER_MODULE = 3105 AND PARAM_KEY = N'TOOL_RESOLVE_REFERENCE'
             AND (VALUE_TYPE <> N'bit' OR DEFAULT_VALUE <> N'1'))
    THROW 50812, N'引用解析工具开关的类型 / 默认值不符（应为 bit、默认 1），迁移中止。', 1;

IF EXISTS (SELECT 1 FROM dbo.SYS_PARAMETER
           WHERE OWNER_MODULE = 3105 AND PARAM_KEY LIKE N'TOOL[_]LIMIT[_]%'
             AND (VALUE_TYPE <> N'int' OR TRY_CONVERT(INT, DEFAULT_VALUE) IS NULL
                  OR TRY_CONVERT(INT, DEFAULT_VALUE) <= 0))
    THROW 50813, N'工具输出上限的类型 / 默认值不符（应为正整数），迁移中止。', 1;

IF EXISTS (SELECT GROUP_CODE, SEQ_NO FROM dbo.SYS_PARAMETER
           WHERE OWNER_MODULE = 3105
           GROUP BY GROUP_CODE, SEQ_NO HAVING COUNT(*) > 1)
    THROW 50814, N'助手参数的组内序号重复，迁移中止。', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.SYS_PARAMETER
               WHERE OWNER_MODULE = 3105 HAVING COUNT(*) = 124)
    THROW 50815, N'助手参数条数不是 124（119 + 5），迁移中止。', 1;

COMMIT TRANSACTION;

PRINT N'== 收口：明细行与引用解析参数已并入 dbo.SYS_PARAMETER（3105 合计 124 条）==';
