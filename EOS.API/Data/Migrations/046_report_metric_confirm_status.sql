-- ============================================================================
-- EOS.ERP migration 046: REPORT_METRIC confirmation status + structured row filter
-- ----------------------------------------------------------------------------
-- A metric definition is only trusted for assistant computation after business
-- confirmation. CONFIRM_STATUS = CANDIDATE (default) / CONFIRMED; CANDIDATE
-- metrics are refused by resolve_metric.
--
-- ROW_FILTER carries the metric's row scope as structured JSON (never SQL text):
--   {"table":"COP_ORDER_M","on":["ORDER_TYPE","ORDER_NO"],
--    "conditions":[{"column":"CONFIRM_TAG","op":"=","value":true}]}
-- table  : the table the conditions apply to (the source table itself when null)
-- on     : join columns between the filter table and the source table (required
--          when the filter table differs; must exist on both sides)
-- column : condition column, must be permission-visible on the filter table
-- op     : one of = <> > < >= <= ; value: scalar (number/string/boolean)
--
-- Naming convention: all uppercase. Idempotent: IF NOT EXISTS guards.
-- Batch note: statements referencing the new columns live in their own batches
-- (columns added via ALTER TABLE are not visible later in the same batch).
-- ============================================================================

SET NOCOUNT ON;

DECLARE @GUARD_MESSAGE NVARCHAR(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, @GUARD_MESSAGE, 1;

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.REPORT_METRIC') AND name = N'CONFIRM_STATUS')
BEGIN
    ALTER TABLE dbo.REPORT_METRIC ADD
        [CONFIRM_STATUS] NVARCHAR(20) NOT NULL
            CONSTRAINT [DF_REPORT_METRIC_CONFIRM_STATUS] DEFAULT (N'CANDIDATE'),
        [CONFIRMED_BY] NCHAR(40) NULL,
        [CONFIRMED_DATE] DATETIME2 NULL;
END

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.REPORT_METRIC') AND name = N'ROW_FILTER')
BEGIN
    ALTER TABLE dbo.REPORT_METRIC ADD [ROW_FILTER] NVARCHAR(MAX) NULL;
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_REPORT_METRIC_CONFIRM_STATUS')
BEGIN
    ALTER TABLE dbo.REPORT_METRIC ADD CONSTRAINT [CK_REPORT_METRIC_CONFIRM_STATUS]
        CHECK ([CONFIRM_STATUS] IN (N'CANDIDATE', N'CONFIRMED'));
END
GO

-- sales_amount (sales amount incl. tax) is business-confirmed to count
-- approved orders only: master CONFIRM_TAG = 1, joined by ORDER_TYPE + ORDER_NO.
IF OBJECT_ID(N'dbo.REPORT_METRIC', N'U') IS NOT NULL
BEGIN
    UPDATE dbo.REPORT_METRIC
    SET [ROW_FILTER] = N'{"table":"COP_ORDER_M","on":["ORDER_TYPE","ORDER_NO"],"conditions":[{"column":"CONFIRM_TAG","op":"=","value":true}]}',
        [CONFIRM_STATUS] = N'CONFIRMED',
        [CONFIRMED_BY] = N'admin',
        [CONFIRMED_DATE] = SYSDATETIME()
    WHERE [METRIC_ID] = N'sales_amount'
      AND ([CONFIRM_STATUS] IS NULL OR [CONFIRM_STATUS] <> N'CONFIRMED' OR [ROW_FILTER] IS NULL);
END
GO
