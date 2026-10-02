-- ============================================================================
-- EOS.ERP migration 304: 助手参数第五批——**报表只读工具的开关与输出上限**（6 条）
-- ----------------------------------------------------------------------------
--  来源：助手能力面补缺（报表域此前完全没有工具：元数据探查 8 个、业务能力面几乎为零）。
--
--  新增两个**只读**工具：
--    · list_reports —— 按模块列当前用户可见的报表（复用 REPORT_TAG 可见性口径）
--    · run_report   —— 按报表编号取数据（归属模块由服务端解析，复用报表仓储链路）
--
--  本批交付的仍是四件套：
--    · 目录条目 —— AssistantParameterCatalog 的 CAPABILITY 工具开关（由 AssistantToolKeys
--                  机械生成，本迁移只负责落库行）与 TOOL_LIMIT 域四个上限；
--    · 库行     —— 本脚本；
--    · 消费改造 —— ReportTools.cs 按上限截断；AssistantToolRegistry 依参数生成声明文本；
--    · 门禁     —— 目录 ↔ 库（连库单测）、工具名 ↔ 开关（离线单测）、工具名 ↔ 中文标题。
--
--  键名与序号：
--    · 工具开关 = TOOL_ + 工具名转大写（AssistantToolKeys.ParameterKeyOf），接在既有 27 个之后；
--    · 组内序号一律**追加在末尾**：连库门禁逐字段比对 SEQ_NO，插在中间会让已有行与目录对不上。
--
--  默认值：开关全开（'1'，与其余工具同口径）；上限取"够用但不烧 token"的量级。
--
--  幂等：按 (OWNER_MODULE, PARAM_KEY) 判存在；可重复执行。
-- ============================================================================
SET NOCOUNT ON;
SET XACT_ABORT ON;

IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, N'本脚本只能在 EOS.ERP 数据库内执行。', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.MODULES WHERE M_IDX = 3105)
    THROW 50620, N'模块 3105 助手设置不存在，迁移中止（先跑迁移 293）。', 1;

BEGIN TRANSACTION;

DECLARE @items TABLE (
    SEQ_NO INT, PARAM_KEY NVARCHAR(64), GROUP_CODE NVARCHAR(32), GROUP_LABEL NVARCHAR(64),
    GROUP_SEQ INT, VALUE_TYPE NVARCHAR(16), DEFAULT_VALUE NVARCHAR(200), DESC_TEXT NVARCHAR(400));

/* 域 CAPABILITY：工具开关（组内序号接在 27 个工具（50–310）与动作族（320–340）之后） */
INSERT INTO @items VALUES
    (350, N'TOOL_LIST_REPORTS', N'CAPABILITY', N'能力面', 40, N'bit', N'1',
     N'关掉之后模型看不到「列出模块报表」（list_reports）这个工具，也调不动它（幻觉出一个已关闭的工具名会被明确拒绝，而不是当成工具不存在）。'),
    (360, N'TOOL_RUN_REPORT', N'CAPABILITY', N'能力面', 40, N'bit', N'1',
     N'关掉之后模型看不到「取报表数据」（run_report）这个工具，也调不动它（幻觉出一个已关闭的工具名会被明确拒绝，而不是当成工具不存在）。');

/* 域 TOOL_LIMIT：输出上限（接在既有 10 条之后） */
INSERT INTO @items VALUES
    (110, N'TOOL_LIMIT_REPORT_LIST_MAX', N'TOOL_LIMIT', N'工具输出', 30, N'int', N'20',
     N'list_reports 一次最多列出几个报表。'),
    (120, N'TOOL_LIMIT_REPORT_MAX_ROWS', N'TOOL_LIMIT', N'工具输出', 30, N'int', N'20',
     N'run_report 一次最多带回几行；同时写进发给模型的工具说明。'),
    (130, N'TOOL_LIMIT_REPORT_MAX_COLUMNS', N'TOOL_LIMIT', N'工具输出', 30, N'int', N'12',
     N'run_report 每行最多带几列。'),
    (140, N'TOOL_LIMIT_REPORT_MAX_VALUE_LENGTH', N'TOOL_LIMIT', N'工具输出', 30, N'int', N'40',
     N'run_report 每个单元格截断到多少字符。');

INSERT INTO dbo.SYSSS (
    OWNER_MODULE, PARAM_KEY, PARAM_VALUE, VALUE_TYPE, DEFAULT_VALUE,
    GROUP_CODE, GROUP_LABEL, GROUP_SEQ, SEQ_NO, DESC_TEXT, EFFECT_SCOPE, REMARK,
    CREATE_PERSON, CREATE_DATE)
SELECT
    3105, i.PARAM_KEY, NULL, i.VALUE_TYPE, i.DEFAULT_VALUE,
    i.GROUP_CODE, i.GROUP_LABEL, i.GROUP_SEQ, i.SEQ_NO, i.DESC_TEXT, N'immediate', NULL,
    N'mig-304', SYSDATETIME()
FROM @items i
WHERE NOT EXISTS (
    SELECT 1 FROM dbo.SYSSS x WHERE x.OWNER_MODULE = 3105 AND x.PARAM_KEY = i.PARAM_KEY);

PRINT N'== 报表工具参数建行：' + CONVERT(NVARCHAR(10), @@ROWCOUNT) + N' 条（表内已有则跳过）==';

/* ---------- 断言 ---------- */
IF EXISTS (SELECT 1 FROM @items i
           WHERE NOT EXISTS (SELECT 1 FROM dbo.SYSSS s
                             WHERE s.OWNER_MODULE = 3105 AND s.PARAM_KEY = i.PARAM_KEY))
    THROW 50621, N'有报表工具参数未落库，迁移中止。', 1;

-- 工具开关：bit 且默认开。写成"默认关"会让新增能力一上线就不可用
IF EXISTS (SELECT 1 FROM dbo.SYSSS
           WHERE OWNER_MODULE = 3105
             AND PARAM_KEY IN (N'TOOL_LIST_REPORTS', N'TOOL_RUN_REPORT')
             AND (VALUE_TYPE <> N'bit' OR DEFAULT_VALUE <> N'1'))
    THROW 50622, N'报表工具开关的类型 / 默认值不符（应为 bit、默认 1），迁移中止。', 1;

-- 上限：int 且为正数（0 或负数会让输出直接空掉，不是"不限制"）
IF EXISTS (SELECT 1 FROM dbo.SYSSS
           WHERE OWNER_MODULE = 3105
             AND PARAM_KEY LIKE N'TOOL_LIMIT_REPORT[_]%'
             AND (VALUE_TYPE <> N'int' OR TRY_CONVERT(INT, DEFAULT_VALUE) IS NULL
                  OR TRY_CONVERT(INT, DEFAULT_VALUE) <= 0))
    THROW 50623, N'报表输出上限的类型 / 默认值不符（应为正整数），迁移中止。', 1;

-- 组内序号唯一（同组重复会让页面顺序不稳、目录与库比对失败）
IF EXISTS (SELECT GROUP_CODE, SEQ_NO FROM dbo.SYSSS
           WHERE OWNER_MODULE = 3105
           GROUP BY GROUP_CODE, SEQ_NO HAVING COUNT(*) > 1)
    THROW 50624, N'助手参数的组内序号重复，迁移中止。', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.SYSSS
               WHERE OWNER_MODULE = 3105 HAVING COUNT(*) = 113)
    THROW 50625, N'助手参数条数不是 113（107 + 6），迁移中止。', 1;

COMMIT TRANSACTION;

PRINT N'== 收口：报表只读工具的开关与输出上限已并入 dbo.SYSSS（3105 合计 113 条）==';
