-- ============================================================================
-- EOS.ERP migration 232: 料件每月统计单（1304）的自定义按钮「生成快照」
-- ----------------------------------------------------------------------------
-- 月结快照的生成入口：按本单的期末日期**反算**该时点结存（上一期已批核快照 + 其后到本期末的
-- 流水净额），按部署级策略参数（MONTH_CLOSE_BY_LOCATION / MONTH_CLOSE_BY_BATCH）决定的行粒度
-- 展开，写成 INV_PRO_MONTH_D 的明细行。
--
-- 本迁移把动作接到 EVENT_CODE='MANUAL' 的配置行上：
--     EFFECT_KEY  = 'month-close-snapshot'（由代码闭集内的处理器实现）
--     CONFIRM_TAG = 1（先返回"将会写多少行"，用户确认才写库——重复点击是重算，会先删本级明细）
--     PARAM_STRUCT = NULL：粒度来自策略参数而不是用户临时填的东西；期末日期也来自本单，不需要参数
--
-- 为什么按钮级授权行不在这里发：按钮授权是 fail-closed 名单（表 SYSDD_BUTTON / SYSDH_BUTTON），
-- 由管理员在按钮权限里授予。迁移里发授权等于把授权写进结构，之后没人能通过界面收回。
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
    THROW 52320, @GUARD_MESSAGE, 1;

DECLARE @Module INT = 1304;
DECLARE @ActionKey NVARCHAR(50) = N'month-close-snapshot';
DECLARE @Source NVARCHAR(100) = N'document-action';
DECLARE @Label NVARCHAR(50) = N'生成快照';
DECLARE @Remark NVARCHAR(400) =
    N'用户在月结单上点击：按本单期末日期反算该时点结存，按策略参数展开成快照明细（已批核的单拒绝生成）';

IF COL_LENGTH(N'dbo.MODULE_BUSINESS_ACTION', N'LABEL') IS NULL
    OR COL_LENGTH(N'dbo.MODULE_BUSINESS_ACTION', N'CONFIRM_TAG') IS NULL
    OR COL_LENGTH(N'dbo.MODULE_BUSINESS_ACTION', N'PARAM_STRUCT') IS NULL
    THROW 52321, N'MODULE_BUSINESS_ACTION 缺少 LABEL / CONFIRM_TAG / PARAM_STRUCT 列，请先执行迁移 218。', 1;

/* 模块形态：主表/明细表必须就是月结单头与快照明细——接错模块会让按钮出现在不该出现的单据上 */
IF NOT EXISTS (SELECT 1 FROM dbo.MODULES WHERE M_IDX = @Module
                 AND LTRIM(RTRIM(ISNULL(MASTER_TABLE, N''))) = N'INV_PRO_MONTH_M'
                 AND LTRIM(RTRIM(ISNULL(DETAIL_TABLE, N''))) = N'INV_PRO_MONTH_D')
    THROW 52322, N'模块 1304 形态不符（应为 INV_PRO_MONTH_M / INV_PRO_MONTH_D），迁移中止。', 1;

/* 目标顺序号不得被别的按钮占用（同一事件内 SEQ 唯一；覆盖别的按钮是配置事故） */
IF EXISTS (SELECT 1 FROM dbo.MODULE_BUSINESS_ACTION
           WHERE MODULE_ID = @Module AND EVENT_CODE = N'MANUAL' AND SEQ = 1
             AND ISNULL(SOURCE_REF, N'') <> @Source)
    THROW 52323, N'模块 1304 的 MANUAL 顺序号 1 已被其它按钮占用，迁移中止。', 1;

MERGE dbo.MODULE_BUSINESS_ACTION AS T
USING (SELECT @Module AS MODULE_ID, 1 AS SEQ) AS S
   ON T.MODULE_ID = S.MODULE_ID AND T.EVENT_CODE = N'MANUAL' AND T.SEQ = S.SEQ
WHEN MATCHED THEN
    UPDATE SET T.EFFECT_KEY = @ActionKey, T.EFFECT_NAME = @Label,
               T.LABEL = @Label, T.CONFIRM_TAG = 1,
               T.ENABLED = 1, T.FAIL_MODE = N'BLOCK',
               T.CONDITION_STRUCT = NULL, T.PARAM_STRUCT = NULL, T.REVERSE_STRUCT = NULL,
               T.REMARK = @Remark, T.SOURCE_REF = @Source,
               T.LAST_UPDATE_BY = N'DbUp', T.LAST_UPDATE_DATE = SYSDATETIME()
WHEN NOT MATCHED THEN
    INSERT (MODULE_ID, EVENT_CODE, SEQ, EFFECT_KEY, EFFECT_NAME, LABEL, CONFIRM_TAG,
            ENABLED, FAIL_MODE, CONDITION_STRUCT, PARAM_STRUCT, REVERSE_STRUCT, REMARK, SOURCE_REF,
            CREATE_PERSON, CREATE_DATE, LAST_UPDATE_BY, LAST_UPDATE_DATE)
    VALUES (S.MODULE_ID, N'MANUAL', S.SEQ, @ActionKey, @Label, @Label, 1,
            1, N'BLOCK', NULL, NULL, NULL, @Remark, @Source,
            N'DbUp', SYSDATETIME(), N'DbUp', SYSDATETIME());

/* 运行时读的是已发布快照：配置改了必须重发布才生效 */
MERGE dbo.WORKBENCH_MODULE_DIRTY AS D
USING (SELECT @Module AS MODULE_ID) AS S
   ON D.MODULE_ID = S.MODULE_ID
WHEN MATCHED THEN UPDATE SET D.DIRTY_TAG = 1, D.LAST_MODIFIED_BY = N'DbUp', D.LAST_MODIFIED_AT = SYSDATETIME()
WHEN NOT MATCHED THEN INSERT (MODULE_ID, DIRTY_TAG, LAST_MODIFIED_BY, LAST_MODIFIED_AT)
    VALUES (S.MODULE_ID, 1, N'DbUp', SYSDATETIME());

/* ---------- 核对断言 ---------- */
IF NOT EXISTS (SELECT 1 FROM dbo.MODULE_BUSINESS_ACTION a
               WHERE a.MODULE_ID = @Module AND a.EVENT_CODE = N'MANUAL' AND a.SEQ = 1
                 AND a.EFFECT_KEY = @ActionKey AND a.ENABLED = 1 AND a.CONFIRM_TAG = 1
                 AND a.LABEL = @Label AND a.FAIL_MODE = N'BLOCK')
    THROW 52324, N'「生成快照」按钮未配置成功，迁移中止。', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.WORKBENCH_MODULE_DIRTY d WHERE d.MODULE_ID = @Module AND d.DIRTY_TAG = 1)
    THROW 52325, N'模块 1304 未标记为待发布，迁移中止。', 1;

PRINT N'== 已为 1304 配上「生成快照」自定义按钮（需重发布该模块快照后生效；按钮授权另由管理员授予）==';
GO
