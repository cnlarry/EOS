-- ============================================================================
-- EOS.ERP migration 219: 库存盘点单（130101）的自定义按钮「重算账面数量」
-- ----------------------------------------------------------------------------
-- 旧系统这一步是明细工具条上的一个按钮（`INV/Check_Stock.aspx.cs` 的 `btnReCount`），
-- 调 `P_INV_CHECK_RECALC_ACCOUNT` 重算**已有明细行**的账面数，不生成行。
-- 本迁移把同一动作接到 EVENT_CODE='MANUAL' 的配置行上：
--     EFFECT_KEY = 'recalc-account'（由代码闭集内的处理器实现，发布校验 document_action_keys_registered 把关）
--     CONFIRM_TAG = 1（点击前先返回"将会发生什么"，用户确认才写库）
--     CONDITION_STRUCT 判 CONFIRM_TAG=1：未批核的单据不允许重算，服务端每次点击都复核
--     LABEL = '重算账面数量'（按钮文案）
--
-- 与保存期的 `stocktake-scope-generate`（按盘点范围生成明细）是两件事，口径写在
-- EOS.API/Data/DocumentActions/Handlers/RecalcAccountHandler.cs 的类注释里：
-- 明细行由"生成"决定，行上的账面数由"重算"决定，两者都不得碰对方的东西。
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

DECLARE @Module INT = 130101;
DECLARE @ActionKey NVARCHAR(50) = N'recalc-account';
DECLARE @Source NVARCHAR(100) = N'document-action';

IF COL_LENGTH(N'dbo.MODULE_BUSINESS_ACTION', N'LABEL') IS NULL
    OR COL_LENGTH(N'dbo.MODULE_BUSINESS_ACTION', N'CONFIRM_TAG') IS NULL
    THROW 52190, N'MODULE_BUSINESS_ACTION 缺少 LABEL / CONFIRM_TAG 列，请先执行迁移 218。', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.MODULES WHERE M_IDX = @Module
                 AND LTRIM(RTRIM(ISNULL(MASTER_TABLE, ''))) = N'INV_CHECK_STOCK_M'
                 AND LTRIM(RTRIM(ISNULL(DETAIL_TABLE, ''))) = N'INV_CHECK_STOCK_D')
    THROW 52191, N'模块 130101 形态不符（应为 INV_CHECK_STOCK_M / INV_CHECK_STOCK_D），迁移中止。', 1;

IF COL_LENGTH(N'dbo.INV_CHECK_STOCK_M', N'CONFIRM_TAG') IS NULL
    THROW 52192, N'INV_CHECK_STOCK_M.CONFIRM_TAG 不存在，无法配置「已批核才能重算」前置条件，迁移中止。', 1;

/* 目标顺序号不得被别的按钮占用（同一事件内 SEQ 唯一；覆盖别的按钮是配置事故） */
IF EXISTS (SELECT 1 FROM dbo.MODULE_BUSINESS_ACTION
           WHERE MODULE_ID = @Module AND EVENT_CODE = N'MANUAL' AND SEQ = 1
             AND ISNULL(SOURCE_REF, N'') <> @Source)
    THROW 52193, N'模块 130101 的 MANUAL 顺序号 1 已被其它按钮占用，迁移中止。', 1;

DECLARE @Condition NVARCHAR(MAX) =
    N'{"logic":"AND","items":[{"type":"value-eq","field":{"scope":"MASTER","field":"CONFIRM_TAG"},"value":1}]}';

MERGE dbo.MODULE_BUSINESS_ACTION AS T
USING (SELECT @Module AS MODULE_ID, 1 AS SEQ) AS S
   ON T.MODULE_ID = S.MODULE_ID AND T.EVENT_CODE = N'MANUAL' AND T.SEQ = S.SEQ
WHEN MATCHED THEN
    UPDATE SET T.EFFECT_KEY = @ActionKey, T.EFFECT_NAME = N'重算账面数量',
               T.LABEL = N'重算账面数量', T.CONFIRM_TAG = 1,
               T.ENABLED = 1, T.FAIL_MODE = N'BLOCK',
               T.CONDITION_STRUCT = @Condition, T.PARAM_STRUCT = NULL, T.REVERSE_STRUCT = NULL,
               T.REMARK = N'用户在盘点单上点击：按当前库存重算已有明细行的账面数（不生成行、不动盘点数）',
               T.SOURCE_REF = @Source,
               T.LAST_UPDATE_BY = N'DbUp', T.LAST_UPDATE_DATE = SYSDATETIME()
WHEN NOT MATCHED THEN
    INSERT (MODULE_ID, EVENT_CODE, SEQ, EFFECT_KEY, EFFECT_NAME, LABEL, CONFIRM_TAG,
            ENABLED, FAIL_MODE, CONDITION_STRUCT, PARAM_STRUCT, REVERSE_STRUCT, REMARK, SOURCE_REF,
            CREATE_PERSON, CREATE_DATE, LAST_UPDATE_BY, LAST_UPDATE_DATE)
    VALUES (S.MODULE_ID, N'MANUAL', S.SEQ, @ActionKey, N'重算账面数量', N'重算账面数量', 1,
            1, N'BLOCK', @Condition, NULL, NULL,
            N'用户在盘点单上点击：按当前库存重算已有明细行的账面数（不生成行、不动盘点数）', @Source,
            N'DbUp', SYSDATETIME(), N'DbUp', SYSDATETIME());

/* 运行时读的是已发布快照：配置改了必须重发布才生效 */
MERGE dbo.WORKBENCH_MODULE_DIRTY AS D
USING (SELECT @Module AS MODULE_ID) AS S
   ON D.MODULE_ID = S.MODULE_ID
WHEN MATCHED THEN UPDATE SET D.DIRTY_TAG = 1, D.LAST_MODIFIED_BY = N'DbUp', D.LAST_MODIFIED_AT = SYSDATETIME()
WHEN NOT MATCHED THEN INSERT (MODULE_ID, DIRTY_TAG, LAST_MODIFIED_BY, LAST_MODIFIED_AT)
    VALUES (S.MODULE_ID, 1, N'DbUp', SYSDATETIME());

/* ---------- 收口断言 ----------
   用 CHARINDEX 而不是 LIKE：JSON 里的 `[` 在 LIKE 模式中是字符类起始符，会静默匹配不上。 */
IF NOT EXISTS (SELECT 1 FROM dbo.MODULE_BUSINESS_ACTION a
               WHERE a.MODULE_ID = @Module AND a.EVENT_CODE = N'MANUAL' AND a.SEQ = 1
                 AND a.EFFECT_KEY = @ActionKey AND a.ENABLED = 1 AND a.CONFIRM_TAG = 1
                 AND a.LABEL = N'重算账面数量' AND a.FAIL_MODE = N'BLOCK'
                 AND CHARINDEX(N'"field":"CONFIRM_TAG"', ISNULL(a.CONDITION_STRUCT, N'')) > 0)
    THROW 52194, N'「重算账面数量」按钮未配置成功，迁移中止。', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.WORKBENCH_MODULE_DIRTY d WHERE d.MODULE_ID = @Module AND d.DIRTY_TAG = 1)
    THROW 52195, N'模块 130101 未标记为待发布，迁移中止。', 1;

PRINT N'== 已为 130101 配上「重算账面数量」自定义按钮（需重发布该模块快照后生效）==';
GO
