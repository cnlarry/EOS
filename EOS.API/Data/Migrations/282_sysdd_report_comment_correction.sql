-- 订正 SYSDD_REPORT 的表注释与 M_IDX 列注释。
--
-- 现状：表注释是「用户报表权限（系统管理 / 基础参数 / 工作流）」，M_IDX 列注释是「权限ID」。
-- 而这张表已经不承载任何权限语义了——报表可见性的唯一真源是归属模块的 `REPORT_TAG`，
-- 表上原先的四个权限列（PREVIEW/PRINT/EXPORT_TAG、DATA_FILTER）已随权限例外层退役，
-- 现在只剩收藏、收藏排序与最近使用时间三项用户状态。
--
-- 为什么必须改：留着"看起来还有权限语义、实际没有"的注释，下一个人会照着它做设计——
-- 这正是这类模型反复长出第二处真源的开端。注释是库的一部分，不是文档的一部分。

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @GUARD_MESSAGE NVARCHAR(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, @GUARD_MESSAGE, 1;

BEGIN TRANSACTION;

DECLARE @TableDescription NVARCHAR(400) =
    N'报表中心的用户状态：收藏标记、收藏排序与最近使用时间。报表可见性由归属模块的 REPORT_TAG 决定，本表不含权限语义。';
DECLARE @ModuleColumnDescription NVARCHAR(400) =
    N'归属模块号（MODULES.M_IDX）：报表的数据集、筛选条件与权限都按它判定。';

IF OBJECT_ID(N'dbo.SYSDD_REPORT') IS NULL
    THROW 55930, N'SYSDD_REPORT 表不存在，中止。', 1;

-- ---------------------------------------------------------------- 表注释
IF EXISTS (SELECT 1 FROM sys.extended_properties
           WHERE major_id = OBJECT_ID(N'dbo.SYSDD_REPORT') AND minor_id = 0 AND name = N'MS_Description')
    EXEC sys.sp_updateextendedproperty @name = N'MS_Description', @value = @TableDescription,
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'SYSDD_REPORT';
ELSE
    EXEC sys.sp_addextendedproperty @name = N'MS_Description', @value = @TableDescription,
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'SYSDD_REPORT';

-- ---------------------------------------------------------------- M_IDX 列注释
IF EXISTS (SELECT 1 FROM sys.extended_properties
           WHERE major_id = OBJECT_ID(N'dbo.SYSDD_REPORT') AND minor_id = COLUMNPROPERTY(OBJECT_ID(N'dbo.SYSDD_REPORT'), N'M_IDX', 'ColumnId')
             AND name = N'MS_Description')
    EXEC sys.sp_updateextendedproperty @name = N'MS_Description', @value = @ModuleColumnDescription,
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'SYSDD_REPORT',
        @level2type = N'COLUMN', @level2name = N'M_IDX';
ELSE
    EXEC sys.sp_addextendedproperty @name = N'MS_Description', @value = @ModuleColumnDescription,
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'SYSDD_REPORT',
        @level2type = N'COLUMN', @level2name = N'M_IDX';

-- ---------------------------------------------------------------- 后置自证
IF EXISTS (SELECT 1 FROM sys.extended_properties
           WHERE major_id = OBJECT_ID(N'dbo.SYSDD_REPORT') AND minor_id = 0 AND name = N'MS_Description'
             AND CAST(value AS NVARCHAR(400)) <> @TableDescription)
    THROW 55931, N'表注释回读不一致，中止。', 1;

IF EXISTS (SELECT 1 FROM sys.extended_properties
           WHERE major_id = OBJECT_ID(N'dbo.SYSDD_REPORT')
             AND minor_id = COLUMNPROPERTY(OBJECT_ID(N'dbo.SYSDD_REPORT'), N'M_IDX', 'ColumnId')
             AND name = N'MS_Description'
             AND CAST(value AS NVARCHAR(400)) <> @ModuleColumnDescription)
    THROW 55932, N'M_IDX 列注释回读不一致，中止。', 1;

COMMIT TRANSACTION;
