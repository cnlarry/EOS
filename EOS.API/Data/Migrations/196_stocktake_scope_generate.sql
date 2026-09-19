-- ============================================================================
-- EOS.ERP migration 197: 盘点单按库区范围生成明细
-- ----------------------------------------------------------------------------
-- 盘点单不直接改库存，全部价值在明细：明细把"这个库里有哪些货、各多少"摊开，人再逐行
-- 填实际数，差异行转调整单。库位维度落地后，"哪些货"的答案依赖位置——只按库别列出全部
-- 料号，按库位盘出的差异会在库别层面被平均掉，等于白盘。
--
-- 单头的盘点范围列（LOCATION_ROOT_NO，P1-08 已加）此前只是一个记录字段，没有任何执行
-- 者。本迁移把它接上：模块 130101 的 SAVE 阶段挂 `stocktake-scope-generate`，保存时若
-- 填了范围且本单还没有明细，就按范围的物化路径前缀把该区当前有量的库存行展开成明细。
--
-- 为什么必须改 DETAIL_NO_SAVE 的判定时点：130101 的 DETAIL_NO_SAVE=1（无明细不可保存），
-- 而"按区生成"正是要保存一张还没有明细的单子。判定改为"保存结束时明细表必须有行"
-- （见 WorkbenchCommandHandler.DetailRequirementAsync），只对声明了明细生成者的模块生效，
-- 其余模块维持原样。
--
-- 幂等：动作按 (MODULE_ID, EVENT_CODE, SEQ) 合并；脏标记按 MODULE_ID 合并。
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
    THROW 52200, @GUARD_MESSAGE, 1;

DECLARE @Module INT = 130101;

IF NOT EXISTS (SELECT 1 FROM dbo.MODULES WHERE M_IDX = @Module
                 AND LTRIM(RTRIM(ISNULL(MASTER_TABLE, ''))) = N'INV_CHECK_STOCK_M'
                 AND LTRIM(RTRIM(ISNULL(DETAIL_TABLE, ''))) = N'INV_CHECK_STOCK_D')
    THROW 52201, N'模块 130101 形态不符（应为 INV_CHECK_STOCK_M / INV_CHECK_STOCK_D），迁移中止。', 1;

/* ---------- 前置列：范围列与明细的位置 / 批次列必须已存在 ---------- */
IF COL_LENGTH(N'dbo.INV_CHECK_STOCK_M', N'LOCATION_ROOT_NO') IS NULL
    THROW 52202, N'INV_CHECK_STOCK_M.LOCATION_ROOT_NO 不存在，迁移中止。', 1;

IF COL_LENGTH(N'dbo.INV_CHECK_STOCK_D', N'LOCATION_NO') IS NULL
    OR COL_LENGTH(N'dbo.INV_CHECK_STOCK_D', N'BATCH_NO') IS NULL
    THROW 52203, N'INV_CHECK_STOCK_D 缺少 LOCATION_NO / BATCH_NO，迁移中止。', 1;

/* ---------- 打开效果引擎并挂 SAVE 阶段动作 ---------- */
UPDATE dbo.MODULES SET EFFECT_ENGINE_TAG = 1, LAST_UPDATE_BY = N'DbUp', LAST_UPDATE_DATE = SYSDATETIME()
 WHERE M_IDX = @Module AND ISNULL(EFFECT_ENGINE_TAG, 0) = 0;

DECLARE @ScopeParam NVARCHAR(MAX) =
    N'{"typeField":"CHECK_STOCK_TYPE","noField":"CHECK_STOCK_NO","scopeField":"LOCATION_ROOT_NO",'
    + N'"masterDepotField":"DEPOT_ID",'
    + N'"locationTable":"DEPOT_LOCATION","locationDepotField":"DEPOT_ID","locationField":"LOCATION_NO",'
    + N'"pathField":"LOCATION_PATH",'
    + N'"stockTable":"INV_PRO_DEPOT","stockDepotField":"DEPOT_ID","stockProductField":"PRO_NO",'
    + N'"stockLocationField":"LOCATION_NO","stockBatchField":"BATCH_NO","stockQtyField":"QTY",'
    + N'"detailProductField":"PRO_NO","detailDepotField":"DEPOT_ID","detailAccountField":"ACCOUNT_QTY",'
    + N'"detailCheckField":"CHECK_QTY","detailLocationField":"LOCATION_NO","detailBatchField":"BATCH_NO",'
    + N'"serialField":"SERIAL_NO","sentinelLocationNo":"-"}';

