-- ============================================================================
-- EOS.ERP migration 052: domain metric confirmations (second M10c batch)
-- ----------------------------------------------------------------------------
-- Opens five more metrics whose aggregate/dimension columns were re-verified
-- against sys.columns before this migration:
--   document-based (approved-documents-only scope, same as the sales domain):
--     account_receivable : COP_ACCOUNT_D.AMOUNT_TAX  / COP_ACCOUNT_M.CONFIRM_TAG
--     payment_amount     : PUR_PAY_D.AMOUNT          / PUR_PAY_M.CONFIRM_TAG
--     purchase_amount    : PUR_PURCHASE_D.AMOUNT_TAX / PUR_PURCHASE_M.CONFIRM_TAG
--   state/master data (no document approval concept, no row filter):
--     employee_count     : HR_EMPLOYEE
--     inventory_qty      : INV_PRO_DEPOT
--
-- Five seed metrics remain CANDIDATE and are NOT touched here: their
-- DEFINITION references columns that do not exist (sales_gross_margin
-- COST_AMOUNT; inventory_turnover IN_QTY; receive_qty RECEIVE_QTY on
-- PUR_RECEIVE_D; produce_qty / produce_finished_qty QTY / FINISHED_QTY on
-- MOC_PRODUCE_D, which is a component-usage table). They stay refused until
-- the business clarifies their real definitions.
-- Naming convention: all uppercase. Idempotent: guarded UPDATE.
-- ============================================================================

SET NOCOUNT ON;

DECLARE @GUARD_MESSAGE NVARCHAR(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, @GUARD_MESSAGE, 1;

IF OBJECT_ID(N'dbo.REPORT_METRIC', N'U') IS NOT NULL
BEGIN
    UPDATE dbo.REPORT_METRIC
    SET [ROW_FILTER] = N'{"table":"COP_ACCOUNT_M","on":["ACCOUNT_TYPE","ACCOUNT_NO"],"conditions":[{"column":"CONFIRM_TAG","op":"=","value":true}]}',
        [CONFIRM_STATUS] = N'CONFIRMED',
        [CONFIRMED_BY] = N'admin',
        [CONFIRMED_DATE] = SYSDATETIME()
    WHERE [METRIC_ID] = N'account_receivable'
      AND ([CONFIRM_STATUS] IS NULL OR [CONFIRM_STATUS] <> N'CONFIRMED');

    UPDATE dbo.REPORT_METRIC
    SET [ROW_FILTER] = N'{"table":"PUR_PAY_M","on":["PAY_TYPE","PAY_NO"],"conditions":[{"column":"CONFIRM_TAG","op":"=","value":true}]}',
        [CONFIRM_STATUS] = N'CONFIRMED',
        [CONFIRMED_BY] = N'admin',
        [CONFIRMED_DATE] = SYSDATETIME()
    WHERE [METRIC_ID] = N'payment_amount'
      AND ([CONFIRM_STATUS] IS NULL OR [CONFIRM_STATUS] <> N'CONFIRMED');

    UPDATE dbo.REPORT_METRIC
    SET [ROW_FILTER] = N'{"table":"PUR_PURCHASE_M","on":["PURCHASE_TYPE","PURCHASE_NO"],"conditions":[{"column":"CONFIRM_TAG","op":"=","value":true}]}',
        [CONFIRM_STATUS] = N'CONFIRMED',
        [CONFIRMED_BY] = N'admin',
        [CONFIRMED_DATE] = SYSDATETIME()
    WHERE [METRIC_ID] = N'purchase_amount'
      AND ([CONFIRM_STATUS] IS NULL OR [CONFIRM_STATUS] <> N'CONFIRMED');

    UPDATE dbo.REPORT_METRIC
    SET [ROW_FILTER] = NULL,
        [CONFIRM_STATUS] = N'CONFIRMED',
        [CONFIRMED_BY] = N'admin',
        [CONFIRMED_DATE] = SYSDATETIME()
    WHERE [METRIC_ID] IN (N'employee_count', N'inventory_qty')
      AND ([CONFIRM_STATUS] IS NULL OR [CONFIRM_STATUS] <> N'CONFIRMED');
END
GO
