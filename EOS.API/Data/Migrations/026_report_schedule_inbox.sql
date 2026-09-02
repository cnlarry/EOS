-- ============================================================================
-- EOS.ERP migration 026: report center schedule subscription + Report Inbox
-- ----------------------------------------------------------------------------
-- Database: EOS.ERP (the single business database of the new system)
--
-- Context: the third piece of the report center — scheduled delivery + Report Inbox
-- (D365 BC pattern: reports proactively reach the user).
--
-- Table design:
--   REPORT_SUBSCRIPTION: user report subscription (period + execution time + enable/disable)
--     - SCHEDULE_TYPE: DAILY / WEEKLY / MONTHLY
--     - RUN_HOUR/RUN_MINUTE: execution hour (0-23) / minute (0-59)
--     - WEEKDAY (for WEEKLY, 1=Mon…7=Sun) / MONTH_DAY (for MONTHLY, 1-31)
--     - Permission: the subscriber must have visibility on the report (REPORT_TAG)
--   REPORT_INBOX: subscription output record (PDF path + generation time + read flag)
--     - PDF_PATH relative path, files stored under Inbox:StorageRoot
--
-- Naming convention: all uppercase. Idempotent: IF NOT EXISTS guards.
-- ============================================================================

SET NOCOUNT ON;

DECLARE @GUARD_MESSAGE NVARCHAR(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, @GUARD_MESSAGE, 1;

BEGIN TRANSACTION;

-- 1. REPORT_SUBSCRIPTION
IF OBJECT_ID('dbo.REPORT_SUBSCRIPTION') IS NULL
BEGIN
    CREATE TABLE dbo.REPORT_SUBSCRIPTION (
        [ID] INT IDENTITY(1,1) NOT NULL CONSTRAINT [PK_REPORT_SUBSCRIPTION] PRIMARY KEY,
        [USER_ID] NCHAR(10) NOT NULL,
        [MODULE_ID] INT NOT NULL,
        [REPORT_ID] NCHAR(50) NOT NULL,
        [SCHEDULE_TYPE] NCHAR(10) NOT NULL CONSTRAINT [DF_REPORT_SUBSCRIPTION_TYPE] DEFAULT (N'DAILY'),
        [RUN_HOUR] INT NOT NULL CONSTRAINT [DF_REPORT_SUBSCRIPTION_HOUR] DEFAULT (8),
        [RUN_MINUTE] INT NOT NULL CONSTRAINT [DF_REPORT_SUBSCRIPTION_MINUTE] DEFAULT (0),
        [WEEKDAY] INT NULL,
        [MONTH_DAY] INT NULL,
        [ENABLED_TAG] BIT NOT NULL CONSTRAINT [DF_REPORT_SUBSCRIPTION_ENABLED] DEFAULT (1),
        [LAST_RUN_AT] DATETIME2 NULL,
        [CREATE_PERSON] NCHAR(40) NULL,
        [CREATE_DATE] DATETIME2 NOT NULL CONSTRAINT [DF_REPORT_SUBSCRIPTION_CREATED] DEFAULT (SYSDATETIME()),
        [LAST_UPDATE_BY] NCHAR(40) NULL,
        [LAST_UPDATE_DATE] DATETIME2 NULL
    );
    CREATE INDEX [IX_REPORT_SUBSCRIPTION_USER] ON dbo.REPORT_SUBSCRIPTION ([USER_ID], [ENABLED_TAG]);
END

-- 2. REPORT_INBOX
IF OBJECT_ID('dbo.REPORT_INBOX') IS NULL
BEGIN
    CREATE TABLE dbo.REPORT_INBOX (
        [ID] INT IDENTITY(1,1) NOT NULL CONSTRAINT [PK_REPORT_INBOX] PRIMARY KEY,
        [USER_ID] NCHAR(10) NOT NULL,
        [MODULE_ID] INT NOT NULL,
        [REPORT_ID] NCHAR(50) NOT NULL,
        [TITLE] NVARCHAR(200) NOT NULL,
        [PDF_PATH] NVARCHAR(500) NOT NULL,
        [GENERATED_AT] DATETIME2 NOT NULL CONSTRAINT [DF_REPORT_INBOX_GENERATED] DEFAULT (SYSDATETIME()),
        [READ_TAG] BIT NOT NULL CONSTRAINT [DF_REPORT_INBOX_READ] DEFAULT (0)
    );
    CREATE INDEX [IX_REPORT_INBOX_USER] ON dbo.REPORT_INBOX ([USER_ID], [GENERATED_AT]);
END

-- 3. 审计计数
SELECT 'REPORT_SUBSCRIPTION' AS KIND, COUNT(*) AS CNT FROM dbo.REPORT_SUBSCRIPTION;
SELECT 'REPORT_INBOX' AS KIND, COUNT(*) AS CNT FROM dbo.REPORT_INBOX;

COMMIT TRANSACTION;