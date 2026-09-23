-- ============================================================================
-- EOS.ERP migration 223: 库存盘点单（130101）的自定义按钮「换区重盘」
-- ----------------------------------------------------------------------------
-- 范围选错的盘点单整单重来：按新盘点范围把明细整单换掉（删既有明细 + 按新范围重插，
-- 与保存期 stocktake-scope-generate 同一把生成尺子，位置/批次原样带入）。
-- 本迁移把同一动作接到 EVENT_CODE='MANUAL' 的配置行上：
--     EFFECT_KEY   = 'restock-scope'（由代码闭集内的处理器实现）
--     CONFIRM_TAG  = 1（先返回"将会发生什么"，用户确认才写库）
--     PARAM_STRUCT = {"fields":[{"key":"scopeRoot",…}]}（新盘点范围由用户填，服务端按同一声明复核）
--     CONDITION_STRUCT = NULL：与 recalc/generate 两按钮的"已批核才能点"有意不同——
--     重盘的典型场景恰恰是单子还没批、范围填错了，要求先批核再重盘等于逼人把错单坐实。
--     守卫只拦"已结案/已转过调整单"（追溯凭据不可换），由处理器执行，见
--     EOS.API/Data/DocumentActions/Handlers/RestockScopeHandler.cs。
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
DECLARE @ActionKey NVARCHAR(50) = N'restock-scope';
DECLARE @Source NVARCHAR(100) = N'document-action';

IF COL_LENGTH(N'dbo.MODULE_BUSINESS_ACTION', N'LABEL') IS NULL
    OR COL_LENGTH(N'dbo.MODULE_BUSINESS_ACTION', N'CONFIRM_TAG') IS NULL
    OR COL_LENGTH(N'dbo.MODULE_BUSINESS_ACTION', N'PARAM_STRUCT') IS NULL
    THROW 52230, N'MODULE_BUSINESS_ACTION 缺少 LABEL / CONFIRM_TAG / PARAM_STRUCT 列，请先执行迁移 218。', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.MODULES WHERE M_IDX = @Module
                 AND LTRIM(RTRIM(ISNULL(MASTER_TABLE, N''))) = N'INV_CHECK_STOCK_M'
                 AND LTRIM(RTRIM(ISNULL(DETAIL_TABLE, N''))) = N'INV_CHECK_STOCK_D')
    THROW 52231, N'模块 130101 形态不符（应为 INV_CHECK_STOCK_M / INV_CHECK_STOCK_D），迁移中止。', 1;

/* 目标顺序号不得被别的按钮占用（同一事件内 SEQ 唯一；覆盖别的按钮是配置事故） */
IF EXISTS (SELECT 1 FROM dbo.MODULE_BUSINESS_ACTION
           WHERE MODULE_ID = @Module AND EVENT_CODE = N'MANUAL' AND SEQ = 3
             AND ISNULL(SOURCE_REF, N'') <> @Source)
    THROW 52232, N'模块 130101 的 MANUAL 顺序号 3 已被其它按钮占用，迁移中止。', 1;

DECLARE @Params NVARCHAR(MAX) =
    N'{"fields":[{"key":"scopeRoot","label":"盘点范围","type":"string","required":true,"maxLength":30}]}';

MERGE dbo.MODULE_BUSINESS_ACTION AS T
USING (SELECT @Module AS MODULE_ID, 3 AS SEQ) AS S
   ON T.MODULE_ID = S.MODULE_ID AND T.EVENT_CODE = N'MANUAL' AND T.SEQ = S.SEQ
WHEN MATCHED THEN
    UPDATE SET T.EFFECT_KEY = @ActionKey, T.EFFECT_NAME = N'换区重盘',
               T.LABEL = N'换区重盘', T.CONFIRM_TAG = 1,
               T.ENABLED = 1, T.FAIL_MODE = N'BLOCK',
               T.CONDITION_STRUCT = NULL, T.PARAM_STRUCT = @Params, T.REVERSE_STRUCT = NULL,
               T.REMARK = N'用户在盘点单上点击：按新盘点范围把明细整单换掉（删既有明细后重插，项次从 1 起排）',
               T.SOURCE_REF = @Source,
               T.LAST_UPDATE_BY = N'DbUp', T.LAST_UPDATE_DATE = SYSDATETIME()
WHEN NOT MATCHED THEN
    INSERT (MODULE_ID, EVENT_CODE, SEQ, EFFECT_KEY, EFFECT_NAME, LABEL, CONFIRM_TAG,
            ENABLED, FAIL_MODE, CONDITION_STRUCT, PARAM_STRUCT, REVERSE_STRUCT, REMARK, SOURCE_REF,
            CREATE_PERSON, CREATE_DATE, LAST_UPDATE_BY, LAST_UPDATE_DATE)
    VALUES (S.MODULE_ID, N'MANUAL', S.SEQ, @ActionKey, N'换区重盘', N'换区重盘', 1,
            1, N'BLOCK', NULL, @Params, NULL,
            N'用户在盘点单上点击：按新盘点范围把明细整单换掉（删既有明细后重插，项次从 1 起排）', @Source,
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
               WHERE a.MODULE_ID = @Module AND a.EVENT_CODE = N'MANUAL' AND a.SEQ = 3
                 AND a.EFFECT_KEY = @ActionKey AND a.ENABLED = 1 AND a.CONFIRM_TAG = 1
                 AND a.LABEL = N'换区重盘' AND a.FAIL_MODE = N'BLOCK'
                 AND CHARINDEX(N'"key":"scopeRoot"', ISNULL(a.PARAM_STRUCT, N'')) > 0)
    THROW 52233, N'「换区重盘」按钮未配置成功，迁移中止。', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.WORKBENCH_MODULE_DIRTY d WHERE d.MODULE_ID = @Module AND d.DIRTY_TAG = 1)
    THROW 52234, N'模块 130101 未标记为待发布，迁移中止。', 1;

PRINT N'== 已为 130101 配上「换区重盘」自定义按钮（需重发布该模块快照后生效）==';
GO
