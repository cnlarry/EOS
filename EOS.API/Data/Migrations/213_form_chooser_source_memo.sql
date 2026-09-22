-- ============================================================================
-- EOS.ERP migration 214: 选择器来源记忆（单据级事实：这个字段当初从哪个来源选入）
-- ----------------------------------------------------------------------------
-- 背景：同一字段可配多个启用来源（FIELD_DATASOURCE）。选择器的回写（RETURN_ITEMS：
-- 来源表.列 → 本表字段）发生在选择当时，落进本表的只有主字段值与有物理列的伴生字段。
-- 「无物理列的复合格从字段」（判定见 FormFieldSelector：是复合格从字段且无物理列）
-- 不落库，读取时按来源现算；而现算只取「首个启用来源」——用户实际选的是哪个来源，
-- 没有任何地方记录，跨来源表配置的回显就会取错表。
--
-- 本表把「来源选择」记成单据级事实：保存时按 (模块, 表, 字段, 行键) 记下所用来源序号；
-- 读取回显时优先按记忆选来源，无记忆再回退「首个启用来源」（与既有行为一致）。
-- 只影响回显解析：来源配置、筛选、回写映射与业务表结构一律不变。
--
-- 键：KEY_VALUES 为承载该字段那一行的主键值 JSON 数组（明细行 = 明细表主键，含主表键）；
--     MASTER_KEY_VALUES 为所属单据主键值 JSON 数组，用于单据删除与整体替换时定位。
-- 幂等：建表前判存在；重复执行不报错。
-- 回滚：DROP TABLE dbo.FORM_CHOOSER_SOURCE_MEMO（记忆可重建，不含业务数据）。
-- ============================================================================

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @GUARD_MESSAGE NVARCHAR(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, @GUARD_MESSAGE, 1;

IF OBJECT_ID(N'dbo.FORM_CHOOSER_SOURCE_MEMO', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.FORM_CHOOSER_SOURCE_MEMO (
        MODULE_ID         INT           NOT NULL,
        T_ID              NVARCHAR(100) NOT NULL,   -- 承载该字段的表（主表或明细表）
        F_ID              NVARCHAR(100) NOT NULL,   -- 选择器主字段（FORM_CELL_ROLE=1）
        KEY_VALUES        NVARCHAR(200) NOT NULL,   -- 该行主键值 JSON 数组
        MASTER_KEY_VALUES NVARCHAR(200) NOT NULL,   -- 所属单据主键值 JSON 数组
        SOURCE_SERIAL_NO  INT           NOT NULL,   -- FIELD_DATASOURCE.SERIAL_NO
        UPDATED_BY        NVARCHAR(50)  NULL,
        UPDATED_AT        DATETIME2(0)  NOT NULL CONSTRAINT DF_FCSM_UPDATED_AT DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT PK_FORM_CHOOSER_SOURCE_MEMO PRIMARY KEY CLUSTERED (MODULE_ID, T_ID, F_ID, KEY_VALUES)
    );
END

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_FCSM_MASTER' AND object_id = OBJECT_ID(N'dbo.FORM_CHOOSER_SOURCE_MEMO'))
    CREATE NONCLUSTERED INDEX IX_FCSM_MASTER ON dbo.FORM_CHOOSER_SOURCE_MEMO (MODULE_ID, MASTER_KEY_VALUES);

IF OBJECT_ID(N'dbo.FORM_CHOOSER_SOURCE_MEMO', N'U') IS NULL
    THROW 51000, N'选择器来源记忆表未建立。', 1;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'PK_FORM_CHOOSER_SOURCE_MEMO' AND object_id = OBJECT_ID(N'dbo.FORM_CHOOSER_SOURCE_MEMO'))
    THROW 51000, N'选择器来源记忆表缺少主键。', 1;

PRINT N'选择器来源记忆表已就绪：dbo.FORM_CHOOSER_SOURCE_MEMO。';
GO
