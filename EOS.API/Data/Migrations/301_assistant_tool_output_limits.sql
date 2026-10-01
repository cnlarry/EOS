-- ============================================================================
-- EOS.ERP migration 301: 助手参数第六批——**工具输出上限**（10 条）
-- ----------------------------------------------------------------------------
--  来源：ADR-030 §5.3.3、§10 批 E2。
--
--  这批搬的是 7 个"读"工具的返回上限（行数 / 列数 / 截断长度）。与前面五批不同的地方在于：
--  这些数字**原先也写在工具发给模型的声明文本里**——SearchRecordsTool.Description 里就有
--  "返回前 5 行及每行主键值数组 _keys"、ParametersJson 里也有"行数上限见声明"。
--
--  所以接线的落点有两个，缺一不可：
--    · 工具执行侧（Tools/*.cs）——按参数截断；
--    · AssistantToolRegistry ——**按参数生成声明文本**里的那一句上限。
--  只接前者会让"参数改成 10 行、模型看到的说明还是 5 行"，模型照旧按 5 行规划。
--  工具的 Description 因此不再写具体数字：数字只在注册表那一句里出现一次。
--
--  默认值取自参数目录里那份唯一的代码默认值（AssistantToolLimitsOptions 的属性初始值），
--  连库门禁逐字段比对，两边不可能各说各话。
--
--  幂等：建行按 (OWNER_MODULE, PARAM_KEY) 判存在；可重复执行。
-- ============================================================================
SET NOCOUNT ON;
SET XACT_ABORT ON;

IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, N'本脚本只能在 EOS.ERP 数据库内执行。', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.MODULES WHERE M_IDX = 3105)
    THROW 50800, N'模块 3105 助手设置不存在，迁移中止（先跑迁移 293）。', 1;

BEGIN TRANSACTION;

DECLARE @rows TABLE (
    PARAM_KEY NVARCHAR(64), SEQ_NO INT, DEFAULT_VALUE NVARCHAR(4000), DESC_TEXT NVARCHAR(1000));

INSERT INTO @rows (PARAM_KEY, SEQ_NO, DEFAULT_VALUE, DESC_TEXT) VALUES
    (N'TOOL_LIMIT_SEARCH_MAX_ROWS',          10, N'5',   N'search_records 一次最多返回几行；同时写进发给模型的工具说明'),
    (N'TOOL_LIMIT_SEARCH_MAX_COLUMNS',       20, N'8',   N'search_records 每行最多带几列'),
    (N'TOOL_LIMIT_SEARCH_MAX_VALUE_LENGTH',  30, N'40',  N'search_records 每个字段值截断到多少字符（列表视图的语义，不是详情）'),
    (N'TOOL_LIMIT_DETAIL_MAX_COLUMNS',       40, N'24',  N'get_record_detail 单行最多带几列'),
    (N'TOOL_LIMIT_DETAIL_MAX_VALUE_LENGTH',  50, N'200', N'get_record_detail 每个字段值截断到多少字符'),
    (N'TOOL_LIMIT_DRAFT_MAX_VALUE_LENGTH',   60, N'500', N'draft_record 试算时单个字段值截断到多少字符；截断会随试算结果一起告警'),
    (N'TOOL_LIMIT_DESCRIBE_MAX_FIELDS',      70, N'80',  N'describe_module 每张表最多列出几个字段'),
    (N'TOOL_LIMIT_LIST_MODULES_MAX',         80, N'50',  N'list_modules 一次最多返回几个模块'),
    (N'TOOL_LIMIT_LIST_CAPABILITIES_MAX',    90, N'50',  N'list_my_capabilities 一次最多返回几个模块'),
    (N'TOOL_LIMIT_FIELD_RELATIONS_MAX',     100, N'50',  N'get_field_relations 一次最多返回几条关系');

INSERT INTO dbo.SYSSS (
    OWNER_MODULE, PARAM_KEY, PARAM_VALUE, VALUE_TYPE, DEFAULT_VALUE,
    GROUP_CODE, GROUP_LABEL, GROUP_SEQ, SEQ_NO, DESC_TEXT, EFFECT_SCOPE, REMARK,
    CREATE_PERSON, CREATE_DATE)
SELECT
    3105, r.PARAM_KEY, NULL, N'int', r.DEFAULT_VALUE,
    N'TOOL_LIMIT', N'工具输出', 30, r.SEQ_NO, r.DESC_TEXT, N'immediate', NULL,
    N'mig-301', SYSDATETIME()
FROM @rows r
WHERE NOT EXISTS (
    SELECT 1 FROM dbo.SYSSS x WHERE x.OWNER_MODULE = 3105 AND x.PARAM_KEY = r.PARAM_KEY);

PRINT N'== 工具输出上限建行：' + CONVERT(NVARCHAR(10), @@ROWCOUNT) + N' 条（表内已有则跳过）==';

/* ---------- 断言 ---------- */
-- 六批合计 106 条（9 + 36 + 27 + 3 + 21 + 10）——到这一批，目标态全部落库。
IF NOT EXISTS (SELECT 1 FROM dbo.SYSSS WHERE OWNER_MODULE = 3105 HAVING COUNT(*) = 106)
    THROW 50801, N'助手参数条数不是 106（9 + 36 + 27 + 3 + 21 + 10），迁移中止。', 1;

IF EXISTS (SELECT 1 FROM @rows r
           WHERE NOT EXISTS (SELECT 1 FROM dbo.SYSSS s
                             WHERE s.OWNER_MODULE = 3105 AND s.PARAM_KEY = r.PARAM_KEY))
    THROW 50802, N'有工具输出上限未落库，迁移中止。', 1;

-- 一律 int、立即生效、取值列留空（= 用默认值）
IF EXISTS (SELECT 1 FROM @rows r
           JOIN dbo.SYSSS s ON s.OWNER_MODULE = 3105 AND s.PARAM_KEY = r.PARAM_KEY
           WHERE s.VALUE_TYPE <> N'int' OR s.EFFECT_SCOPE <> N'immediate'
              OR s.DEFAULT_VALUE IS NULL OR s.PARAM_VALUE IS NOT NULL)
    THROW 50803, N'工具输出上限的类型 / 生效范围 / 默认值形态不符（应为 int、immediate、取值留空）。', 1;

-- 上限必须为正：0 会让工具"什么都返回不了"，而它看起来像个正常配置
IF EXISTS (SELECT 1 FROM dbo.SYSSS
           WHERE OWNER_MODULE = 3105 AND PARAM_KEY LIKE N'TOOL[_]LIMIT[_]%'
             AND TRY_CAST(DEFAULT_VALUE AS INT) <= 0)
    THROW 50804, N'工具输出上限必须为正整数，迁移中止。', 1;

COMMIT TRANSACTION;

PRINT N'== 收口：工具输出上限已并入 dbo.SYSSS（3105 合计 106 条，目标态闭合）==';
