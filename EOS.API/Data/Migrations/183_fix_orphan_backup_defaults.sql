-- ============================================================================
-- EOS.ERP migration 184: 补齐归档表的默认值约束
-- ----------------------------------------------------------------------------
-- INV_PRO_DEPOT_ORPHAN_DEPOT_BACKUP 由 SELECT * INTO 生成，而该语句不复制默认值
-- 约束，导致表上的建立组与状态位缺少 DEFAULT，结构口径巡检会判 FAIL。
-- 本迁移按结构口径补回这三个约束（幂等：已有即跳过）。
--
-- 说明：该归档表存放的是从余额表中移除的孤立库别行，用于按需还原；补默认值只为
-- 满足全库统一的结构口径，不改变归档内容。
--
-- 回滚：DROP 这三个默认值约束。
-- ============================================================================

SET NOCOUNT ON;

SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET ARITHABORT ON;

DECLARE @GUARD_MESSAGE NVARCHAR(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 51300, @GUARD_MESSAGE, 1;

IF OBJECT_ID(N'dbo.INV_PRO_DEPOT_ORPHAN_DEPOT_BACKUP', N'U') IS NULL
BEGIN
    PRINT N'== 归档表不存在（无需补默认值）==';
END
ELSE
BEGIN
    IF NOT EXISTS (SELECT 1 FROM sys.default_constraints
                   WHERE parent_object_id = OBJECT_ID(N'dbo.INV_PRO_DEPOT_ORPHAN_DEPOT_BACKUP')
                     AND parent_column_id = COLUMNPROPERTY(OBJECT_ID(N'dbo.INV_PRO_DEPOT_ORPHAN_DEPOT_BACKUP'), N'CREATE_PERSON', 'ColumnId'))
        ALTER TABLE dbo.INV_PRO_DEPOT_ORPHAN_DEPOT_BACKUP
            ADD CONSTRAINT DF_INV_PRO_DEPOT_ORPHAN_CREATE_PERSON DEFAULT (N'') FOR CREATE_PERSON;

    IF NOT EXISTS (SELECT 1 FROM sys.default_constraints
                   WHERE parent_object_id = OBJECT_ID(N'dbo.INV_PRO_DEPOT_ORPHAN_DEPOT_BACKUP')
                     AND parent_column_id = COLUMNPROPERTY(OBJECT_ID(N'dbo.INV_PRO_DEPOT_ORPHAN_DEPOT_BACKUP'), N'CREATE_DATE', 'ColumnId'))
        ALTER TABLE dbo.INV_PRO_DEPOT_ORPHAN_DEPOT_BACKUP
            ADD CONSTRAINT DF_INV_PRO_DEPOT_ORPHAN_CREATE_DATE DEFAULT (GETDATE()) FOR CREATE_DATE;

    IF NOT EXISTS (SELECT 1 FROM sys.default_constraints
                   WHERE parent_object_id = OBJECT_ID(N'dbo.INV_PRO_DEPOT_ORPHAN_DEPOT_BACKUP')
                     AND parent_column_id = COLUMNPROPERTY(OBJECT_ID(N'dbo.INV_PRO_DEPOT_ORPHAN_DEPOT_BACKUP'), N'CONFIRM_TAG', 'ColumnId'))
        ALTER TABLE dbo.INV_PRO_DEPOT_ORPHAN_DEPOT_BACKUP
            ADD CONSTRAINT DF_INV_PRO_DEPOT_ORPHAN_CONFIRM_TAG DEFAULT ((0)) FOR CONFIRM_TAG;

    PRINT N'== 归档表的建立组 / 状态位默认值约束已补齐 ==';
END

/* ---------- 收口断言：建立组与状态位必须 NOT NULL 且有 DEFAULT ---------- */
IF OBJECT_ID(N'dbo.INV_PRO_DEPOT_ORPHAN_DEPOT_BACKUP', N'U') IS NOT NULL
   AND EXISTS (
       SELECT 1 FROM sys.columns c
       LEFT JOIN sys.default_constraints dc ON dc.parent_object_id = c.object_id AND dc.parent_column_id = c.column_id
       WHERE c.object_id = OBJECT_ID(N'dbo.INV_PRO_DEPOT_ORPHAN_DEPOT_BACKUP')
         AND c.name IN (N'CREATE_PERSON', N'CREATE_DATE', N'CONFIRM_TAG')
         AND (c.is_nullable = 1 OR dc.object_id IS NULL))
    THROW 51301, N'归档表仍有建立组 / 状态位列缺少默认值约束，迁移中止。', 1;

PRINT N'== 收口：归档表结构口径与全库一致 ==';
