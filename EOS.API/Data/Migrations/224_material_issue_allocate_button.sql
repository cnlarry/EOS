-- ============================================================================
-- EOS.ERP migration 225: 领料单族（1503/1514/1517/2805/2806）的自定义按钮「按库别取料」
-- ----------------------------------------------------------------------------
-- 旧系统这一步是领料单上的一个按钮（`MOC/Get.aspx.cs` 的 `btnDepotGet`），
-- 调 `P_MOC_GET_DEPOT` 按各行库别的当前库存重算可发料数量，并把待办表逐行分配。
-- 本迁移把同一动作接到 EVENT_CODE='MANUAL' 的配置行上（每模块 SEQ = 1）：
--     EFFECT_KEY   = 'material-issue-allocate'（由代码闭集内的处理器实现）
--     CONFIRM_TAG  = 1（先返回"将会发生什么"，用户确认才写库）
--     CONDITION_STRUCT = NULL：取数定量的正常时机在批核之前，不设"已批核才能点"。
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

DECLARE @ActionKey NVARCHAR(50) = N'material-issue-allocate';
DECLARE @Source NVARCHAR(100) = N'document-action';

IF COL_LENGTH(N'dbo.MODULE_BUSINESS_ACTION', N'LABEL') IS NULL
    OR COL_LENGTH(N'dbo.MODULE_BUSINESS_ACTION', N'CONFIRM_TAG') IS NULL
    THROW 52250, N'MODULE_BUSINESS_ACTION 缺少 LABEL / CONFIRM_TAG 列，请先执行迁移 218。', 1;

/* 五个领料模块必须同为 MOC_GET_M / MOC_GET_D 主从形态，否则按钮口径不对 */
IF EXISTS (SELECT 1 FROM dbo.MODULES WHERE M_IDX IN (1503, 1514, 1517, 2805, 2806)
           AND (LTRIM(RTRIM(ISNULL(MASTER_TABLE, N''))) <> N'MOC_GET_M'
                OR LTRIM(RTRIM(ISNULL(DETAIL_TABLE, N''))) <> N'MOC_GET_D'))
    THROW 52251, N'领料模块形态不符（应为 MOC_GET_M / MOC_GET_D），迁移中止。', 1;

/* 目标顺序号不得被别的按钮占用（同一事件内 SEQ 唯一；覆盖别的按钮是配置事故） */
IF EXISTS (SELECT 1 FROM dbo.MODULE_BUSINESS_ACTION
           WHERE MODULE_ID IN (1503, 1514, 1517, 2805, 2806) AND EVENT_CODE = N'MANUAL' AND SEQ = 1
             AND ISNULL(SOURCE_REF, N'') <> @Source)
    THROW 52252, N'领料模块的 MANUAL 顺序号 1 已被其它按钮占用，迁移中止。', 1;

MERGE dbo.MODULE_BUSINESS_ACTION AS T
USING (SELECT M_IDX AS MODULE_ID FROM dbo.MODULES WHERE M_IDX IN (1503, 1514, 1517, 2805, 2806)) AS S
   ON T.MODULE_ID = S.MODULE_ID AND T.EVENT_CODE = N'MANUAL' AND T.SEQ = 1
WHEN MATCHED THEN
    UPDATE SET T.EFFECT_KEY = @ActionKey, T.EFFECT_NAME = N'按库别取料',
               T.LABEL = N'按库别取料', T.CONFIRM_TAG = 1,
               T.ENABLED = 1, T.FAIL_MODE = N'BLOCK',
               T.CONDITION_STRUCT = NULL, T.PARAM_STRUCT = NULL, T.REVERSE_STRUCT = NULL,
               T.REMARK = N'用户在领料单上点击：按各行库别的当前库存重算可发料数量，并把待办表逐行分配',
               T.SOURCE_REF = @Source,
               T.LAST_UPDATE_BY = N'DbUp', T.LAST_UPDATE_DATE = SYSDATETIME()
WHEN NOT MATCHED THEN
    INSERT (MODULE_ID, EVENT_CODE, SEQ, EFFECT_KEY, EFFECT_NAME, LABEL, CONFIRM_TAG,
            ENABLED, FAIL_MODE, CONDITION_STRUCT, PARAM_STRUCT, REVERSE_STRUCT, REMARK, SOURCE_REF,
            CREATE_PERSON, CREATE_DATE, LAST_UPDATE_BY, LAST_UPDATE_DATE)
    VALUES (S.MODULE_ID, N'MANUAL', 1, @ActionKey, N'按库别取料', N'按库别取料', 1,
            1, N'BLOCK', NULL, NULL, NULL,
            N'用户在领料单上点击：按各行库别的当前库存重算可发料数量，并把待办表逐行分配', @Source,
            N'DbUp', SYSDATETIME(), N'DbUp', SYSDATETIME());

/* 运行时读的是已发布快照：配置改了必须重发布才生效 */
MERGE dbo.WORKBENCH_MODULE_DIRTY AS D
USING (SELECT M_IDX AS MODULE_ID FROM dbo.MODULES WHERE M_IDX IN (1503, 1514, 1517, 2805, 2806)) AS S
   ON D.MODULE_ID = S.MODULE_ID
WHEN MATCHED THEN UPDATE SET D.DIRTY_TAG = 1, D.LAST_MODIFIED_BY = N'DbUp', D.LAST_MODIFIED_AT = SYSDATETIME()
WHEN NOT MATCHED THEN INSERT (MODULE_ID, DIRTY_TAG, LAST_MODIFIED_BY, LAST_MODIFIED_AT)
    VALUES (S.MODULE_ID, 1, N'DbUp', SYSDATETIME());

/* ---------- 收口断言 ---------- */
IF (SELECT COUNT(*) FROM dbo.MODULE_BUSINESS_ACTION a
    WHERE a.MODULE_ID IN (1503, 1514, 1517, 2805, 2806) AND a.EVENT_CODE = N'MANUAL' AND a.SEQ = 1
      AND a.EFFECT_KEY = @ActionKey AND a.ENABLED = 1 AND a.CONFIRM_TAG = 1
      AND a.LABEL = N'按库别取料' AND a.FAIL_MODE = N'BLOCK') <> 5
    THROW 52253, N'「按库别取料」按钮未在 5 个领料模块配齐，迁移中止。', 1;

IF (SELECT COUNT(*) FROM dbo.WORKBENCH_MODULE_DIRTY d
    WHERE d.MODULE_ID IN (1503, 1514, 1517, 2805, 2806) AND d.DIRTY_TAG = 1) <> 5
    THROW 52254, N'领料模块未全部标记为待发布，迁移中止。', 1;

PRINT N'== 已为领料单族配上「按库别取料」自定义按钮（需重发布这些模块快照后生效）==';
GO
