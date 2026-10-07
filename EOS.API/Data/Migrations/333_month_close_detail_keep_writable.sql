-- ============================================================================
-- EOS.ERP migration 333: 月结单明细的可写性恢复原状，只保留"取消必填"
-- ----------------------------------------------------------------------------
-- 背景：332 为了修"新建月结单保存被必填校验拒"（报障：DEPOT_ID / BATCH_NO 必填未满足），
--   把 INV_PRO_MONTH_D 的字段**全部**置为只读。那一步越过了既有口径 ——
--   MonthCloseFieldMetadataLiveTests 明确钉住两条：
--     ⒜ 派生列（QTY / PRICE）**必须只读**：它们是生成快照算出的期末值与加权单价，人改就不再是那一期的值；
--     ⒝ 维度列（LOCATION_NO / BATCH_NO）**必须可见且仍可人工指定**：快照粒度由维度键决定，
--        补录时仍需要人工指定维度。
--
-- 处置：把只读**收敛回 332 之前的状态**（该可写的仍可写），**只保留 332 的"取消必填"**——
--   真正堵住"建单"那一步的是必填校验，不是可写性；把可写性一并砍掉属于顺手扩大改动面。
--   手工录明细由前端侧解决（新增且明细为空时不提交 details、空白新行不进提交体，见迁移 332 的注释与 40 篇）。
--
-- 保留项：INV_PRO_MONTH_D 全部字段 IS_VERIFY = 0（新建时不再因明细必填被拒）。
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
    THROW 52600, @GUARD_MESSAGE, 1;

DECLARE @DetailTable NVARCHAR(100) = N'INV_PRO_MONTH_D';
DECLARE @MonthModule INT = 1304;

IF NOT EXISTS (SELECT 1 FROM dbo.MODULES WHERE M_IDX = @MonthModule
                 AND LTRIM(RTRIM(ISNULL(DETAIL_TABLE, ''))) = @DetailTable)
    THROW 52601, N'模块 1304 形态不符（明细表应为 INV_PRO_MONTH_D），迁移中止。', 1;

-- 332 之前**本来就是可写**的那些字段（含主键列与两个维度列）。
DECLARE @Writable TABLE (F_ID NVARCHAR(100) PRIMARY KEY);
INSERT INTO @Writable (F_ID) VALUES
    (N'MONTH_TYPE'), (N'MONTH_NO'), (N'SERIAL_NO'),
    (N'PRO_NO'), (N'DEPOT_ID'), (N'LOCATION_NO'), (N'BATCH_NO'), (N'AMOUNT');

UPDATE f
   SET IS_READONLY = 0,
       LAST_UPDATE_BY = N'migration-333',
       LAST_UPDATE_DATE = SYSDATETIME()
  FROM dbo.FIELDS f
  JOIN @Writable w ON w.F_ID = LTRIM(RTRIM(f.F_ID))
 WHERE LTRIM(RTRIM(f.T_ID)) = @DetailTable
   AND ISNULL(f.IS_READONLY, 0) = 1;

/* ---------- 标脏待发布 ---------- */
MERGE dbo.WORKBENCH_MODULE_DIRTY AS T
USING (SELECT @MonthModule AS M_IDX) AS S ON T.M_IDX = S.M_IDX
WHEN MATCHED THEN UPDATE SET DIRTY_TAG = 1, LAST_MODIFIED_BY = N'DbUp', LAST_MODIFIED_AT = SYSDATETIME()
WHEN NOT MATCHED THEN INSERT (M_IDX, DIRTY_TAG, LAST_MODIFIED_BY, LAST_MODIFIED_AT)
    VALUES (S.M_IDX, 1, N'DbUp', SYSDATETIME());

/* ---------- 收口断言 ---------- */
-- 维度列与主键列必须回到可写（既有口径：补录时仍要人工指定维度）
IF EXISTS (SELECT 1 FROM dbo.FIELDS f JOIN @Writable w ON w.F_ID = LTRIM(RTRIM(f.F_ID))
            WHERE LTRIM(RTRIM(f.T_ID)) = @DetailTable AND ISNULL(f.IS_READONLY, 0) = 1)
    THROW 52602, N'仍有应可写的字段处于只读，迁移中止。', 1;

-- 派生列必须仍是只读（332 之前的口径，不能被这次回滚带走）
IF EXISTS (SELECT 1 FROM dbo.FIELDS
            WHERE LTRIM(RTRIM(T_ID)) = @DetailTable
              AND LTRIM(RTRIM(F_ID)) IN (N'QTY', N'PRICE')
              AND ISNULL(IS_READONLY, 0) = 0)
    THROW 52603, N'派生列 QTY / PRICE 变成可写了，迁移中止。', 1;

-- 取消必填这一条必须保住（这才是"新建保存被拒"的解药）
IF EXISTS (SELECT 1 FROM dbo.FIELDS WHERE LTRIM(RTRIM(T_ID)) = @DetailTable AND ISNULL(IS_VERIFY, 0) = 1)
    THROW 52604, N'INV_PRO_MONTH_D 又出现必填字段，迁移中止。', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.WORKBENCH_MODULE_DIRTY WHERE M_IDX = @MonthModule AND DIRTY_TAG = 1)
    THROW 52605, N'模块 1304 未标记为待发布，迁移中止。', 1;

PRINT N'== 月结单明细可写性已恢复原状（派生列仍只读），必填保持取消，模块待发布 ==';
