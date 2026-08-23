-- ============================================================================
-- EOS.ERP 迁移 003：Workbench 写路径幂等键（ADR-005 阶段 3）
-- ----------------------------------------------------------------------------
-- 定位：WorkbenchCommandHandler / WorkbenchApprovalService 写路径的幂等键存储。
--       调用方（EOS.Web / EOS.Client / 集成）传 IdempotencyKey，重复提交返回
--       缓存结果，不重复产生副作用（重复建单、重复批核等）。
-- 命名约定（AGENTS.md 强制）：表 / 列 / 索引 / 约束一律全大写。
-- ============================================================================

SET NOCOUNT ON;

DECLARE @GUARD_MESSAGE NVARCHAR(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, @GUARD_MESSAGE, 1;

PRINT N'== 开始创建 EOS.ERP Workbench 幂等表 ==';

CREATE TABLE dbo.WORKBENCH_IDEMPOTENCY
(
    IDEMPOTENCY_KEY NVARCHAR(128)  NOT NULL,
    MODULE_ID       INT            NOT NULL,
    ACTION          NVARCHAR(20)   NOT NULL,
    RESULT_KEY      NVARCHAR(1000) NULL,
    FLOW_STARTED    BIT            NOT NULL CONSTRAINT DF_WORKBENCH_IDEMPOTENCY_FLOW_STARTED DEFAULT (0),
    CREATED_AT      DATETIME2(3)   NOT NULL CONSTRAINT DF_WORKBENCH_IDEMPOTENCY_CREATED_AT DEFAULT (SYSDATETIME())
);

ALTER TABLE dbo.WORKBENCH_IDEMPOTENCY ADD CONSTRAINT PK_WORKBENCH_IDEMPOTENCY PRIMARY KEY CLUSTERED (IDEMPOTENCY_KEY);
CREATE INDEX IX_WORKBENCH_IDEMPOTENCY_SCOPE ON dbo.WORKBENCH_IDEMPOTENCY (MODULE_ID, ACTION, CREATED_AT);

EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'Workbench 写路径幂等键表（ADR-005 阶段 3）：调用方传 IdempotencyKey，重复提交返回缓存结果；事务内 claim/complete，失败回滚即释放。', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'WORKBENCH_IDEMPOTENCY';
EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'幂等键（调用方提供，≤128 字符）。', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'WORKBENCH_IDEMPOTENCY', @level2type=N'COLUMN', @level2name=N'IDEMPOTENCY_KEY';
EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'模块号（MODULES.M_IDX）。', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'WORKBENCH_IDEMPOTENCY', @level2type=N'COLUMN', @level2name=N'MODULE_ID';
EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'动作（INSERT/UPDATE/DELETE/APPROVE/DEAPPROVE/ENDCASE/UNENDCASE）。', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'WORKBENCH_IDEMPOTENCY', @level2type=N'COLUMN', @level2name=N'ACTION';
EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'成功结果（主键值 JSON 数组或逗号串），重复提交直接返回。', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'WORKBENCH_IDEMPOTENCY', @level2type=N'COLUMN', @level2name=N'RESULT_KEY';
EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'是否已启动审批流（FlowStarted）。', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'WORKBENCH_IDEMPOTENCY', @level2type=N'COLUMN', @level2name=N'FLOW_STARTED';
EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'首次提交时间。', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'WORKBENCH_IDEMPOTENCY', @level2type=N'COLUMN', @level2name=N'CREATED_AT';

PRINT N'== EOS.ERP Workbench 幂等表创建完成 ==';
