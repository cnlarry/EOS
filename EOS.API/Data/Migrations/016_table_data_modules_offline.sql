-- ============================================================================
-- EOS.ERP migration 016: remove table data maintenance modules (2310/2312)
-- ----------------------------------------------------------------------------
-- Database: EOS.ERP (the single business database of the new system)
--
-- Context: 2310 (MagTableData: full table TRUNCATE) and 2312 (MagTableData2:
-- range-delete by CREATE_DATE/CLIENT_ID/SUPPLIER_ID + FK orphan cleanup) were
-- destructive data-cleaning tools that are not ported to the new system because:
--   ① "Pick any table → truncate/delete" violates the project's security boundary
--      (dynamic table name + irreversible destruction); DB cleanup is an
--      implementation/DBA operation via script + backup.
--   ② The controlled read-only browse page (/admin/table-data) — which bypasses
--      cost/secrecy/deny-field filters and EXEC_TAG/DATA_FILTER scope — is also removed.
--   ③ Business data viewing goes through each module's workbench (full field permissions
--      + data scope). Table structure inspection is handled by 2302 and 2303.
-- Both modules (2310, 2312) are physically deleted with no replacement module.
--
-- Scope:
--   1. SYSDD / SYSDH: delete permission rows for 2310/2312;
--   2. WORKBENCH_MODULE_DIRTY / WORKBENCH_DEFINITION_SNAPSHOT: defensive cleanup;
--   3. MODULES: physically delete 2310/2312;
--   4. Audit/history (SYSDF, AUDIT_EVENT) kept intact for traceability.
-- Idempotent: guarded by EXISTS/IF. Naming convention: all uppercase.
-- ============================================================================

SET NOCOUNT ON;

DECLARE @GUARD_MESSAGE NVARCHAR(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, @GUARD_MESSAGE, 1;

BEGIN TRANSACTION;

DECLARE @MIG NVARCHAR(20) = N'EOS-23';

-- 1. SYSDD：删除用户个人权限行（无承接模块，直接删除不合并）
DELETE FROM dbo.SYSDD WHERE M_IDX IN (2310, 2312);
PRINT N'[EOS-23] SYSDD 2310/2312 权限行删除：' + CAST(@@ROWCOUNT AS NVARCHAR(10)) + N' 行';

-- 2. SYSDH：删除用户组权限行（同上）
DELETE FROM dbo.SYSDH WHERE M_IDX IN (2310, 2312);
PRINT N'[EOS-23] SYSDH 2310/2312 权限行删除：' + CAST(@@ROWCOUNT AS NVARCHAR(10)) + N' 行';

-- 3. WORKBENCH 脏标记 / Definition 快照防御性清理（2310/2312 非工作台模块，通常为 0 行）
IF OBJECT_ID('dbo.WORKBENCH_MODULE_DIRTY','U') IS NOT NULL
BEGIN
    DELETE FROM dbo.WORKBENCH_MODULE_DIRTY WHERE MODULE_ID IN (2310, 2312);
    PRINT N'[EOS-23] WORKBENCH_MODULE_DIRTY 2310/2312 清理：' + CAST(@@ROWCOUNT AS NVARCHAR(10)) + N' 行';
END

IF OBJECT_ID('dbo.WORKBENCH_DEFINITION_SNAPSHOT','U') IS NOT NULL
BEGIN
    DELETE FROM dbo.WORKBENCH_DEFINITION_SNAPSHOT WHERE MODULE_ID IN (2310, 2312);
    PRINT N'[EOS-23] WORKBENCH_DEFINITION_SNAPSHOT 2310/2312 清理：' + CAST(@@ROWCOUNT AS NVARCHAR(10)) + N' 行';
END

-- 4. MODULES：2310/2312 物理删除（父目录 2311 下仍有 2302/2303/2307，不空组）
DELETE FROM dbo.MODULES WHERE M_IDX IN (2310, 2312);
PRINT N'[EOS-23] MODULES 2310/2312 物理删除完成。';

-- 5. 留档：审计/历史不物理删除（保持追溯），仅输出计数
DECLARE @SYSDF_CNT INT = 0;
IF OBJECT_ID('dbo.SYSDF','U') IS NOT NULL
    SELECT @SYSDF_CNT = COUNT_BIG(1) FROM dbo.SYSDF WITH (NOLOCK) WHERE M_IDX IN (2310, 2312);
PRINT N'[EOS-23] 历史审计 SYSDF.M_IDX IN (2310,2312) 保留 ' + CAST(@SYSDF_CNT AS NVARCHAR(10)) + N' 行（不删除，供追溯）';

DECLARE @AUDIT_CNT INT = 0;
IF OBJECT_ID('dbo.AUDIT_EVENT','U') IS NOT NULL
    SELECT @AUDIT_CNT = COUNT_BIG(1) FROM dbo.AUDIT_EVENT WITH (NOLOCK) WHERE MODULE_ID IN (2310, 2312);
PRINT N'[EOS-23] 历史审计 AUDIT_EVENT.MODULE_ID IN (2310,2312) 保留 ' + CAST(@AUDIT_CNT AS NVARCHAR(10)) + N' 行（不删除，供追溯）';

COMMIT TRANSACTION;
PRINT N'[EOS-23] 数据表数据维护模块（2310/2312）下线完成（物理删除，页面/端点/路由代码随本次提交移除）。';
