-- dbo.INV_OCCUR_IN_M.LAST_UPDATE_BY 恢复为可空。
--
-- 口径：单据生命周期列分两类——**状态位与建立组**必须 NOT NULL + DEFAULT（它们记录"单据已成立"
-- 这一既有事实），**经办人与日期列永久可空**（NULL 表示"该事件从未发生"，NOT NULL 会逼出一批
-- 无意义的哨兵值，把"没发生过"与"发生过但操作人未知"混成同一个值）。
--
-- 本列是单点漂移：本表建为非空，而同族的 INV_OCCUR_OUT_M / INV_PRO_DEPOT / INV_PRO_MONTH_M /
-- INV_BATCH_M 的 LAST_UPDATE_BY 都是可空。它不来自任何迁移——是随既有库结构导入的非空声明，
-- 因此由本迁移放宽。
--
-- 只放宽可空性：不改列类型（原样显式写全 NCHAR(20)）、不动数据、不加也不删默认值，不改索引
-- 与约束（实查该列上没有默认约束、没有索引列、没有检查约束）。
--
-- 幂等：已是可空视为已落地，打印一行说明后跳过（不重复执行 DDL）；列不存在或类型不是
-- NCHAR(20) 说明库结构与本迁移的预期不符，报错而不是静默跳过。

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @GUARD_MESSAGE NVARCHAR(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, @GUARD_MESSAGE, 1;

BEGIN TRANSACTION;

-- ① 前置自证：列存在，且类型与长度就是 NCHAR(20)（ALTER COLUMN 必须显式写全类型，
--    写错了会顺手改掉列类型，故先核对再改）。
IF COL_LENGTH('dbo.INV_OCCUR_IN_M', 'LAST_UPDATE_BY') IS NULL
    THROW 55000, N'dbo.INV_OCCUR_IN_M.LAST_UPDATE_BY 不存在，与本迁移的预期不符。', 1;

IF NOT EXISTS (
    SELECT 1
    FROM sys.columns c
    JOIN sys.objects o ON o.object_id = c.object_id
    WHERE o.name = N'INV_OCCUR_IN_M' AND o.type = N'U'
      AND c.name = N'LAST_UPDATE_BY'
      AND TYPE_NAME(c.user_type_id) = N'nchar' AND c.max_length = 40
)
    THROW 55001, N'dbo.INV_OCCUR_IN_M.LAST_UPDATE_BY 的类型不是 NCHAR(20)，与本迁移的预期不符。', 1;

-- ② 放宽可空性（已可空即跳过）
IF EXISTS (
    SELECT 1
    FROM sys.columns c
    JOIN sys.objects o ON o.object_id = c.object_id
    WHERE o.name = N'INV_OCCUR_IN_M' AND c.name = N'LAST_UPDATE_BY' AND c.is_nullable = 0
)
    ALTER TABLE dbo.INV_OCCUR_IN_M ALTER COLUMN LAST_UPDATE_BY NCHAR(20) NULL;
ELSE
    PRINT N'dbo.INV_OCCUR_IN_M.LAST_UPDATE_BY 已是可空，跳过（本迁移已落地过）。';

-- ③ 后置自证：改完必须可空
IF EXISTS (
    SELECT 1
    FROM sys.columns c
    JOIN sys.objects o ON o.object_id = c.object_id
    WHERE o.name = N'INV_OCCUR_IN_M' AND c.name = N'LAST_UPDATE_BY' AND c.is_nullable = 0
)
    THROW 55002, N'dbo.INV_OCCUR_IN_M.LAST_UPDATE_BY 仍为非空：ALTER COLUMN 未生效。', 1;

COMMIT TRANSACTION;
