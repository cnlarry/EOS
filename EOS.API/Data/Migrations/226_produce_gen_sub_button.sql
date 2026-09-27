-- ============================================================================
-- EOS.ERP migration 227: 制令单（1502）的自定义按钮「展开子制令」
-- ----------------------------------------------------------------------------
-- 既有实现这一步是制令单上的一个按钮（`btnGenSubProduce`，
-- 文案"展开制令"）：按工单 BOM 把有下阶料的子件逐个建成子制令（父项指向本单）。
-- 本迁移把同一动作接到 EVENT_CODE='MANUAL' 的配置行上（SEQ = 2，SEQ 1 已被「计算用料」占用）：
--     EFFECT_KEY   = 'produce-gen-sub'（由代码闭集内的处理器实现）
--     CONFIRM_TAG  = 1（先返回"将会发生什么"，用户确认才写库）
--     CONDITION_STRUCT = NULL：展开的正常时机在批核之前，不设"已批核才能点"；
--     只拦已完工结案（追溯凭据不可动），由处理器执行。
-- 只配在 1502（默认制令模块）：子单的单别恒取默认制令单别，家就在 1502。
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

DECLARE @Module INT = 1502;
DECLARE @ActionKey NVARCHAR(50) = N'produce-gen-sub';
DECLARE @Source NVARCHAR(100) = N'document-action';

IF COL_LENGTH(N'dbo.MODULE_BUSINESS_ACTION', N'LABEL') IS NULL
    OR COL_LENGTH(N'dbo.MODULE_BUSINESS_ACTION', N'CONFIRM_TAG') IS NULL
    THROW 52270, N'MODULE_BUSINESS_ACTION 缺少 LABEL / CONFIRM_TAG 列，请先执行迁移 218。', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.MODULES WHERE M_IDX = @Module
                 AND LTRIM(RTRIM(ISNULL(MASTER_TABLE, N''))) = N'MOC_PRODUCE_M'
                 AND LTRIM(RTRIM(ISNULL(DETAIL_TABLE, N''))) = N'MOC_PRODUCE_D')
    THROW 52271, N'模块 1502 形态不符（应为 MOC_PRODUCE_M / MOC_PRODUCE_D），迁移中止。', 1;

/* 目标顺序号不得被别的按钮占用（同一事件内 SEQ 唯一；覆盖别的按钮是配置事故） */
IF EXISTS (SELECT 1 FROM dbo.MODULE_BUSINESS_ACTION
           WHERE MODULE_ID = @Module AND EVENT_CODE = N'MANUAL' AND SEQ = 2
             AND ISNULL(SOURCE_REF, N'') <> @Source)
    THROW 52272, N'模块 1502 的 MANUAL 顺序号 2 已被其它按钮占用，迁移中止。', 1;

MERGE dbo.MODULE_BUSINESS_ACTION AS T
USING (SELECT @Module AS MODULE_ID, 2 AS SEQ) AS S
   ON T.MODULE_ID = S.MODULE_ID AND T.EVENT_CODE = N'MANUAL' AND T.SEQ = S.SEQ
WHEN MATCHED THEN
    UPDATE SET T.EFFECT_KEY = @ActionKey, T.EFFECT_NAME = N'展开子制令',
               T.LABEL = N'展开子制令', T.CONFIRM_TAG = 1,
               T.ENABLED = 1, T.FAIL_MODE = N'BLOCK',
               T.CONDITION_STRUCT = NULL, T.PARAM_STRUCT = NULL, T.REVERSE_STRUCT = NULL,
               T.REMARK = N'用户在制令单上点击：按工单 BOM 把有下阶料的子件逐个建成子制令（只补漏，不复制）',
               T.SOURCE_REF = @Source,
               T.LAST_UPDATE_BY = N'DbUp', T.LAST_UPDATE_DATE = SYSDATETIME()
WHEN NOT MATCHED THEN
    INSERT (MODULE_ID, EVENT_CODE, SEQ, EFFECT_KEY, EFFECT_NAME, LABEL, CONFIRM_TAG,
            ENABLED, FAIL_MODE, CONDITION_STRUCT, PARAM_STRUCT, REVERSE_STRUCT, REMARK, SOURCE_REF,
            CREATE_PERSON, CREATE_DATE, LAST_UPDATE_BY, LAST_UPDATE_DATE)
    VALUES (S.MODULE_ID, N'MANUAL', S.SEQ, @ActionKey, N'展开子制令', N'展开子制令', 1,
            1, N'BLOCK', NULL, NULL, NULL,
            N'用户在制令单上点击：按工单 BOM 把有下阶料的子件逐个建成子制令（只补漏，不复制）', @Source,
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
               WHERE a.MODULE_ID = @Module AND a.EVENT_CODE = N'MANUAL' AND a.SEQ = 2
                 AND a.EFFECT_KEY = @ActionKey AND a.ENABLED = 1 AND a.CONFIRM_TAG = 1
                 AND a.LABEL = N'展开子制令' AND a.FAIL_MODE = N'BLOCK')
    THROW 52273, N'「展开子制令」按钮未配置成功，迁移中止。', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.WORKBENCH_MODULE_DIRTY d WHERE d.MODULE_ID = @Module AND d.DIRTY_TAG = 1)
    THROW 52274, N'模块 1502 未标记为待发布，迁移中止。', 1;

PRINT N'== 已为 1502 配上「展开子制令」自定义按钮（需重发布该模块快照后生效）==';
GO
