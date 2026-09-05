-- ============================================================================
-- EOS.ERP migration 049: metric confirmations for M10c class coverage
-- ----------------------------------------------------------------------------
-- Per-class opening of the controlled metric subset requires at least one
-- business-confirmed metric per definition form:
--   arithmetic-in-aggregate  : sales_gross_margin
--   aggregate-division       : inventory_turnover
--   distinct-count           : order_count
-- plus the remaining sales-domain aggregates sharing the order-detail source:
--   sales_amount_ex / sales_qty / sales_discount
--
-- Sales-domain metrics (source COP_ORDER_D) inherit the approved-orders-only
-- scope confirmed for sales_amount: master CONFIRM_TAG = 1 joined by
-- ORDER_TYPE + ORDER_NO (same structured ROW_FILTER JSON).
-- inventory_turnover reads INV_PRO_DEPOT (stock state table, no document
-- approval concept), so it carries no row filter.
--
-- Naming convention: all uppercase. Idempotent: guarded UPDATE.
-- ============================================================================

SET NOCOUNT ON;

DECLARE @GUARD_MESSAGE NVARCHAR(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, @GUARD_MESSAGE, 1;

IF OBJECT_ID(N'dbo.REPORT_METRIC', N'U') IS NOT NULL
BEGIN
    UPDATE dbo.REPORT_METRIC
    SET [ROW_FILTER] = N'{"table":"COP_ORDER_M","on":["ORDER_TYPE","ORDER_NO"],"conditions":[{"column":"CONFIRM_TAG","op":"=","value":true}]}',
        [CONFIRM_STATUS] = N'CONFIRMED',
        [CONFIRMED_BY] = N'admin',
        [CONFIRMED_DATE] = SYSDATETIME()
    WHERE [METRIC_ID] IN (
            N'sales_amount_ex', N'sales_qty', N'sales_discount',
            N'sales_gross_margin', N'order_count')
      AND ([CONFIRM_STATUS] IS NULL OR [CONFIRM_STATUS] <> N'CONFIRMED');

    UPDATE dbo.REPORT_METRIC
    SET [ROW_FILTER] = NULL,
        [CONFIRM_STATUS] = N'CONFIRMED',
        [CONFIRMED_BY] = N'admin',
        [CONFIRMED_DATE] = SYSDATETIME()
    WHERE [METRIC_ID] = N'inventory_turnover'
      AND ([CONFIRM_STATUS] IS NULL OR [CONFIRM_STATUS] <> N'CONFIRMED');
END
GO
