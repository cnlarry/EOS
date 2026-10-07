-- ============================================================================
-- EOS.ERP migration 331: 月结单（1304）关闭"自动批核" + 批核期要求快照明细非空
-- ----------------------------------------------------------------------------
-- 现象：月结单是**业务单据**，却配着 AUTO_APPROVE=1 ⇒ 建单那一刻就 CONFIRM_TAG=1，也就是
-- **已关账**，而此时 INV_PRO_MONTH_D（快照明细）仍是 0 行；此后点「生成快照」会被拒
-- （400 "该月结单已批核，快照不能再生成（要先反结账）"），必须先解批 → 生成 → 再批核。
-- 空快照被当成一期的后果：月结对账报 **186** 个差异键，比有快照时的 138 还多
-- （快照那条腿为 0，全部余额都被算成差异）。
--
-- 处置（2026-10-07 用户拍板，两项一起做）：
--   ⒜ 关掉 1304 的 AUTO_APPROVE。月结单不是基础数据，"建单"与"审核"是两个不同的权限位；
--      关掉之后官方路径自然变成「建单 → 生成快照 → 批核」，不再需要"先反结账"这种绕法。
--   ⒝ 批核阶段挂一道声明式校验：该期快照明细一行都没有就**拒绝批核**（APPROVE 阶段），
--      判据落在 CONFIRM_TAG 翻转之前，与关账锚点是同一处。
--
-- 注：⒝ 用的是通用规则 detail-required-on-approve（只按模块定义判"明细表按本单主键是否零行"），
-- **不认模块号**；本迁移只是第一个把它挂在 APPROVE 阶段的模块。挂上与关掉自动批核是两件事：
-- ① 保证"没快照就关不了账"；② 保证人工审核这条路确实存在（有关闭自动批核才有得审）。
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
    THROW 52400, @GUARD_MESSAGE, 1;

DECLARE @MonthModule INT = 1304;

IF NOT EXISTS (SELECT 1 FROM dbo.MODULES WHERE M_IDX = @MonthModule
                 AND LTRIM(RTRIM(ISNULL(MASTER_TABLE, ''))) = N'INV_PRO_MONTH_M'
                 AND LTRIM(RTRIM(ISNULL(DETAIL_TABLE, ''))) = N'INV_PRO_MONTH_D')
    THROW 52401, N'模块 1304 形态不符（应为 INV_PRO_MONTH_M / INV_PRO_MONTH_D），迁移中止。', 1;

-- 主表必须有状态位，否则关掉自动批核会让"关账"这件事无处落点
IF COL_LENGTH('dbo.INV_PRO_MONTH_M', 'CONFIRM_TAG') IS NULL
    THROW 52402, N'INV_PRO_MONTH_M 缺少 CONFIRM_TAG，迁移中止。', 1;

/* ---------- ⒜ 关闭自动批核 ---------- */
UPDATE dbo.MODULES
   SET AUTO_APPROVE = 0, LAST_UPDATE_BY = N'DbUp', LAST_UPDATE_DATE = SYSDATETIME()
 WHERE M_IDX = @MonthModule AND ISNULL(AUTO_APPROVE, 0) = 1;

/* ---------- ⒝ 批核期要求快照明细非空 ---------- */
DECLARE @Message NVARCHAR(500) = N'该期快照尚未生成（明细为空），不能关账：请先生成快照。';
DECLARE @Params NVARCHAR(MAX) = N'{"handler":"detail-required-on-approve","check":'
    + N'{"message":"该期快照尚未生成（明细为空），不能关账：请先生成快照。"}}';

IF EXISTS (SELECT 1 FROM dbo.MODULE_VALIDATION_RULE
            WHERE M_IDX = @MonthModule AND STAGE = N'APPROVE' AND SEQ = 1)
    UPDATE dbo.MODULE_VALIDATION_RULE
       SET VALIDATION_KEY = N'custom-validation', ENABLED = 1,
           PARAM_STRUCT = @Params, MESSAGE = @Message,
           REMARK = N'未生成快照（明细为空）不许关账', SOURCE_REF = N'month-close',
           LAST_UPDATE_BY = N'migration-331', LAST_UPDATE_DATE = SYSDATETIME()
     WHERE M_IDX = @MonthModule AND STAGE = N'APPROVE' AND SEQ = 1;
ELSE
    INSERT INTO dbo.MODULE_VALIDATION_RULE
        (M_IDX, STAGE, SEQ, VALIDATION_KEY, ENABLED, PARAM_STRUCT, MESSAGE, REMARK, SOURCE_REF,
         CREATE_PERSON, CREATE_DATE)
    VALUES
        (@MonthModule, N'APPROVE', 1, N'custom-validation', 1, @Params, @Message,
         N'未生成快照（明细为空）不许关账', N'month-close', N'migration-331', SYSDATETIME());

/* ---------- 标脏待发布（定义快照要重发布才生效） ---------- */
MERGE dbo.WORKBENCH_MODULE_DIRTY AS T
USING (SELECT @MonthModule AS M_IDX) AS S ON T.M_IDX = S.M_IDX
WHEN MATCHED THEN UPDATE SET DIRTY_TAG = 1, LAST_MODIFIED_BY = N'DbUp', LAST_MODIFIED_AT = SYSDATETIME()
WHEN NOT MATCHED THEN INSERT (M_IDX, DIRTY_TAG, LAST_MODIFIED_BY, LAST_MODIFIED_AT)
    VALUES (S.M_IDX, 1, N'DbUp', SYSDATETIME());

/* ---------- 收口断言 ---------- */
IF EXISTS (SELECT 1 FROM dbo.MODULES WHERE M_IDX = @MonthModule AND ISNULL(AUTO_APPROVE, 0) <> 0)
    THROW 52403, N'模块 1304 仍处于自动批核，迁移中止。', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.MODULE_VALIDATION_RULE
                WHERE M_IDX = @MonthModule AND STAGE = N'APPROVE' AND SEQ = 1
                  AND VALIDATION_KEY = N'custom-validation' AND ISNULL(ENABLED, 0) = 1)
    THROW 52404, N'模块 1304 未挂上"批核期快照闸"，迁移中止。', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.WORKBENCH_MODULE_DIRTY WHERE M_IDX = @MonthModule AND DIRTY_TAG = 1)
    THROW 52405, N'模块 1304 未标记为待发布，迁移中止。', 1;

PRINT N'== 月结单已关闭自动批核（建单不再即关账），并挂上"未生成快照不许批核"闸，模块待发布 ==';
