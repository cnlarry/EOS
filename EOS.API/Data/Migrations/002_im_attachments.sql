-- ============================================================================
-- EOS.IM 迁移 002：文件/附件（M3）
-- ----------------------------------------------------------------------------
-- 1. 附件二进制存 EOS.IM（内部规模文件不大，统一入库便于备份与权限校验）；
-- 2. 上传先落附件（MessageId 为 NULL），发送文件消息后再回填关联；
-- 3. 消息类型放开 File；下载/打开时服务端必须校验会话成员身份。
-- ============================================================================

SET NOCOUNT ON;

CREATE TABLE dbo.im_attachments
(
    Id                BIGINT         IDENTITY(1,1) NOT NULL,
    MessageId         BIGINT         NULL,
    ConversationId    BIGINT         NOT NULL,
    UploadedByUserId  NVARCHAR(64)   NOT NULL,
    FileName          NVARCHAR(255)  NOT NULL,
    ContentType       NVARCHAR(128)  NOT NULL,
    SizeBytes         BIGINT         NOT NULL,
    Content           VARBINARY(MAX) NOT NULL,
    Sha256            CHAR(64)       NOT NULL,
    UploadedAt        DATETIME2(3)   NOT NULL CONSTRAINT DF_im_attachments_UploadedAt DEFAULT SYSUTCDATETIME()
);

ALTER TABLE dbo.im_attachments ADD CONSTRAINT PK_im_attachments PRIMARY KEY CLUSTERED (Id);
ALTER TABLE dbo.im_attachments ADD CONSTRAINT CK_im_attachments_Size
    CHECK (SizeBytes > 0 AND SizeBytes <= 20971520);
GO

CREATE INDEX IX_im_attachments_Conversation
    ON dbo.im_attachments (ConversationId, UploadedAt);
GO

ALTER TABLE dbo.im_attachments ADD CONSTRAINT FK_im_attachments_message
    FOREIGN KEY (MessageId) REFERENCES dbo.im_messages (Id);
ALTER TABLE dbo.im_attachments ADD CONSTRAINT FK_im_attachments_conversation
    FOREIGN KEY (ConversationId) REFERENCES dbo.im_conversations (Id) ON DELETE CASCADE;
GO

ALTER TABLE dbo.im_messages DROP CONSTRAINT CK_im_messages_Type;
ALTER TABLE dbo.im_messages ADD CONSTRAINT CK_im_messages_Type
    CHECK (MessageType IN (N'Text', N'Card', N'System', N'File'));
GO

EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'附件表（M3）：文件二进制与元数据统一入库，上限 20MB；上传先落附件、发送文件消息后回填 MessageId；下载时必须校验会话成员身份。', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'im_attachments';

EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'附件ID，自增主键。', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'im_attachments', @level2type=N'COLUMN', @level2name=N'Id';
EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'关联的文件消息ID；上传成功但尚未发送文件消息时为 NULL，发送成功后回填。', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'im_attachments', @level2type=N'COLUMN', @level2name=N'MessageId';
EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'所属会话ID，外键关联 im_conversations。', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'im_attachments', @level2type=N'COLUMN', @level2name=N'ConversationId';
EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'上传者用户ID；仅上传者可把该附件作为消息发出。', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'im_attachments', @level2type=N'COLUMN', @level2name=N'UploadedByUserId';
EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'原始文件名（仅用于展示，下载时服务端按白名单校验扩展名）。', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'im_attachments', @level2type=N'COLUMN', @level2name=N'FileName';
EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'MIME 类型（上传时按白名单校验）。', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'im_attachments', @level2type=N'COLUMN', @level2name=N'ContentType';
EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'文件大小（字节），CHECK 约束限制 0 < SizeBytes <= 20MB。', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'im_attachments', @level2type=N'COLUMN', @level2name=N'SizeBytes';
EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'文件二进制内容（VARBINARY(MAX)），不落磁盘路径，便于备份与权限控制。', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'im_attachments', @level2type=N'COLUMN', @level2name=N'Content';
EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'文件 SHA-256 十六进制摘要（去重与完整性校验）。', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'im_attachments', @level2type=N'COLUMN', @level2name=N'Sha256';
EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'上传时间（UTC）。', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'im_attachments', @level2type=N'COLUMN', @level2name=N'UploadedAt';

EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'按会话查询附件（会话文件列表）的索引。', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'im_attachments', @level2type=N'INDEX', @level2name=N'IX_im_attachments_Conversation';

PRINT N'== 迁移 002 完成：im_attachments + File 消息类型 ==';
