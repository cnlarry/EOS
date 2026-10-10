-- ============================================================================
-- EOS.ERP migration 344: 导入映射记忆（IMPORT_MAPPING）
-- ----------------------------------------------------------------------------
-- 背景：基本资料导入的列映射（"客户代号 → CLIENT_ID"这类）原本只活在浏览器的一次会话里，
-- 刷新即丢、换人换机就没有。而实施期的真实用法是**同一个客户、同一张表要导很多次**
-- （初导、补漏、修正、补第二批…），每次都要人重新确认一遍映射，是这个工具最容易被骂的地方。
--
-- 故按（用户 + 模块）记住一份映射：源列名 → 目标字段键。之所以按**列名**而不是列下标记，
-- 是因为补导的文件常由 Excel 重新另存，列顺序会变而列名不变；按下标记会把映射错位到别的列上。
--
-- 归属说明：这是**用户偏好**级数据，不是业务数据——进不了统一表单、不参与发布、不需要字段元数据登记，
-- 与 WORKBENCH_IDEMPOTENCY / WORKBENCH_DEFINITION_SNAPSHOT 同类（同属内部运行状态表）。
-- 因此不登记 TABLES/FIELDS，也不挂审计（写入者由 LAST_UPDATE_BY 留档）。
--
-- 幂等：建表与列均按存在性判断，可重复执行。
-- ============================================================================

SET NOCOUNT ON;
SET XACT_ABORT ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET ARITHABORT ON;

DECLARE @GuardMessage NVARCHAR(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 51894, @GuardMessage, 1;

IF OBJECT_ID(N'dbo.MODULES', N'U') IS NULL
    THROW 51895, N'dbo.MODULES 不存在（建库基线未执行？），迁移中止。', 1;

BEGIN TRANSACTION;

IF OBJECT_ID(N'dbo.IMPORT_MAPPING', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.[IMPORT_MAPPING](
        -- 身份 = （用户 + 模块）：同一用户在同一模块上只保留一份"最近用的映射"
        [USER_ID]          NVARCHAR(100) NOT NULL,
        [M_IDX]            INT           NOT NULL,
        -- 上次用它的是哪个文件（只作提示，不参与匹配：匹配按列名逐列来）
        [SOURCE_NAME]      NVARCHAR(300) NOT NULL CONSTRAINT DF_IMPORT_MAPPING_SOURCE_NAME DEFAULT (N''),
        -- 映射本体：[{"column":"客户代号","field":"CLIENT_ID"}, …]；field 为 null 表示该列不导入
        [MAPPING_JSON]     NVARCHAR(MAX) NOT NULL,
        -- 经办人/日期列按全库约定**保持可空**：NULL = 这件事还没发生过。
        -- 不要套用建立组（CREATE_PERSON/CREATE_DATE）的 NOT NULL + DEFAULT 口径
        -- ——两者语义不同，由 scripts/check-lifecycle-columns.ps1 分开核对。
        [LAST_UPDATE_BY]   NVARCHAR(80)  NULL,
        [LAST_UPDATE_DATE] DATETIME      NULL,
        CONSTRAINT PK_IMPORT_MAPPING PRIMARY KEY CLUSTERED ([USER_ID], [M_IDX])
    );
    PRINT N'== 已建 IMPORT_MAPPING 表 ==';
END
ELSE
BEGIN
    PRINT N'== IMPORT_MAPPING 已存在，跳过建表 ==';

    /* 收敛：早期版本把经办人/日期列误建成了 NOT NULL + DEFAULT，这里按库内实际结构改回可空 */
    IF EXISTS (SELECT 1 FROM sys.default_constraints dc
               JOIN sys.columns c ON c.object_id = dc.parent_object_id AND c.column_id = dc.parent_column_id
               WHERE dc.parent_object_id = OBJECT_ID(N'dbo.IMPORT_MAPPING')
                 AND c.name = N'LAST_UPDATE_BY' AND dc.name = N'DF_IMPORT_MAPPING_LAST_UPDATE_BY')
        ALTER TABLE dbo.IMPORT_MAPPING DROP CONSTRAINT DF_IMPORT_MAPPING_LAST_UPDATE_BY;

    IF EXISTS (SELECT 1 FROM sys.default_constraints dc
               JOIN sys.columns c ON c.object_id = dc.parent_object_id AND c.column_id = dc.parent_column_id
               WHERE dc.parent_object_id = OBJECT_ID(N'dbo.IMPORT_MAPPING')
                 AND c.name = N'LAST_UPDATE_DATE' AND dc.name = N'DF_IMPORT_MAPPING_LAST_UPDATE_DATE')
        ALTER TABLE dbo.IMPORT_MAPPING DROP CONSTRAINT DF_IMPORT_MAPPING_LAST_UPDATE_DATE;

    IF EXISTS (SELECT 1 FROM sys.columns
               WHERE object_id = OBJECT_ID(N'dbo.IMPORT_MAPPING') AND name = N'LAST_UPDATE_BY' AND is_nullable = 0)
    BEGIN
        UPDATE dbo.IMPORT_MAPPING SET LAST_UPDATE_BY = N'' WHERE LAST_UPDATE_BY IS NULL;
        ALTER TABLE dbo.IMPORT_MAPPING ALTER COLUMN LAST_UPDATE_BY NVARCHAR(80) NULL;
    END

    IF EXISTS (SELECT 1 FROM sys.columns
               WHERE object_id = OBJECT_ID(N'dbo.IMPORT_MAPPING') AND name = N'LAST_UPDATE_DATE' AND is_nullable = 0)
    BEGIN
        UPDATE dbo.IMPORT_MAPPING SET LAST_UPDATE_DATE = SYSDATETIME() WHERE LAST_UPDATE_DATE IS NULL;
        ALTER TABLE dbo.IMPORT_MAPPING ALTER COLUMN LAST_UPDATE_DATE DATETIME NULL;
    END
END

COMMIT TRANSACTION;

/* ---------- 收口断言：经办人/日期列必须可空（与建立组的口径分开） ---------- */
IF EXISTS (SELECT 1 FROM sys.columns
           WHERE object_id = OBJECT_ID(N'dbo.IMPORT_MAPPING') AND name IN (N'LAST_UPDATE_BY', N'LAST_UPDATE_DATE')
             AND is_nullable = 0)
    THROW 51897, N'IMPORT_MAPPING 的经办人/日期列必须是可空，迁移中止。', 1;

/* ---------- 收口断言：主键必须在（Upsert 依赖它做唯一性） ---------- */
IF NOT EXISTS (SELECT 1 FROM sys.indexes i
               WHERE i.object_id = OBJECT_ID(N'dbo.IMPORT_MAPPING') AND i.is_primary_key = 1)
    THROW 51896, N'IMPORT_MAPPING 缺少主键，迁移中止。', 1;

PRINT N'== 收口：导入映射记忆表就位（身份 = 用户 + 模块）==';
