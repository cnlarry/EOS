-- cost-cap atomicity: per-user/per-day usage ledger (micro-yuan integers,
-- reserve-then-settle so concurrent chats cannot breach caps) + estimated-usage
-- flag on assistant messages (supplier usage missing => conservative estimate).
-- USER_ID = N'*' is the global sentinel row. All amounts in micro-yuan.

SET NOCOUNT ON;

DECLARE @GUARD NVARCHAR(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, @GUARD, 1;

BEGIN TRANSACTION;

IF OBJECT_ID(N'dbo.ASSISTANT_USAGE_DAY', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.ASSISTANT_USAGE_DAY (
        [USER_ID] NVARCHAR(50) NOT NULL,
        [USAGE_DATE] DATE NOT NULL,
        [RESERVED_MICROYUAN] BIGINT NOT NULL CONSTRAINT [DF_USAGE_DAY_RESERVED] DEFAULT (0),
        [SPENT_MICROYUAN] BIGINT NOT NULL CONSTRAINT [DF_USAGE_DAY_SPENT] DEFAULT (0),
        [REQUESTS] INT NOT NULL CONSTRAINT [DF_USAGE_DAY_REQUESTS] DEFAULT (0),
        [UPDATED_AT] DATETIME2(3) NOT NULL CONSTRAINT [DF_USAGE_DAY_UPDATED] DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT [PK_ASSISTANT_USAGE_DAY] PRIMARY KEY ([USER_ID], [USAGE_DATE])
    );
END

IF NOT EXISTS (SELECT 1 FROM sys.columns
               WHERE object_id = OBJECT_ID(N'dbo.ASSISTANT_MESSAGE') AND name = N'IS_ESTIMATED')
BEGIN
    ALTER TABLE dbo.ASSISTANT_MESSAGE ADD [IS_ESTIMATED] BIT NOT NULL
        CONSTRAINT [DF_ASSISTANT_MESSAGE_EST] DEFAULT (0);
END

COMMIT TRANSACTION;

PRINT N'[045] 用量台账表与估算标记已就绪（ASSISTANT_USAGE_DAY / IS_ESTIMATED）。';