MERGE dbo.MODULE_BUSINESS_ACTION AS T
USING (SELECT @Module AS MODULE_ID) AS S
   ON T.MODULE_ID = S.MODULE_ID AND T.EVENT_CODE = N'SAVE' AND T.SEQ = 1
WHEN MATCHED THEN
    UPDATE SET T.EFFECT_KEY = N'stocktake-scope-generate', T.EFFECT_NAME = N'按盘点范围生成明细（保存期）',
               T.ENABLED = 1, T.FAIL_MODE = N'BLOCK', T.CONDITION_STRUCT = NULL, T.PARAM_STRUCT = @ScopeParam,
               T.REVERSE_STRUCT = N'{"kind":"none"}',
               T.REMARK = N'单头填了盘点范围且本单尚无明细时，按该库区的物化路径前缀展开当前有量的库存行为明细；账面数与盘点数均取当前库存量',
               T.LAST_UPDATE_BY = N'DbUp', T.LAST_UPDATE_DATE = SYSDATETIME()
WHEN NOT MATCHED THEN
    INSERT (MODULE_ID, EVENT_CODE, SEQ, EFFECT_KEY, EFFECT_NAME, ENABLED, FAIL_MODE, CONDITION_STRUCT,
            PARAM_STRUCT, REVERSE_STRUCT, REMARK, SOURCE_REF, CREATE_PERSON, CREATE_DATE, LAST_UPDATE_BY, LAST_UPDATE_DATE)
    VALUES (S.MODULE_ID, N'SAVE', 1, N'stocktake-scope-generate', N'按盘点范围生成明细（保存期）', 1, N'BLOCK', NULL,
            @ScopeParam, N'{"kind":"none"}',
            N'单头填了盘点范围且本单尚无明细时，按该库区的物化路径前缀展开当前有量的库存行为明细；账面数与盘点数均取当前库存量',
            N'inv-check-stock', N'DbUp', SYSDATETIME(), N'DbUp', SYSDATETIME());

/* ---------- 标记待发布：运行时读的是已发布快照 ---------- */
MERGE dbo.WORKBENCH_MODULE_DIRTY AS T
USING (SELECT @Module AS MODULE_ID) AS S
   ON T.MODULE_ID = S.MODULE_ID
WHEN MATCHED THEN UPDATE SET DIRTY_TAG = 1, LAST_MODIFIED_BY = N'DbUp', LAST_MODIFIED_AT = SYSDATETIME()
WHEN NOT MATCHED THEN INSERT (MODULE_ID, DIRTY_TAG, LAST_MODIFIED_BY, LAST_MODIFIED_AT)
    VALUES (S.MODULE_ID, 1, N'DbUp', SYSDATETIME());

/* ---------- 收口断言 ---------- */
IF EXISTS (SELECT 1 FROM dbo.MODULES WHERE M_IDX = @Module AND ISNULL(EFFECT_ENGINE_TAG, 0) <> 1)
    THROW 52204, N'效果引擎未对模块 130101 打开，迁移中止。', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.MODULE_BUSINESS_ACTION
                WHERE MODULE_ID = @Module AND EVENT_CODE = N'SAVE'
                  AND EFFECT_KEY = N'stocktake-scope-generate' AND ENABLED = 1 AND FAIL_MODE = N'BLOCK')
    THROW 52205, N'缺少按盘点范围生成明细的保存期动作，迁移中止。', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.MODULE_BUSINESS_ACTION
                WHERE MODULE_ID = @Module AND EVENT_CODE = N'SAVE' AND SEQ = 1
                  AND PARAM_STRUCT LIKE N'%"scopeField":"LOCATION_ROOT_NO"%'
                  AND PARAM_STRUCT LIKE N'%"stockQtyField":"QTY"%')
    THROW 52206, N'盘点范围生成动作的参数不完整，迁移中止。', 1;

/* 批次粒度必须传递：明细不带 BATCH_NO 会把同料号不同批次的差异合并成一行 */
IF NOT EXISTS (SELECT 1 FROM dbo.MODULE_BUSINESS_ACTION
                WHERE MODULE_ID = @Module AND EVENT_CODE = N'SAVE' AND SEQ = 1
                  AND PARAM_STRUCT LIKE N'%"detailBatchField":"BATCH_NO"%')
    THROW 52207, N'盘点范围生成动作未传递批次列，迁移中止。', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.WORKBENCH_MODULE_DIRTY WHERE MODULE_ID = @Module AND DIRTY_TAG = 1)
    THROW 52208, N'模块 130101 未标记为待发布，迁移中止。', 1;

PRINT N'== 盘点单已可按库区范围生成明细：130101 SAVE 动作已挂、模块待发布 ==';
