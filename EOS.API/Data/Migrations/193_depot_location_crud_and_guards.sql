-- ============================================================================
-- EOS.ERP migration 194: 库位主档可用化（CRUD 落地 + 哨兵行自动创建与保护 + 路径重算收口）
-- ----------------------------------------------------------------------------
-- 模块 110309 库位主档（MASTER_TABLE=DEPOT_LOCATION，单主档）早已注册，但没有定义快照，
-- 因此工作台里进不去；同时它的两条地基约束也没有落点：
--   ① 哨兵行（LOCATION_NO='-'）是"未指定位置"的兜底行，余额表位置外键指向它。新库别若没有它，
--      该库别的任何过账都会卡在"库位不存在"；它被停用/改父/删除，该库别的库存就失去落点。
--   ② LOCATION_PATH 是物化路径，库位改挂上级后若不同步后代，整棵子树会指向不存在的路径
--      （按区盘点、按区汇总都跟着错）。
--
-- 本迁移把三件事配到位：
--   ⒜ 给 DEPOT_LOCATION 补生命周期列（CONFIRM_TAG 等）——效果引擎接管批核的模块按发布门要求
--      主表必须有 CONFIRM_TAG，缺列会被发布校验直接拦下；
--   ⒝ 打开 110309 / 110306 的效果引擎，并挂上保存期动作（路径重算 / 哨兵自动创建）
--      与校验（哨兵形态守卫 / 哨兵删除守卫）；
--   ⒞ 标记两个模块待发布——运行时读的是已发布快照，不重发布则配置不生效。
--
-- 幂等：加列前先判存在；配置按 (MODULE_ID, STAGE/EVENT_CODE, SEQ, KEY) 合并。
-- ============================================================================

SET NOCOUNT ON;

SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET ARITHABORT ON;

DECLARE @GUARD_MESSAGE NVARCHAR(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 52100, @GUARD_MESSAGE, 1;

DECLARE @LocationModule INT = 110309;
DECLARE @DepotModule INT = 110306;

IF NOT EXISTS (SELECT 1 FROM dbo.MODULES WHERE M_IDX = @LocationModule
                 AND LTRIM(RTRIM(ISNULL(MASTER_TABLE, ''))) = N'DEPOT_LOCATION')
    THROW 52101, N'模块 110309 形态不符（应为 DEPOT_LOCATION），迁移中止。', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.MODULES WHERE M_IDX = @DepotModule
                 AND LTRIM(RTRIM(ISNULL(MASTER_TABLE, ''))) = N'DEPOT')
    THROW 52102, N'模块 110306 形态不符（应为 DEPOT），迁移中止。', 1;

/* ---------- ⒜ 生命周期列：效果引擎接管批核的模块，主表必须有 CONFIRM_TAG ---------- */
IF COL_LENGTH('dbo.DEPOT_LOCATION', 'CONFIRM_TAG') IS NULL
    ALTER TABLE dbo.DEPOT_LOCATION ADD CONFIRM_TAG BIT NOT NULL
        CONSTRAINT DF_DEPOT_LOCATION_CONFIRM_TAG DEFAULT (0);

IF COL_LENGTH('dbo.DEPOT_LOCATION', 'CONFIRM_PERSON') IS NULL
    ALTER TABLE dbo.DEPOT_LOCATION ADD CONFIRM_PERSON NCHAR(20) NULL;

IF COL_LENGTH('dbo.DEPOT_LOCATION', 'CONFIRM_DATE') IS NULL
    ALTER TABLE dbo.DEPOT_LOCATION ADD CONFIRM_DATE DATETIME NULL;

/* ---------- ⒝ 打开效果引擎 ---------- */
UPDATE dbo.MODULES SET EFFECT_ENGINE_TAG = 1, LAST_UPDATE_BY = N'DbUp', LAST_UPDATE_DATE = SYSDATETIME()
 WHERE M_IDX IN (@LocationModule, @DepotModule) AND ISNULL(EFFECT_ENGINE_TAG, 0) = 0;

/* ---------- ⒞ 保存期动作：路径重算（110309）/ 哨兵自动创建（110306） ---------- */
DECLARE @PathParam NVARCHAR(MAX) =
    N'{"table":"DEPOT_LOCATION","depotField":"DEPOT_ID","locationField":"LOCATION_NO",'
    + N'"parentField":"PARENT_NO","pathField":"LOCATION_PATH"}';

MERGE dbo.MODULE_BUSINESS_ACTION AS T
USING (SELECT @LocationModule AS MODULE_ID) AS S
   ON T.MODULE_ID = S.MODULE_ID AND T.EVENT_CODE = N'SAVE' AND T.SEQ = 1
