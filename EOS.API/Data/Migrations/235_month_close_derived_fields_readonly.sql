-- ============================================================================
-- EOS.ERP migration 236: 月结明细的派生列置只读，库位 / 批次列保持可见
-- ----------------------------------------------------------------------------
-- 月结明细上的数量与单价是**生成快照算出来的派生值**（时间点结存与移动加权单价），
-- 不是人手填的。手能改的话，快照就不再是"那个时点的期末值"，而只是"某人写下的一个数"——
-- 报表期初会随之漂移，且事后无从分辨。
--
-- 因此把这两列在字段元数据上置为只读；库位与批次是快照的维度键（按参数展开时它们决定行数），
-- 必须看得见，否则用户无法判断这张快照是按哪几个维度生成的。
--
-- 本迁移只动字段元数据，不动表结构，也不改任何已生成的行。
--
-- 幂等：只读 / 可见位按目标值写入，重复执行无副作用。
-- 回滚：把 INV_PRO_MONTH_D.QTY / PRICE 的 IS_READONLY 置回 0。
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
    THROW 52500, @GUARD_MESSAGE, 1;

IF COL_LENGTH(N'dbo.FIELDS', N'IS_READONLY') IS NULL OR COL_LENGTH(N'dbo.FIELDS', N'IS_VISIBLE') IS NULL
    THROW 52501, N'FIELDS 缺少 IS_READONLY / IS_VISIBLE 列，迁移中止。', 1;

/* 元数据行必须先存在：本迁移只改标志，不凭空造字段行（造出来也不会与表结构对得上） */
IF NOT EXISTS (SELECT 1 FROM dbo.FIELDS WHERE RTRIM(T_ID) = N'INV_PRO_MONTH_D' AND RTRIM(F_ID) IN (N'QTY', N'PRICE'))
    THROW 52502, N'INV_PRO_MONTH_D 的 QTY / PRICE 字段元数据行不存在，迁移中止。', 1;

/* ---------- ① 派生列置只读 ---------- */
UPDATE dbo.FIELDS
   SET IS_READONLY = 1, LAST_UPDATE_BY = N'DbUp', LAST_UPDATE_DATE = GETDATE()
 WHERE RTRIM(T_ID) = N'INV_PRO_MONTH_D' AND RTRIM(F_ID) IN (N'QTY', N'PRICE')
   AND ISNULL(IS_READONLY, 0) = 0;

/* ---------- ② 维度列保持可见 ---------- */
/* 库位 / 批次是快照的维度键：按参数展开时行数由它们决定，看不见就无法判断快照粒度。
   这两列不是派生值，仍可人工指定（用于按库位 / 批次补录）。 */
UPDATE dbo.FIELDS
   SET IS_VISIBLE = 1, LAST_UPDATE_BY = N'DbUp', LAST_UPDATE_DATE = GETDATE()
 WHERE RTRIM(T_ID) = N'INV_PRO_MONTH_D' AND RTRIM(F_ID) IN (N'LOCATION_NO', N'BATCH_NO')
   AND ISNULL(IS_VISIBLE, 0) = 0;

/* ---------- ③ 重发布信号 ---------- */
MERGE dbo.WORKBENCH_MODULE_DIRTY AS D
USING (SELECT 1304 AS MODULE_ID) AS S
   ON D.MODULE_ID = S.MODULE_ID
WHEN MATCHED THEN UPDATE SET D.DIRTY_TAG = 1, D.LAST_MODIFIED_BY = N'DbUp', D.LAST_MODIFIED_AT = SYSDATETIME()
WHEN NOT MATCHED THEN INSERT (MODULE_ID, DIRTY_TAG, LAST_MODIFIED_BY, LAST_MODIFIED_AT)
    VALUES (S.MODULE_ID, 1, N'DbUp', SYSDATETIME());

/* ---------- ④ 核对断言 ---------- */
IF EXISTS (SELECT 1 FROM dbo.FIELDS
            WHERE RTRIM(T_ID) = N'INV_PRO_MONTH_D' AND RTRIM(F_ID) IN (N'QTY', N'PRICE')
              AND ISNULL(IS_READONLY, 0) = 0)
    THROW 52503, N'月结明细的数量 / 单价未置为只读，迁移中止。', 1;

IF EXISTS (SELECT 1 FROM dbo.FIELDS
            WHERE RTRIM(T_ID) = N'INV_PRO_MONTH_D' AND RTRIM(F_ID) IN (N'LOCATION_NO', N'BATCH_NO')
              AND ISNULL(IS_VISIBLE, 0) = 0)
    THROW 52504, N'月结明细的库位 / 批次列不可见，迁移中止。', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.WORKBENCH_MODULE_DIRTY WHERE MODULE_ID = 1304 AND DIRTY_TAG = 1)
    THROW 52505, N'模块 1304 未标记为待发布，迁移中止。', 1;

PRINT N'== 月结明细的数量 / 单价已置只读，库位 / 批次列可见（需重发布模块 1304 生效）==';
GO
