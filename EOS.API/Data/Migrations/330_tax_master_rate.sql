-- ============================================================================
-- EOS.ERP migration 330: the tax master carries the rate, documents derive it
-- ----------------------------------------------------------------------------
-- Database: EOS.ERP (the single business database of the new system)
--
-- Context: TAX_RATE on a document master is a read-only derived column -- the
-- client never submits it. The server now fills it before writing the master
-- row by looking the document's TAX_ID up in the tax master
-- (EOS.API/Data/Workbench/MasterDerivedColumnFiller.cs). The tax master read 0
-- for every tax, so every derived rate was 0% and the amount recalculation
-- produced zero tax. The rate is a fact of the tax master: maintain it once
-- here and documents follow the tax they select.
--
-- Evidence (non-zero rates across the whole database, grouped by TAX_ID/TAX_RATE):
--   * TAX02 (增值税) = 13 in eight tables -- CLIENT_PRICE_D, COP_ORDER_D,
--     COP_ORDER_M, COP_QUOTE_D, COP_QUOTE_M, COP_SEND_D, COP_SEND_M,
--     PUR_PURCHASE_D -- 873 rows in total, so 13 is TAX02's real rate.
--   * TAX01 (转厂) / TAX05 (不含税) / TAX06 (出口) only ever hold 1, and only in
--     MOC_PRODUCT_IN_D and PUR_DUE_D (2 456 and 90 rows): a placeholder rather
--     than a rate, and those three taxes are tax-free, so 0 stands for them.
--
-- Scope: backfill TAX02 only, and only while the master still reads 0 -- a rate
-- maintained by hand is preserved. Existing documents keep their historic
-- values; only newly saved ones derive the rate.
-- Idempotent: a rerun matches 0 rows. All objects uppercase.
-- ============================================================================

SET NOCOUNT ON;

/* Some metadata tables carry filtered indexes / indexed views; DML needs the
   matching SET options. */
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;

DECLARE @GUARD_MESSAGE NVARCHAR(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, @GUARD_MESSAGE, 1;

IF OBJECT_ID(N'dbo.TAX', N'U') IS NULL
    THROW 50001, N'税别主档 dbo.TAX 不存在，无法回填税率。', 1;

-- Guard: TAX_ID must be unique, otherwise the backfill cannot tell which row to fix.
IF EXISTS (SELECT 1 FROM dbo.TAX GROUP BY TAX_ID HAVING COUNT_BIG(*) > 1)
    THROW 50002, N'税别主档的 TAX_ID 存在重复行，税率回填中止。', 1;

UPDATE dbo.TAX
SET TAX_RATE = 13
WHERE LTRIM(RTRIM(TAX_ID)) = N'TAX02'
  AND ISNULL(TAX_RATE, 0) = 0;

IF @@ROWCOUNT > 1
    THROW 50003, N'税别主档出现多行 TAX02，税率回填中止。', 1;
