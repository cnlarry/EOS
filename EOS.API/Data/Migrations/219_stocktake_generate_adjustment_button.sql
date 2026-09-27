-- ============================================================================
-- EOS.ERP migration 220: 库存盘点单（130101）的自定义按钮「生成调整单」
-- ----------------------------------------------------------------------------
-- 既有实现这一步同样是明细工具条上的按钮（`btnAdjust`）：
-- 有盈亏才转单、无盈亏直接提示"不需要调整"。本迁移把同一动作接到 MANUAL 配置行上：
--     EFFECT_KEY = 'generate-adjustment'（处理器见 DocumentActions/Handlers/GenerateAdjustmentHandler.cs）
--     CONFIRM_TAG = 1（点击前先返回"将会发生什么"）
--     CONDITION_STRUCT 判 CONFIRM_TAG=1：未批核的盘点单不许转单，服务端每次点击都复核
--     SEQ = 2（与「重算账面数量」同事件内排序，界面按此顺序排列按钮）
--
-- 与「重算账面数量」的分工：重算是把已有明细的账面数刷成最新库存（不改行、不转单）；
-- 生成调整单是拿"盘点数－账面数"的差异去开下游单据并过账，成功后同事务结案锁死（once）。
--
-- 幂等：动作按 (MODULE_ID, EVENT_CODE='MANUAL', SEQ) 合并；脏标记按 MODULE_ID 合并。
-- 回滚：DELETE 按 SOURCE_REF='document-action' 且 SEQ=2 的 MANUAL 行并清脏标记。
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
    THROW 50000, @GUARD_MESSAGE, 1;

DECLARE @Module INT = 130101;
DECLARE @ActionKey NVARCHAR(50) = N'generate-adjustment';
DECLARE @Source NVARCHAR(100) = N'document-action';

IF COL_LENGTH(N'dbo.MODULE_BUSINESS_ACTION', N'LABEL') IS NULL
    OR COL_LENGTH(N'dbo.MODULE_BUSINESS_ACTION', N'CONFIRM_TAG') IS NULL
    THROW 52200, N'MODULE_BUSINESS_ACTION 缺少 LABEL / CONFIRM_TAG 列，请先执行迁移 218。', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.MODULES WHERE M_IDX = @Module
                 AND LTRIM(RTRIM(ISNULL(MASTER_TABLE, ''))) = N'INV_CHECK_STOCK_M'
                 AND LTRIM(RTRIM(ISNULL(DETAIL_TABLE, ''))) = N'INV_CHECK_STOCK_D')
    THROW 52201, N'模块 130101 形态不符（应为 INV_CHECK_STOCK_M / INV_CHECK_STOCK_D），迁移中止。', 1;

-- 追溯落点：盘点单上的 ADJUST_TYPE/ADJUST_NO（既有实现即写这两列）+ FINISHED_TAG 结案锁死。
IF COL_LENGTH(N'dbo.INV_CHECK_STOCK_M', N'ADJUST_TYPE') IS NULL
    OR COL_LENGTH(N'dbo.INV_CHECK_STOCK_M', N'ADJUST_NO') IS NULL
    OR COL_LENGTH(N'dbo.INV_CHECK_STOCK_M', N'FINISHED_TAG') IS NULL
    THROW 52202, N'INV_CHECK_STOCK_M 缺少 ADJUST_TYPE / ADJUST_NO / FINISHED_TAG，无法回写来源与结案，迁移中止。', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.MODULES WHERE M_IDX = 130107
                 AND LTRIM(RTRIM(ISNULL(MASTER_TABLE, ''))) = N'INV_OCCUR_ADJUST_M'
                 AND LTRIM(RTRIM(ISNULL(DETAIL_TABLE, ''))) = N'INV_OCCUR_ADJUST_D')
    THROW 52203, N'模块 130107 形态不符（应为 INV_OCCUR_ADJUST_M / INV_OCCUR_ADJUST_D），迁移中止。', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.BILLKIND WHERE B_M_IDX = 130107 AND IS_DEFAULT = 1 AND IS_AUTO = 1)
    THROW 52204, N'库存调整单没有"默认且自动编号"的单别，生成的调整单拿不到单号，迁移中止。', 1;