WHEN MATCHED THEN
    UPDATE SET T.EFFECT_KEY = N'location-path-recalc', T.EFFECT_NAME = N'库位路径重算（保存期）',
               T.ENABLED = 1, T.FAIL_MODE = N'BLOCK', T.PARAM_STRUCT = @PathParam,
               T.REVERSE_STRUCT = N'{"kind":"none"}',
               T.REMARK = N'按上级重算自身物化路径，路径变化时同步整棵子树；拒绝成环',
               T.LAST_UPDATE_BY = N'DbUp', T.LAST_UPDATE_DATE = SYSDATETIME()
WHEN NOT MATCHED THEN
    INSERT (MODULE_ID, EVENT_CODE, SEQ, EFFECT_KEY, EFFECT_NAME, ENABLED, FAIL_MODE, CONDITION_STRUCT,
            PARAM_STRUCT, REVERSE_STRUCT, REMARK, SOURCE_REF, CREATE_PERSON, CREATE_DATE, LAST_UPDATE_BY, LAST_UPDATE_DATE)
    VALUES (S.MODULE_ID, N'SAVE', 1, N'location-path-recalc', N'库位路径重算（保存期）', 1, N'BLOCK', NULL,
            @PathParam, N'{"kind":"none"}',
            N'按上级重算自身物化路径，路径变化时同步整棵子树；拒绝成环', N'location-master',
            N'DbUp', SYSDATETIME(), N'DbUp', SYSDATETIME());

DECLARE @SentinelParam NVARCHAR(MAX) =
    N'{"table":"DEPOT_LOCATION","depotField":"DEPOT_ID","locationField":"LOCATION_NO",'
    + N'"parentField":"PARENT_NO","pathField":"LOCATION_PATH","typeField":"LOCATION_TYPE",'
    + N'"nameField":"LOCATION_NAME","seqField":"SEQ_NO","sentinelName":"未指定位置（待归位）"}';

MERGE dbo.MODULE_BUSINESS_ACTION AS T
USING (SELECT @DepotModule AS MODULE_ID) AS S
   ON T.MODULE_ID = S.MODULE_ID AND T.EVENT_CODE = N'SAVE' AND T.SEQ = 1
WHEN MATCHED THEN
    UPDATE SET T.EFFECT_KEY = N'depot-sentinel-location', T.EFFECT_NAME = N'库位哨兵行自动创建（保存期）',
               T.ENABLED = 1, T.FAIL_MODE = N'BLOCK', T.PARAM_STRUCT = @SentinelParam,
               T.REVERSE_STRUCT = N'{"kind":"none"}',
               T.REMARK = N'保存库别后保证该库别的"未指定位置"哨兵行存在（只补不覆盖）',
               T.LAST_UPDATE_BY = N'DbUp', T.LAST_UPDATE_DATE = SYSDATETIME()
WHEN NOT MATCHED THEN
    INSERT (MODULE_ID, EVENT_CODE, SEQ, EFFECT_KEY, EFFECT_NAME, ENABLED, FAIL_MODE, CONDITION_STRUCT,
            PARAM_STRUCT, REVERSE_STRUCT, REMARK, SOURCE_REF, CREATE_PERSON, CREATE_DATE, LAST_UPDATE_BY, LAST_UPDATE_DATE)
    VALUES (S.MODULE_ID, N'SAVE', 1, N'depot-sentinel-location', N'库位哨兵行自动创建（保存期）', 1, N'BLOCK', NULL,
            @SentinelParam, N'{"kind":"none"}',
            N'保存库别后保证该库别的"未指定位置"哨兵行存在（只补不覆盖）', N'location-master',
            N'DbUp', SYSDATETIME(), N'DbUp', SYSDATETIME());

/* ---------- 校验：哨兵形态守卫（SAVE）/ 哨兵删除守卫（DELETE） ---------- */
DECLARE @GuardCheck NVARCHAR(MAX) =
    N'{"table":"DEPOT_LOCATION","depotField":"DEPOT_ID","locationField":"LOCATION_NO",'
    + N'"message":"未指定位置（哨兵行）必须保持原样，不允许改上级 / 改类型 / 改路径 / 停用：",'
    + N'"deleteMessage":"未指定位置（哨兵行）不允许删除：该库别的库存余额与过账都指向它。"}';
DECLARE @GuardParam NVARCHAR(MAX) = N'{"handler":"depot-location-guard","check":' + @GuardCheck + N'}';
DECLARE @DeleteParam NVARCHAR(MAX) = N'{"handler":"depot-location-delete-guard","check":' + @GuardCheck + N'}';

MERGE dbo.MODULE_VALIDATION_RULE AS T
USING (SELECT @LocationModule AS MODULE_ID) AS S
   ON T.MODULE_ID = S.MODULE_ID AND T.STAGE = N'SAVE' AND T.VALIDATION_KEY = N'custom-validation' AND T.SEQ = 1
WHEN MATCHED THEN
    UPDATE SET T.PARAM_STRUCT = @GuardParam, T.ENABLED = 1,
               T.REMARK = N'哨兵位置行形态守卫（不可改上级/类型/路径/停用）',
               T.LAST_UPDATE_BY = N'DbUp', T.LAST_UPDATE_DATE = SYSDATETIME()
