-- ============================================================================
-- EOS.Mail 邮件任务 Schema v1（001_mail_tasks.sql）
-- ----------------------------------------------------------------------------
-- 库：EOS.Mail（邮件衍生任务库，独立于 EOS.ERP 旧库与 EOS.IM 消息库）
--
-- 定位：EOS.Client 邮件入口中，由用户确认的"邮件待办"统一落到 EOS.API 任务体系，
--      在 2102"我的任务"的展示面上与单据待批核并列，但数据完全隔离。
--
-- 隐私与信息隔离约定（不可破坏）：
--   1. 只存任务元数据（标题/描述/来源邮件标识/截止时间），绝不存邮件正文、附件；
--   2. Description 是 AI 提取并裁剪后的待办摘要（<=1000 字符），SourceSubject 仅作来源标识；
--   3. 归属用户由 EOS.API 服务端强制写入（UserId），不接受客户端提交；
--   4. 所有读写按 UserId 过滤，任何人只能访问自己的任务；
--   5. 审计只记元数据，不落邮件内容。
--
-- 命名约定：表 / 列使用 mail_ 前缀；备注使用中文。
-- ============================================================================

SET NOCOUNT ON;

DECLARE @mailGuardMessage NVARCHAR(400) = N'本脚本只能在 EOS.Mail 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.Mail'
    THROW 50000, @mailGuardMessage, 1;

PRINT N'== 开始创建 EOS.Mail schema ==';

CREATE TABLE dbo.mail_tasks
(
    Id              BIGINT         IDENTITY(1,1) NOT NULL,
    UserId          NVARCHAR(64)   NOT NULL,
    Title           NVARCHAR(200)  NOT NULL,
    Description     NVARCHAR(1000) NULL,
    SourceKind      NVARCHAR(16)   NOT NULL CONSTRAINT DF_mail_tasks_SourceKind DEFAULT N'email',
    SourceMailbox   NVARCHAR(255)  NULL,
    SourceMessageId NVARCHAR(255)  NULL,
    SourceSubject   NVARCHAR(255)  NULL,
    DueDate         DATETIME2(3)   NULL,
    Priority        TINYINT        NOT NULL CONSTRAINT DF_mail_tasks_Priority DEFAULT 2,
    Status          NVARCHAR(16)   NOT NULL CONSTRAINT DF_mail_tasks_Status DEFAULT N'open',
    CreatedAt       DATETIME2(3)   NOT NULL CONSTRAINT DF_mail_tasks_CreatedAt DEFAULT SYSUTCDATETIME(),
    CompletedAt     DATETIME2(3)   NULL,
    UpdatedAt       DATETIME2(3)   NOT NULL CONSTRAINT DF_mail_tasks_UpdatedAt DEFAULT SYSUTCDATETIME()
);

ALTER TABLE dbo.mail_tasks ADD CONSTRAINT PK_mail_tasks PRIMARY KEY CLUSTERED (Id);
ALTER TABLE dbo.mail_tasks ADD CONSTRAINT CK_mail_tasks_Priority CHECK (Priority BETWEEN 1 AND 3);
ALTER TABLE dbo.mail_tasks ADD CONSTRAINT CK_mail_tasks_Status
    CHECK (Status IN (N'open', N'done', N'cancelled'));
CREATE INDEX IX_mail_tasks_UserStatus ON dbo.mail_tasks (UserId, Status, CreatedAt DESC);
CREATE INDEX IX_mail_tasks_UserDue ON dbo.mail_tasks (UserId, DueDate) WHERE DueDate IS NOT NULL;

PRINT N'== EOS.Mail schema 创建完成 ==';
