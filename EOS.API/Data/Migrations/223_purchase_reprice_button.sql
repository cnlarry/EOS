-- ============================================================================
-- EOS.ERP migration 224: 采购单（1606）的自定义按钮「重新取价」
-- ----------------------------------------------------------------------------
-- 既有实现这一步是单据上的一个按钮（`btnCalc`，文案"取最新单价"，
-- 确认框"您确定要覆盖当前单价吗？"），调 `P_PUR_PURCHASE_GETPRICE` 按厂商计价重算单价与金额。
-- 本迁移把同一动作接到 EVENT_CODE='MANUAL' 的配置行上：
--     EFFECT_KEY   = 'purchase-reprice'（由代码闭集内的处理器实现）
--     CONFIRM_TAG  = 1（先返回"将会发生什么"，用户确认才写库）
--     CONDITION_STRUCT = NULL：取价的正常时机恰恰是批核之前（草稿先取价再送审），
--     不设"已批核才能点"；只拦已完工结案（结算凭据不可动），由处理器执行。
--
-- 幂等：动作按 (MODULE_ID, EVENT_CODE='MANUAL', SEQ) 合并；脏标记按 MODULE_ID 合并。
-- 回滚：DELETE 按 SOURCE_REF='document-action' 写入的 MANUAL 行并清脏标记。
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

DECLARE @Module INT = 1606;
DECLARE @ActionKey NVARCHAR(50) = N'purchase-reprice';
DECLARE @Source NVARCHAR(100) = N'document-action';

IF COL_LENGTH(N'dbo.MODULE_BUSINESS_ACTION', N'LABEL') IS NULL
    OR COL_LENGTH(N'dbo.MODULE_BUSINESS_ACTION', N'CONFIRM_TAG') IS NULL
    THROW 52240, N'MODULE_BUSINESS_ACTION 缺少 LABEL / CONFIRM_TAG 列，请先执行迁移 218。', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.MODULES WHERE M_IDX = @Module
                 AND LTRIM(RTRIM(ISNULL(MASTER_TABLE, N''))) = N'PUR_PURCHASE_M'
                 AND LTRIM(RTRIM(ISNULL(DETAIL_TABLE, N''))) = N'PUR_PURCHASE_D')
    THROW 52241, N'模块 1606 形态不符（应为 PUR_PURCHASE_M / PUR_PURCHASE_D），迁移中止。', 1;

/* 目标顺序号不得被别的按钮占用（同一事件内 SEQ 唯一；覆盖别的按钮是配置事故） */
IF EXISTS (SELECT 1 FROM dbo.MODULE_BUSINESS_ACTION
           WHERE MODULE_ID = @Module AND EVENT_CODE = N'MANUAL' AND SEQ = 1
             AND ISNULL(SOURCE_REF, N'') <> @Source)
    THROW 52242, N'模块 1606 的 MANUAL 顺序号 1 已被其它按钮占用，迁移中止。', 1;

MERGE dbo.MODULE_BUSINESS_ACTION AS T
USING (SELECT @Module AS MODULE_ID, 1 AS SEQ) AS S
   ON T.MODULE_ID = S.MODULE_ID AND T.EVENT_CODE = N'MANUAL' AND T.SEQ = S.SEQ
WHEN MATCHED THEN
    UPDATE SET T.EFFECT_KEY = @ActionKey, T.EFFECT_NAME = N'重新取价',
               T.LABEL = N'重新取价', T.CONFIRM_TAG = 1,
               T.ENABLED = 1, T.FAIL_MODE = N'BLOCK',
               T.CONDITION_STRUCT = NULL, T.PARAM_STRUCT = NULL, T.REVERSE_STRUCT = NULL,
               T.REMARK = N'用户在采购单上点击：按厂商计价重算本单明细的单价与金额（无计价的行保留原价）',
               T.SOURCE_REF = @Source,
               T.LAST_UPDATE_BY = N'DbUp', T.LAST_UPDATE_DATE = SYSDATETIME()
WHEN NOT MATCHED THEN
    INSERT (MODULE_ID, EVENT_CODE, SEQ, EFFECT_KEY, EFFECT_NAME, LABEL, CONFIRM_TAG,
            ENABLED, FAIL_MODE, CONDITION_STRUCT, PARAM_STRUCT, REVERSE_STRUCT, REMARK, SOURCE_REF,
            CREATE_PERSON, CREATE_DATE, LAST_UPDATE_BY, LAST_UPDATE_DATE)
    VALUES (S.MODULE_ID, N'MANUAL', S.SEQ, @ActionKey, N'重新取价', N'重新取价', 1,
            1, N'BLOCK', NULL, NULL, NULL,
            N'用户在采购单上点击：按厂商计价重算本单明细的单价与金额（无计价的行保留原价）', @Source,
            N'DbUp', SYSDATETIME(), N'DbUp', SYSDATETIME());

/* 运行时读的是已发布快照：配置改了必须重发布才生效 */
MERGE dbo.WORKBENCH_MODULE_DIRTY AS D
USING (SELECT @Module AS MODULE_ID) AS S
   ON D.MODULE_ID = S.MODULE_ID
WHEN MATCHED THEN UPDATE SET D.DIRTY_TAG = 1, D.LAST_MODIFIED_BY = N'DbUp', D.LAST_MODIFIED_AT = SYSDATETIME()
WHEN NOT MATCHED THEN INSERT (MODULE_ID, DIRTY_TAG, LAST_MODIFIED_BY, LAST_MODIFIED_AT)
    VALUES (S.MODULE_ID, 1, N'DbUp', SYSDATETIME());

/* ---------- 收口断言 ---------- */
IF NOT EXISTS (SELECT 1 FROM dbo.MODULE_BUSINESS_ACTION a
               WHERE a.MODULE_ID = @Module AND a.EVENT_CODE = N'MANUAL' AND a.SEQ = 1
                 AND a.EFFECT_KEY = @ActionKey AND a.ENABLED = 1 AND a.CONFIRM_TAG = 1
                 AND a.LABEL = N'重新取价' AND a.FAIL_MODE = N'BLOCK')
    THROW 52243, N'「重新取价」按钮未配置成功，迁移中止。', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.WORKBENCH_MODULE_DIRTY d WHERE d.MODULE_ID = @Module AND d.DIRTY_TAG = 1)
    THROW 52244, N'模块 1606 未标记为待发布，迁移中止。', 1;

PRINT N'== 已为 1606 配上「重新取价」自定义按钮（需重发布该模块快照后生效）==';
GO