WHEN NOT MATCHED THEN
    INSERT (MODULE_ID, STAGE, SEQ, VALIDATION_KEY, ENABLED, PARAM_STRUCT, MESSAGE, REMARK, SOURCE_REF,
            CREATE_PERSON, CREATE_DATE, LAST_UPDATE_BY, LAST_UPDATE_DATE)
    VALUES (S.MODULE_ID, N'SAVE', 1, N'custom-validation', 1, @GuardParam, NULL,
            N'哨兵位置行形态守卫（不可改上级/类型/路径/停用）', N'location-master',
            N'DbUp', SYSDATETIME(), N'DbUp', SYSDATETIME());

MERGE dbo.MODULE_VALIDATION_RULE AS T
USING (SELECT @LocationModule AS MODULE_ID) AS S
   ON T.MODULE_ID = S.MODULE_ID AND T.STAGE = N'DELETE' AND T.VALIDATION_KEY = N'custom-validation' AND T.SEQ = 1
WHEN MATCHED THEN
    UPDATE SET T.PARAM_STRUCT = @DeleteParam, T.ENABLED = 1,
               T.REMARK = N'哨兵位置行删除守卫', T.LAST_UPDATE_BY = N'DbUp', T.LAST_UPDATE_DATE = SYSDATETIME()
WHEN NOT MATCHED THEN
    INSERT (MODULE_ID, STAGE, SEQ, VALIDATION_KEY, ENABLED, PARAM_STRUCT, MESSAGE, REMARK, SOURCE_REF,
            CREATE_PERSON, CREATE_DATE, LAST_UPDATE_BY, LAST_UPDATE_DATE)
    VALUES (S.MODULE_ID, N'DELETE', 1, N'custom-validation', 1, @DeleteParam, NULL,
            N'哨兵位置行删除守卫', N'location-master',
            N'DbUp', SYSDATETIME(), N'DbUp', SYSDATETIME());

/* ---------- ⒟ 标记待发布：运行时读已发布快照 ---------- */
MERGE dbo.WORKBENCH_MODULE_DIRTY AS T
USING (SELECT M_IDX AS MODULE_ID FROM dbo.MODULES WHERE M_IDX IN (@LocationModule, @DepotModule)) AS S
   ON T.MODULE_ID = S.MODULE_ID
WHEN MATCHED THEN UPDATE SET DIRTY_TAG = 1, LAST_MODIFIED_BY = N'DbUp', LAST_MODIFIED_AT = SYSDATETIME()
WHEN NOT MATCHED THEN INSERT (MODULE_ID, DIRTY_TAG, LAST_MODIFIED_BY, LAST_MODIFIED_AT)
    VALUES (S.MODULE_ID, 1, N'DbUp', SYSDATETIME());

/* ---------- 收口断言 ---------- */
IF COL_LENGTH('dbo.DEPOT_LOCATION', 'CONFIRM_TAG') IS NULL
    THROW 52103, N'DEPOT_LOCATION.CONFIRM_TAG 未补上，迁移中止。', 1;

IF EXISTS (SELECT 1 FROM dbo.MODULES WHERE M_IDX IN (@LocationModule, @DepotModule) AND ISNULL(EFFECT_ENGINE_TAG,0) <> 1)
    THROW 52104, N'效果引擎未对两个模块打开，迁移中止。', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.MODULE_BUSINESS_ACTION
                WHERE MODULE_ID = @LocationModule AND EVENT_CODE = N'SAVE' AND EFFECT_KEY = N'location-path-recalc' AND ENABLED = 1)
    THROW 52105, N'缺少库位路径重算动作，迁移中止。', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.MODULE_BUSINESS_ACTION
                WHERE MODULE_ID = @DepotModule AND EVENT_CODE = N'SAVE' AND EFFECT_KEY = N'depot-sentinel-location' AND ENABLED = 1)
    THROW 52106, N'缺少哨兵行自动创建动作，迁移中止。', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.MODULE_VALIDATION_RULE
                WHERE MODULE_ID = @LocationModule AND STAGE = N'SAVE' AND ENABLED = 1
                  AND PARAM_STRUCT LIKE N'%"handler":"depot-location-guard"%')
    THROW 52107, N'缺少哨兵形态守卫，迁移中止。', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.MODULE_VALIDATION_RULE
                WHERE MODULE_ID = @LocationModule AND STAGE = N'DELETE' AND ENABLED = 1
                  AND PARAM_STRUCT LIKE N'%"handler":"depot-location-delete-guard"%')
    THROW 52108, N'缺少哨兵删除守卫，迁移中止。', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.WORKBENCH_MODULE_DIRTY WHERE MODULE_ID IN (@LocationModule, @DepotModule) AND DIRTY_TAG = 1)
    THROW 52109, N'模块未标记为待发布，迁移中止。', 1;

PRINT N'== 库位主档已可 CRUD：生命周期列 + 路径重算 + 哨兵自动创建/保护，两个模块待发布 ==';
