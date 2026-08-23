-- ============================================================================
-- EOS.ERP 迁移 002：Workbench Definition 快照（ADR-005 阶段 2，发布侧）
-- ----------------------------------------------------------------------------
-- 库：EOS.ERP（全新系统的唯一业务数据库）
--
-- 定位：
--   1. WORKBENCH_MODULE_DIRTY：元数据写路径（2302 字段维护、工作台列设置/列宽、
--      菜单默认列）保存后仅把模块标记为「脏」，不逐次生成快照；
--   2. WORKBENCH_DEFINITION_SNAPSHOT：发布动作校验通过后写入的版本化 Definition 快照
--      （定义 JSON + 校验结果 + 发布人/时间），每模块唯一当前快照（过滤唯一索引）。
--
-- 命名约定（AGENTS.md 强制）：表 / 列 / 索引 / 约束一律全大写。
-- 运行时不可变快照加载与 definitionVersion 传播按 ADR 实施边界保持挂起，
-- 本迁移只落发布侧存储；后续阶段在快照之上追加运行时消费。
-- ============================================================================

SET NOCOUNT ON;

DECLARE @GUARD_MESSAGE NVARCHAR(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, @GUARD_MESSAGE, 1;

PRINT N'== 开始创建 EOS.ERP Workbench Definition 快照表 ==';

CREATE TABLE dbo.WORKBENCH_MODULE_DIRTY
(
    MODULE_ID        INT           NOT NULL,
    DIRTY_TAG        BIT           NOT NULL CONSTRAINT DF_WORKBENCH_MODULE_DIRTY_DIRTY_TAG DEFAULT (1),
    LAST_MODIFIED_BY NVARCHAR(100) NULL,
    LAST_MODIFIED_AT DATETIME2(3)  NOT NULL CONSTRAINT DF_WORKBENCH_MODULE_DIRTY_LAST_MODIFIED_AT DEFAULT (SYSDATETIME())
);

ALTER TABLE dbo.WORKBENCH_MODULE_DIRTY ADD CONSTRAINT PK_WORKBENCH_MODULE_DIRTY PRIMARY KEY CLUSTERED (MODULE_ID);

CREATE TABLE dbo.WORKBENCH_DEFINITION_SNAPSHOT
(
    SNAPSHOT_ID             BIGINT         IDENTITY(1,1) NOT NULL,
    MODULE_ID               INT            NOT NULL,
    VERSION                 INT            NOT NULL,
    DEFINITION_JSON         NVARCHAR(MAX)  NOT NULL,
    SOURCE_METADATA_VERSION NVARCHAR(100)  NULL,
    VALIDATION_STATUS       NVARCHAR(20)   NOT NULL,
    VALIDATION_REPORT_JSON  NVARCHAR(MAX)  NULL,
    PUBLISHED_BY            NVARCHAR(100)  NULL,
    PUBLISHED_AT            DATETIME2(3)   NOT NULL CONSTRAINT DF_WORKBENCH_DEFINITION_SNAPSHOT_PUBLISHED_AT DEFAULT (SYSDATETIME()),
    IS_CURRENT              BIT            NOT NULL CONSTRAINT DF_WORKBENCH_DEFINITION_SNAPSHOT_IS_CURRENT DEFAULT (0)
);

ALTER TABLE dbo.WORKBENCH_DEFINITION_SNAPSHOT ADD CONSTRAINT PK_WORKBENCH_DEFINITION_SNAPSHOT PRIMARY KEY CLUSTERED (SNAPSHOT_ID);
CREATE UNIQUE INDEX UX_WORKBENCH_DEFINITION_SNAPSHOT_CURRENT
    ON dbo.WORKBENCH_DEFINITION_SNAPSHOT (MODULE_ID) WHERE IS_CURRENT = 1;
CREATE INDEX IX_WORKBENCH_DEFINITION_SNAPSHOT_MODULE
    ON dbo.WORKBENCH_DEFINITION_SNAPSHOT (MODULE_ID, VERSION);

EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'Workbench 模块元数据脏标记表（ADR-005 §3）：元数据写路径保存后仅标记脏，由发布动作批量校验并生成快照；「已编辑但未发布」= DIRTY_TAG=1 或快照早于最后修改。', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'WORKBENCH_MODULE_DIRTY';
EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'模块号（MODULES.M_IDX）。', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'WORKBENCH_MODULE_DIRTY', @level2type=N'COLUMN', @level2name=N'MODULE_ID';
EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'脏标记（1=已编辑未发布）。', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'WORKBENCH_MODULE_DIRTY', @level2type=N'COLUMN', @level2name=N'DIRTY_TAG';
EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'最后修改人。', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'WORKBENCH_MODULE_DIRTY', @level2type=N'COLUMN', @level2name=N'LAST_MODIFIED_BY';
EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'最后修改时间。', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'WORKBENCH_MODULE_DIRTY', @level2type=N'COLUMN', @level2name=N'LAST_MODIFIED_AT';

EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'Workbench Definition 发布快照表（ADR-005 §3）：保存定义 JSON、来源元数据版本、校验结果、发布人、发布时间与当前状态；每模块唯一当前快照（IS_CURRENT 过滤唯一索引）。', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'WORKBENCH_DEFINITION_SNAPSHOT';
EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'快照 ID，自增主键。', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'WORKBENCH_DEFINITION_SNAPSHOT', @level2type=N'COLUMN', @level2name=N'SNAPSHOT_ID';
EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'模块号（MODULES.M_IDX）。', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'WORKBENCH_DEFINITION_SNAPSHOT', @level2type=N'COLUMN', @level2name=N'MODULE_ID';
EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'版本号（按模块递增，1 起）；definitionVersion=module-{id}-v{version}。', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'WORKBENCH_DEFINITION_SNAPSHOT', @level2type=N'COLUMN', @level2name=N'VERSION';
EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'定义 JSON（全权限基线 WorkbenchDefinition，含模块/表/字段/路由/过滤/分组/表单元数据；VIRTUAL_EXP/CONVERT_FUNCTION 等高危表达式字段不序列化）。', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'WORKBENCH_DEFINITION_SNAPSHOT', @level2type=N'COLUMN', @level2name=N'DEFINITION_JSON';
EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'来源元数据版本（模块行与主/子表字段 LAST_UPDATE_DATE 的最大时间戳，用于判断快照是否落后）。', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'WORKBENCH_DEFINITION_SNAPSHOT', @level2type=N'COLUMN', @level2name=N'SOURCE_METADATA_VERSION';
EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'校验结果（PASS/FAIL），未通过校验的定义不发布。', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'WORKBENCH_DEFINITION_SNAPSHOT', @level2type=N'COLUMN', @level2name=N'VALIDATION_STATUS';
EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'机器可读校验报告 JSON（checks: code/passed/message）。', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'WORKBENCH_DEFINITION_SNAPSHOT', @level2type=N'COLUMN', @level2name=N'VALIDATION_REPORT_JSON';
EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'发布人。', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'WORKBENCH_DEFINITION_SNAPSHOT', @level2type=N'COLUMN', @level2name=N'PUBLISHED_BY';
EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'发布时间。', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'WORKBENCH_DEFINITION_SNAPSHOT', @level2type=N'COLUMN', @level2name=N'PUBLISHED_AT';
EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'是否当前快照（每模块至多 1 条为 1，过滤唯一索引保证）。', @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'WORKBENCH_DEFINITION_SNAPSHOT', @level2type=N'COLUMN', @level2name=N'IS_CURRENT';

PRINT N'== EOS.ERP Workbench Definition 快照表创建完成 ==';
