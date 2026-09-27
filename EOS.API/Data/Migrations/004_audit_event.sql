-- ============================================================================
-- EOS.ERP migration 004: unified business audit
-- ----------------------------------------------------------------------------
-- Purpose: audit source of truth for the new system.
--   AUDIT_EVENT: audit event master; AUDIT_FIELD_CHANGE: field-level change detail.
-- Constraints:
--   1. Audit writes share the business transaction (WorkbenchAuditWriter writes inside it);
--   2. ACTOR_TYPE: 1=user 2=system task 3=agent on behalf of user 4=external integration;
--   3. CLIENT_TYPE: 1=Web 2=API 3=agent 4=integration;
--   4. Passwords/keys/cookies/attachment content never enter the audit; large or sensitive
--      fields store only hash/mask/summary;
--   5. Retention/archival policy: see scripts/audit-retention.ps1 (DETAIL_JSON 30-day truncation,
--      AUDIT_FIELD_CHANGE 90 days, AUDIT_EVENT 180-day archival).
-- Naming convention: tables / columns / indexes / constraints all uppercase.
-- ============================================================================

SET NOCOUNT ON;

DECLARE @GUARD_MESSAGE NVARCHAR(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, @GUARD_MESSAGE, 1;

PRINT N'== 开始创建 EOS.ERP 统一审计表 ==';

CREATE TABLE dbo.AUDIT_EVENT
(
    EVENT_ID           BIGINT         IDENTITY(1,1) NOT NULL,
    OCCURRED_AT        DATETIME2(3)   NOT NULL,
    CORRELATION_ID     NVARCHAR(64)   NOT NULL,
    ACTOR_USER_ID      NVARCHAR(20)   NOT NULL,
    ACTOR_TYPE         TINYINT        NOT NULL,
    CLIENT_TYPE        TINYINT        NOT NULL,
    MODULE_ID          INT            NULL,
    RESOURCE_TYPE      NVARCHAR(50)   NOT NULL,
    RESOURCE_KEY       NVARCHAR(200)  NOT NULL,
    ACTION             NVARCHAR(50)   NOT NULL,
    RESULT             TINYINT        NOT NULL,
    DEFINITION_VERSION NVARCHAR(64)   NULL,
    SUMMARY            NVARCHAR(1000) NULL,
    DETAIL_JSON        NVARCHAR(MAX)  NULL,
    CREATED_DATE       DATETIME2(3)   NOT NULL
);

ALTER TABLE dbo.AUDIT_EVENT ADD CONSTRAINT PK_AUDIT_EVENT PRIMARY KEY CLUSTERED (EVENT_ID);
CREATE INDEX IX_AUDIT_EVENT_OCCURRED_AT ON dbo.AUDIT_EVENT (OCCURRED_AT);
CREATE INDEX IX_AUDIT_EVENT_CORRELATION_ID ON dbo.AUDIT_EVENT (CORRELATION_ID, OCCURRED_AT);
CREATE INDEX IX_AUDIT_EVENT_MODULE_ACTION ON dbo.AUDIT_EVENT (MODULE_ID, ACTION, OCCURRED_AT);

CREATE TABLE dbo.AUDIT_FIELD_CHANGE
(
    EVENT_ID   BIGINT        NOT NULL,
    FIELD_NAME NVARCHAR(100) NOT NULL,
    OLD_VALUE  NVARCHAR(MAX) NULL,
    NEW_VALUE  NVARCHAR(MAX) NULL,
    VALUE_HASH BINARY(32)    NULL,
    CONSTRAINT PK_AUDIT_FIELD_CHANGE PRIMARY KEY (EVENT_ID, FIELD_NAME)
);

ALTER TABLE dbo.AUDIT_FIELD_CHANGE ADD CONSTRAINT FK_AUDIT_FIELD_CHANGE_EVENT
    FOREIGN KEY (EVENT_ID) REFERENCES dbo.AUDIT_EVENT (EVENT_ID);
CREATE INDEX IX_AUDIT_FIELD_CHANGE_EVENT ON dbo.AUDIT_FIELD_CHANGE (EVENT_ID);

EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'统一业务审计事件表（）：本系统审计事实源，与业务事务同生共死；逐步替代旧 SYSDF 核心证据源，SYSDF 继续作兼容查询来源。', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'AUDIT_EVENT';
EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'审计事件 ID，自增主键。', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'AUDIT_EVENT', @level2type=N'COLUMN', @level2name=N'EVENT_ID';
EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'发生时间（本地时间，与业务事务一致）。', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'AUDIT_EVENT', @level2type=N'COLUMN', @level2name=N'OCCURRED_AT';
EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'关联 ID（X-Correlation-Id，服务端生成或透传），与日志/错误响应关联。', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'AUDIT_EVENT', @level2type=N'COLUMN', @level2name=N'CORRELATION_ID';
EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'执行者用户 ID。', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'AUDIT_EVENT', @level2type=N'COLUMN', @level2name=N'ACTOR_USER_ID';
EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'执行者类型：1=用户 2=系统任务 3=Agent 代表用户 4=外部集成。', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'AUDIT_EVENT', @level2type=N'COLUMN', @level2name=N'ACTOR_TYPE';
EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'调用方类型：1=Web 2=API 3=Agent 4=集成调用。', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'AUDIT_EVENT', @level2type=N'COLUMN', @level2name=N'CLIENT_TYPE';
EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'模块号（MODULES.M_IDX），无模块动作（如导出/权限拒绝）可为 NULL。', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'AUDIT_EVENT', @level2type=N'COLUMN', @level2name=N'MODULE_ID';
EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'资源类型（WORKBENCH_RECORD/ATTACHMENT/EXPORT/PRINT/PERMISSION/LOG_QUERY 等）。', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'AUDIT_EVENT', @level2type=N'COLUMN', @level2name=N'RESOURCE_TYPE';
EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'资源键（主键值逗号串或导出/打印标识）。', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'AUDIT_EVENT', @level2type=N'COLUMN', @level2name=N'RESOURCE_KEY';
EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'动作（INSERT/UPDATE/DELETE/APPROVE/DEAPPROVE/ENDCASE/UNENDCASE/EXPORT/PRINT/LOG_QUERY 等）。', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'AUDIT_EVENT', @level2type=N'COLUMN', @level2name=N'ACTION';
EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'结果：1=成功 0=失败/拒绝。', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'AUDIT_EVENT', @level2type=N'COLUMN', @level2name=N'RESULT';
EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'Definition 版本（module-{id}-v{n}；运行时快照版本化挂起期间为 NULL）。', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'AUDIT_EVENT', @level2type=N'COLUMN', @level2name=N'DEFINITION_VERSION';
EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'摘要（SYSDF CONTENT 兼容口径）。', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'AUDIT_EVENT', @level2type=N'COLUMN', @level2name=N'SUMMARY';
EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'明细 JSON（字段变更、导出条件等；按留存策略 30 天后截断）。', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'AUDIT_EVENT', @level2type=N'COLUMN', @level2name=N'DETAIL_JSON';
EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'创建时间（入库时间）。', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'AUDIT_EVENT', @level2type=N'COLUMN', @level2name=N'CREATED_DATE';

EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'审计字段变更明细（）：按事件滚动归档；大字段/敏感字段只存 hash/掩码/摘要。', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'AUDIT_FIELD_CHANGE';
EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'所属审计事件 ID（FK AUDIT_EVENT）。', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'AUDIT_FIELD_CHANGE', @level2type=N'COLUMN', @level2name=N'EVENT_ID';
EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'字段名（大写）。', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'AUDIT_FIELD_CHANGE', @level2type=N'COLUMN', @level2name=N'FIELD_NAME';
EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'旧值（敏感字段可掩码）。', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'AUDIT_FIELD_CHANGE', @level2type=N'COLUMN', @level2name=N'OLD_VALUE';
EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'新值（敏感字段可掩码）。', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'AUDIT_FIELD_CHANGE', @level2type=N'COLUMN', @level2name=N'NEW_VALUE';
EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'新值 SHA-256（大字段/敏感字段只存 hash）。', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'AUDIT_FIELD_CHANGE', @level2type=N'COLUMN', @level2name=N'VALUE_HASH';

PRINT N'== EOS.ERP 统一审计表创建完成 ==';