IF EXISTS (SELECT 1 FROM dbo.MODULE_BUSINESS_ACTION
           WHERE MODULE_ID = @Module AND EVENT_CODE = N'MANUAL' AND SEQ = 2
             AND ISNULL(SOURCE_REF, N'') <> @Source)
    THROW 52205, N'模块 130101 的 MANUAL 顺序号 2 已被其它按钮占用，迁移中止。', 1;

DECLARE @Condition NVARCHAR(MAX) =
    N'{"logic":"AND","items":[{"type":"value-eq","field":{"scope":"MASTER","field":"CONFIRM_TAG"},"value":1}]}';

MERGE dbo.MODULE_BUSINESS_ACTION AS T
USING (SELECT @Module AS MODULE_ID, 2 AS SEQ) AS S
   ON T.MODULE_ID = S.MODULE_ID AND T.EVENT_CODE = N'MANUAL' AND T.SEQ = S.SEQ
WHEN MATCHED THEN
    UPDATE SET T.EFFECT_KEY = @ActionKey, T.EFFECT_NAME = N'生成调整单',
               T.LABEL = N'生成调整单', T.CONFIRM_TAG = 1,
               T.ENABLED = 1, T.FAIL_MODE = N'BLOCK',
               T.CONDITION_STRUCT = @Condition, T.PARAM_STRUCT = NULL, T.REVERSE_STRUCT = NULL,
               T.REMARK = N'用户在盘点单上点击：把盈亏行转成库存调整单（130107）并过账，成功后同事务结案锁死',
               T.SOURCE_REF = @Source,
               T.LAST_UPDATE_BY = N'DbUp', T.LAST_UPDATE_DATE = SYSDATETIME()
WHEN NOT MATCHED THEN
    INSERT (MODULE_ID, EVENT_CODE, SEQ, EFFECT_KEY, EFFECT_NAME, LABEL, CONFIRM_TAG,
            ENABLED, FAIL_MODE, CONDITION_STRUCT, PARAM_STRUCT, REVERSE_STRUCT, REMARK, SOURCE_REF,
            CREATE_PERSON, CREATE_DATE, LAST_UPDATE_BY, LAST_UPDATE_DATE)
    VALUES (S.MODULE_ID, N'MANUAL', S.SEQ, @ActionKey, N'生成调整单', N'生成调整单', 1,
            1, N'BLOCK', @Condition, NULL, NULL,
            N'用户在盘点单上点击：把盈亏行转成库存调整单（130107）并过账，成功后同事务结案锁死', @Source,
            N'DbUp', SYSDATETIME(), N'DbUp', SYSDATETIME());

MERGE dbo.WORKBENCH_MODULE_DIRTY AS D
USING (SELECT @Module AS MODULE_ID) AS S
   ON D.MODULE_ID = S.MODULE_ID
WHEN MATCHED THEN UPDATE SET D.DIRTY_TAG = 1, D.LAST_MODIFIED_BY = N'DbUp', D.LAST_MODIFIED_AT = SYSDATETIME()
WHEN NOT MATCHED THEN INSERT (MODULE_ID, DIRTY_TAG, LAST_MODIFIED_BY, LAST_MODIFIED_AT)
    VALUES (S.MODULE_ID, 1, N'DbUp', SYSDATETIME());

IF NOT EXISTS (SELECT 1 FROM dbo.MODULE_BUSINESS_ACTION a
               WHERE a.MODULE_ID = @Module AND a.EVENT_CODE = N'MANUAL' AND a.SEQ = 2
                 AND a.EFFECT_KEY = @ActionKey AND a.ENABLED = 1 AND a.CONFIRM_TAG = 1
                 AND a.LABEL = N'生成调整单' AND a.FAIL_MODE = N'BLOCK'
                 AND CHARINDEX(N'"field":"CONFIRM_TAG"', ISNULL(a.CONDITION_STRUCT, N'')) > 0)
    THROW 52206, N'「生成调整单」按钮未配置成功，迁移中止。', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.WORKBENCH_MODULE_DIRTY d WHERE d.MODULE_ID = @Module AND d.DIRTY_TAG = 1)
    THROW 52207, N'模块 130101 未标记为待发布，迁移中止。', 1;

PRINT N'== 已为 130101 配上「生成调整单」自定义按钮（需重发布该模块快照后生效）==';
GO
