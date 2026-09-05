-- ============================================================================
-- EOS.ERP migration 047: AUDIT_EVENT v2 (SYSDF-compatible dual write removed)
-- ----------------------------------------------------------------------------
-- The new-system operation log no longer mirrors the legacy SYSDF table.
-- Compared with SYSDF (LOG_IDX/M_IDX/RECORD_IDX/CONTENT/TYPE/EXEC_BY/EXEC_DATE/
-- CI/OPERFLAG), AUDIT_EVENT v2 keeps every SYSDF capability and adds:
--   TRACE_ID           : W3C trace id, joins log/MCP/error responses
--   ACTOR_DISPLAY_NAME : employee display name (SYSDF EXEC_BY mixed id/name)
--   CLIENT_IP          : caller IP (SYSDF had none)
--   USER_AGENT         : caller user agent (SYSDF had none)
--   REQUEST_METHOD     : HTTP method (SYSDF had none)
--   REQUEST_PATH       : request path (SYSDF had none)
--   ERROR_CODE         : stable error code for failures (SYSDF had no result)
-- plus RESULT (success/fail), field-level changes (AUDIT_FIELD_CHANGE),
-- DEFINITION_VERSION and DETAIL_JSON carried over from v1.
-- SYSDF stays in the database read-only for history; nothing writes to it.
--
-- Naming convention: all uppercase. Idempotent: sys.* guards.
-- Batch note: statements referencing the new columns live in their own batches
-- (columns added via ALTER TABLE are not visible later in the same batch).
-- ============================================================================

SET NOCOUNT ON;

DECLARE @GUARD_MESSAGE NVARCHAR(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, @GUARD_MESSAGE, 1;

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.AUDIT_EVENT') AND name = N'TRACE_ID')
BEGIN
    ALTER TABLE dbo.AUDIT_EVENT ADD
        [TRACE_ID] NVARCHAR(64) NULL,
        [ACTOR_DISPLAY_NAME] NVARCHAR(50) NULL,
        [CLIENT_IP] NVARCHAR(45) NULL,
        [USER_AGENT] NVARCHAR(500) NULL,
        [REQUEST_METHOD] NVARCHAR(10) NULL,
        [REQUEST_PATH] NVARCHAR(500) NULL,
        [ERROR_CODE] NVARCHAR(100) NULL;
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.AUDIT_EVENT') AND name = N'IX_AUDIT_EVENT_ACTOR')
BEGIN
    CREATE INDEX [IX_AUDIT_EVENT_ACTOR] ON dbo.AUDIT_EVENT ([ACTOR_USER_ID], [OCCURRED_AT] DESC);
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.AUDIT_EVENT') AND name = N'IX_AUDIT_EVENT_RESULT')
BEGIN
    CREATE INDEX [IX_AUDIT_EVENT_RESULT] ON dbo.AUDIT_EVENT ([RESULT], [OCCURRED_AT] DESC);
END
GO

EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'W3C trace id，与日志/MCP/错误响应关联。', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'AUDIT_EVENT', @level2type=N'COLUMN', @level2name=N'TRACE_ID';
EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'执行者显示名（员工姓名；旧 SYSDF EXEC_BY 混用账号与姓名）。', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'AUDIT_EVENT', @level2type=N'COLUMN', @level2name=N'ACTOR_DISPLAY_NAME';
EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'调用方 IP。', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'AUDIT_EVENT', @level2type=N'COLUMN', @level2name=N'CLIENT_IP';
EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'调用方 User-Agent。', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'AUDIT_EVENT', @level2type=N'COLUMN', @level2name=N'USER_AGENT';
EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'HTTP 方法。', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'AUDIT_EVENT', @level2type=N'COLUMN', @level2name=N'REQUEST_METHOD';
EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'请求路径。', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'AUDIT_EVENT', @level2type=N'COLUMN', @level2name=N'REQUEST_PATH';
EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'稳定错误码（失败/拒绝时；成功为 NULL）。', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'AUDIT_EVENT', @level2type=N'COLUMN', @level2name=N'ERROR_CODE';

PRINT N'== AUDIT_EVENT v2 升级完成 ==';
