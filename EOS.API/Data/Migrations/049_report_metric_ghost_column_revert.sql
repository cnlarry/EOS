-- ============================================================================
-- EOS.ERP migration 050: revert metrics whose definitions reference missing columns
-- ----------------------------------------------------------------------------
-- Two seed metrics reference columns that do not exist on their source tables
-- (re-verified against sys.columns at confirmation time):
--   sales_gross_margin  : SUM(AMOUNT_TAX - COST_AMOUNT) on COP_ORDER_D —
--                         no COST_AMOUNT column exists on COP_ORDER_D/M/MORE;
--                         the order-cost source needs business clarification.
--   inventory_turnover  : SUM(QTY) / NULLIF(SUM(IN_QTY),0) on INV_PRO_DEPOT —
--                         no IN_QTY column exists (available: QTY/INIT_QTY/
--                         USEABLE_QTY); the turnover formula needs business
--                         clarification.
-- Their computation is blocked by the validator (deterministic refusal), so the
-- confirmation flags are reverted to CANDIDATE until the definitions are fixed
-- by a developer-owned migration.
-- Naming convention: all uppercase. Idempotent: guarded UPDATE.
-- ============================================================================

SET NOCOUNT ON;

DECLARE @GUARD_MESSAGE NVARCHAR(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, @GUARD_MESSAGE, 1;

IF OBJECT_ID(N'dbo.REPORT_METRIC', N'U') IS NOT NULL
BEGIN
    UPDATE dbo.REPORT_METRIC
    SET [CONFIRM_STATUS] = N'CANDIDATE',
        [CONFIRMED_BY] = NULL,
        [CONFIRMED_DATE] = NULL,
        [ROW_FILTER] = NULL
    WHERE [METRIC_ID] IN (N'sales_gross_margin', N'inventory_turnover')
      AND [CONFIRM_STATUS] = N'CONFIRMED';
END
GO
