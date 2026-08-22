-- ============================================================================
-- EOS.ERP 迁移 001：业务单据附件元数据（ATTACHMENT）
-- ----------------------------------------------------------------------------
-- 库：EOS.ERP（全新系统的唯一业务数据库）
--
-- 定位：统一表单（DocumentWorkbench）单据级附件的元数据存储。文件二进制存文件系统
--      （Attachment:StorageRoot），本表只存元数据 + SHA-256，便于追溯与后续向量化
--      （工作助手经 EOS.API 授权读取，不直连库）。
--
-- 命名约定（AGENTS.md 强制）：表 / 列 / 索引 / 约束一律全大写。
--
-- 追溯与安全约定（不可破坏）：
--   1. 归属单据用结构化主键（MODULE_ID + MASTER_TABLE + KEY_VALUES JSON 数组），
--      不再用旧 SYS_FILE 的 KEY_VALUE 字符串条件；
--   2. UPLOADED_BY / UPLOADED_BY_DISPLAY 由 EOS.API 服务端从登录会话强制写入，不接受客户端提交；
--   3. 上传/查看/改备注/删除分别由 FILE_UPDA/VIEW/EDIT/DELE_TAG 权限位服务端强制校验；
--   4. CLIENT_FILE_NAME 仅用于展示，服务器实际文件名为服务端生成（防路径穿越）；
--   5. 文件不可达（磁盘缺失）时下载返回 404，元数据保留以便审计定位。
-- ============================================================================

SET NOCOUNT ON;

DECLARE @GUARD_MESSAGE NVARCHAR(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, @GUARD_MESSAGE, 1;

PRINT N'== 开始创建 EOS.ERP ATTACHMENT 表 ==';

CREATE TABLE dbo.ATTACHMENT
(
    ID                   BIGINT         IDENTITY(1,1) NOT NULL,
    MODULE_ID            INT            NOT NULL,
    MASTER_TABLE         NVARCHAR(128)  NOT NULL,
    KEY_VALUES           NVARCHAR(2000) NOT NULL,
    SERIAL_NO            INT            NOT NULL,
    FILE_NAME            NVARCHAR(255)  NOT NULL,
    CLIENT_FILE_NAME     NVARCHAR(255)  NOT NULL,
    CONTENT_TYPE         NVARCHAR(128)  NOT NULL,
    SIZE_BYTES           BIGINT         NOT NULL,
    SHA256               CHAR(64)       NOT NULL,
    REMARK               NVARCHAR(500)  NULL,
    UPLOADED_BY          NVARCHAR(64)   NOT NULL,
    UPLOADED_BY_DISPLAY  NVARCHAR(100)  NULL,
    UPLOADED_AT          DATETIME2(3)   NOT NULL CONSTRAINT DF_ATTACHMENT_UPLOADED_AT DEFAULT SYSUTCDATETIME()
);

ALTER TABLE dbo.ATTACHMENT ADD CONSTRAINT PK_ATTACHMENT PRIMARY KEY CLUSTERED (ID);
ALTER TABLE dbo.ATTACHMENT ADD CONSTRAINT CK_ATTACHMENT_SIZE
    CHECK (SIZE_BYTES > 0 AND SIZE_BYTES <= 52428800);
ALTER TABLE dbo.ATTACHMENT ADD CONSTRAINT UQ_ATTACHMENT_SCOPE_SERIAL
    UNIQUE (MODULE_ID, MASTER_TABLE, KEY_VALUES, SERIAL_NO);

CREATE INDEX IX_ATTACHMENT_SCOPE ON dbo.ATTACHMENT (MODULE_ID, MASTER_TABLE, KEY_VALUES);

EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'业务单据附件元数据表（EOS.ERP 唯一库内）：文件存文件系统，本表只存元数据与 SHA-256，按 MODULE_ID+MASTER_TABLE+KEY_VALUES+SERIAL_NO 定位单据级附件，支持追溯与后续向量化。', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'ATTACHMENT';

EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'附件ID，自增主键。', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'ATTACHMENT', @level2type=N'COLUMN', @level2name=N'ID';
EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'所属模块号（MODULES.M_IDX），与权限门对应。', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'ATTACHMENT', @level2type=N'COLUMN', @level2name=N'MODULE_ID';
EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'主表名（工作台定义，已白名单校验）。', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'ATTACHMENT', @level2type=N'COLUMN', @level2name=N'MASTER_TABLE';
EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'结构化主键值 JSON 数组（按主键顺序，如 ["2026-08-22","A001"]）。', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'ATTACHMENT', @level2type=N'COLUMN', @level2name=N'KEY_VALUES';
EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'附件项次（同单据内自增，1 起）。', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'ATTACHMENT', @level2type=N'COLUMN', @level2name=N'SERIAL_NO';
EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'服务器端保存的文件名（服务端生成，含扩展名；客户端文件名仅存 CLIENT_FILE_NAME）。', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'ATTACHMENT', @level2type=N'COLUMN', @level2name=N'FILE_NAME';
EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'客户端原始文件名（仅展示）。', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'ATTACHMENT', @level2type=N'COLUMN', @level2name=N'CLIENT_FILE_NAME';
EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'MIME 类型（上传时按白名单校验）。', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'ATTACHMENT', @level2type=N'COLUMN', @level2name=N'CONTENT_TYPE';
EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'文件大小（字节），CHECK 约束限制 0 < SIZE_BYTES <= 50MB。', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'ATTACHMENT', @level2type=N'COLUMN', @level2name=N'SIZE_BYTES';
EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'文件 SHA-256 十六进制摘要（完整性校验，向量化去重）。', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'ATTACHMENT', @level2type=N'COLUMN', @level2name=N'SHA256';
EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'附件说明（用户可编辑）。', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'ATTACHMENT', @level2type=N'COLUMN', @level2name=N'REMARK';
EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'上传者用户 ID（服务端强制写入）。', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'ATTACHMENT', @level2type=N'COLUMN', @level2name=N'UPLOADED_BY';
EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'上传者员工姓名（服务端从登录会话写入，便于展示与追溯）。', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'ATTACHMENT', @level2type=N'COLUMN', @level2name=N'UPLOADED_BY_DISPLAY';
EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'上传时间（UTC）。', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'ATTACHMENT', @level2type=N'COLUMN', @level2name=N'UPLOADED_AT';

PRINT N'== EOS.ERP ATTACHMENT 表创建完成 ==';