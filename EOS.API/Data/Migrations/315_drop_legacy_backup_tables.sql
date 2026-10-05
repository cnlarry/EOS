-- ============================================================================
-- EOS.ERP migration 315: drop legacy backup tables (BAK / Backup / GHOST)
-- ----------------------------------------------------------------------------
-- Database: EOS.ERP (the single business database of the new system)
--
-- Context: five tables in the database are snapshots taken while earlier
-- migrations were prepared, not part of the product model:
--   FIELDS_GHOST_BAK_20260909            214 号迁移前对 FIELDS 的备份
--   TABLES_GHOST_BAK_20260909            TABLES 的同批备份
--   FIELD_DATASOURCE_RESIDUE_BAK_20260909 字段数据来源重构期间的中间表备份
--   MODULES_Backup_ADR004                ADR-004 迁移前对 MODULES 的备份
--   SYSDL_Backup_ADR004                  ADR-004 迁移前对 SYSDL 的备份
-- Migration 274 already declared by comment that the two GHOST tables "must
-- not be used as a reference"; they have been carried in every schema export
-- since, and the export script had to exclude them by name pattern so they
-- would not land in the bootstrap baseline.
--
-- Verified before writing:
--   1. no foreign key references them, and they have no foreign keys of their
--      own (sys.foreign_keys on both sides = 0);
--   2. no view / function / procedure references them
--      (sys.sql_expression_dependencies = 0);
--   3. no application code references them (repository-wide search: the only
--      hits are historical migration scripts and the export script's own
--      exclusion pattern).
--
-- Scope:
--   1. drop the five tables;
--   2. retire their metadata registrations (TABLES / FIELDS), which would
--      otherwise point at tables that no longer exist.
--
-- This is a destructive migration: the tables are dropped, not archived. The
-- data in them is a pre-migration snapshot of structures that the live tables
-- have since moved past, so nothing usable is lost.
-- ============================================================================

SET NOCOUNT ON;
GO

-- ---------------------------------------------------------------- 1. 元数据登记
-- 先删登记（FIELDS 的 T_ID 指向 TABLES 的主表标识），再删表，避免留下悬空引用。
DELETE FROM dbo.FIELDS
 WHERE T_ID IN (N'FIELDS_GHOST_BAK_20260909',
                N'TABLES_GHOST_BAK_20260909',
                N'FIELD_DATASOURCE_RESIDUE_BAK_20260909',
                N'MODULES_Backup_ADR004',
                N'SYSDL_Backup_ADR004');
GO

DELETE FROM dbo.TABLES
 WHERE T_ID IN (N'FIELDS_GHOST_BAK_20260909',
                N'TABLES_GHOST_BAK_20260909',
                N'FIELD_DATASOURCE_RESIDUE_BAK_20260909',
                N'MODULES_Backup_ADR004',
                N'SYSDL_Backup_ADR004');
GO

-- ---------------------------------------------------------------- 2. 删表
-- OBJECT_ID 判空，使迁移在已清理过的库上重复执行也安全。
IF OBJECT_ID(N'dbo.FIELDS_GHOST_BAK_20260909', N'U') IS NOT NULL
    DROP TABLE dbo.FIELDS_GHOST_BAK_20260909;
GO

IF OBJECT_ID(N'dbo.TABLES_GHOST_BAK_20260909', N'U') IS NOT NULL
    DROP TABLE dbo.TABLES_GHOST_BAK_20260909;
GO

IF OBJECT_ID(N'dbo.FIELD_DATASOURCE_RESIDUE_BAK_20260909', N'U') IS NOT NULL
    DROP TABLE dbo.FIELD_DATASOURCE_RESIDUE_BAK_20260909;
GO

IF OBJECT_ID(N'dbo.MODULES_Backup_ADR004', N'U') IS NOT NULL
    DROP TABLE dbo.MODULES_Backup_ADR004;
GO

IF OBJECT_ID(N'dbo.SYSDL_Backup_ADR004', N'U') IS NOT NULL
    DROP TABLE dbo.SYSDL_Backup_ADR004;
GO

-- ---------------------------------------------------------------- 3. 收口断言
-- 五张表都不该还在库里，也不该还有元数据登记。
IF EXISTS (SELECT 1 FROM sys.objects
            WHERE name IN (N'FIELDS_GHOST_BAK_20260909',
                           N'TABLES_GHOST_BAK_20260909',
                           N'FIELD_DATASOURCE_RESIDUE_BAK_20260909',
                           N'MODULES_Backup_ADR004',
                           N'SYSDL_Backup_ADR004'))
    THROW 60001, '315: 历史备份表仍存在，删除未完成。', 1;
GO

IF EXISTS (SELECT 1 FROM dbo.TABLES
            WHERE T_ID IN (N'FIELDS_GHOST_BAK_20260909',
                           N'TABLES_GHOST_BAK_20260909',
                           N'FIELD_DATASOURCE_RESIDUE_BAK_20260909',
                           N'MODULES_Backup_ADR004',
                           N'SYSDL_Backup_ADR004'))
    THROW 60002, '315: 备份表的 TABLES 登记未清理。', 1;
GO

IF EXISTS (SELECT 1 FROM dbo.FIELDS
            WHERE T_ID IN (N'FIELDS_GHOST_BAK_20260909',
                           N'TABLES_GHOST_BAK_20260909',
                           N'FIELD_DATASOURCE_RESIDUE_BAK_20260909',
                           N'MODULES_Backup_ADR004',
                           N'SYSDL_Backup_ADR004'))
    THROW 60003, '315: 备份表的 FIELDS 登记未清理。', 1;
GO
