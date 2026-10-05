-- ============================================================================
-- EOS.ERP migration 314: retire search-centre shell modules (2501-2508 + parent 25)
-- ----------------------------------------------------------------------------
-- Database: EOS.ERP (the single business database of the new system)
--
-- Context: 2501-2508 are eight "search centre" menu leaves inherited from the
-- legacy system, where every business domain had its own search page
-- (ClientSearch / OrderSearch / SupplierSearch / PurchaseSearch / ProduceSearch /
-- EmployeeSearch / ProSearch). The new system hosts a single search centre
-- (/search-center) whose module list is driven by MODULES.SEARCH_1 / SEARCH_2:
-- 36 business modules carry those flags today, none of 2501-2508 does. Hence:
--   ① all eight modules point at M_URL = /search-center with
--      SEARCH_1 = SEARCH_2 = 0, so they contribute no search surface and only
--      repeat the same page eight times in the menu; /search-center/{2501..2508}
--      cannot even preselect a module, those ids are not in the searchable list;
--   ② five of them (2501/2502/2503/2505/2508) have no master table at all;
--   ③ the column browse links that pointed at 2502/2503 (FIELDS.BROWSE_M_IDX,
--      9 rows) are already dead: WorkbenchBrowseResolver only links to targets
--      whose M_URL is /workbench and that own a master table, so these rows
--      degrade to plain text at runtime;
--   ④ report REPORT_ID = 'COP_ORDER_D' ('订单查询中心') is bound to module 2504
--      and retires together with it.
-- The parent folder 25 ('数据查询中心') holds exactly those eight children and
-- is removed as well.
--
-- Scope:
--   1. Report family of module 2504 (sort / conditions / inbox / subscription /
--      per-user state) and the REPORT row itself;
--   2. Permission rows: SYSDD / SYSDH for 2501-2508 and the parent 25;
--   3. Runtime state: workbench dirty flags / definition snapshots / idempotency;
--   4. FIELDS: clear the dead BROWSE_URL / BROWSE_M_IDX of the rows that
--      pointed at 2501-2508;
--   5. TABLES.T_REMARK and table-level MS_Description: drop the stale
--      "2504:订单查询中心；" / "2507:人事查询中心；" entries;
--   6. MODULES: physical delete of 2501-2508, then of the parent 25;
--   7. History (SYSDF / AUDIT_EVENT) is kept intact for traceability.
-- Idempotent: every step guarded by OBJECT_ID / EXISTS. All objects uppercase.
-- ============================================================================

SET NOCOUNT ON;

/* Some metadata tables carry filtered indexes / indexed views: DELETE and
   UPDATE need the correct SET options. */
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;

DECLARE @GUARD_MESSAGE NVARCHAR(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, @GUARD_MESSAGE, 1;

-- Guard: refuse to remove rows that regained children, or a parent folder that
-- gained other children (25 must hold exactly the eight modules being retired).
IF EXISTS (SELECT 1 FROM dbo.MODULES WHERE M_P_IDX BETWEEN 2501 AND 2508)
    THROW 50001, N'2501-2508 下仍有子节点，拒绝自动删除。', 1;

IF EXISTS (SELECT 1 FROM dbo.MODULES WHERE M_P_IDX = 25 AND M_IDX NOT BETWEEN 2501 AND 2508)
BEGIN
    DECLARE @EXTRA NVARCHAR(20) = (SELECT TOP 1 CAST(M_IDX AS NVARCHAR(20)) FROM dbo.MODULES WHERE M_P_IDX = 25 AND M_IDX NOT BETWEEN 2501 AND 2508);
    DECLARE @EXTRA_MESSAGE NVARCHAR(400) = N'父目录 25 下出现未预期的子模块 ' + @EXTRA + N'，拒绝自动删除。';
    THROW 50002, @EXTRA_MESSAGE, 1;
END

BEGIN TRANSACTION;

DECLARE @n INT;

-- ---------------------------------------------------------------------------
-- 1. Report family of module 2504 (children first).
-- ---------------------------------------------------------------------------
IF OBJECT_ID('dbo.REPORT', 'U') IS NOT NULL
BEGIN
    SELECT REPORT_ID
    INTO #DEAD_REPORT
    FROM dbo.REPORT
    WHERE M_IDX BETWEEN 2501 AND 2508;

    IF OBJECT_ID('dbo.REPORT_SORT', 'U') IS NOT NULL
    BEGIN
        DELETE FROM dbo.REPORT_SORT WHERE REPORT_ID IN (SELECT REPORT_ID FROM #DEAD_REPORT);
        SET @n = @@ROWCOUNT;
        PRINT N'[314] REPORT_SORT 删除：' + CAST(@n AS NVARCHAR(10)) + N' 行';
    END

    IF OBJECT_ID('dbo.SYSQR', 'U') IS NOT NULL
    BEGIN
        DELETE FROM dbo.SYSQR WHERE REPORT_ID IN (SELECT REPORT_ID FROM #DEAD_REPORT);
        SET @n = @@ROWCOUNT;
        PRINT N'[314] SYSQR 删除：' + CAST(@n AS NVARCHAR(10)) + N' 行';
    END

    IF OBJECT_ID('dbo.SYSQR_DA', 'U') IS NOT NULL
    BEGIN
        DELETE FROM dbo.SYSQR_DA WHERE M_IDX BETWEEN 2501 AND 2508;
        SET @n = @@ROWCOUNT;
        PRINT N'[314] SYSQR_DA 删除：' + CAST(@n AS NVARCHAR(10)) + N' 行';
    END

    IF OBJECT_ID('dbo.SYSQR_DEFAULT', 'U') IS NOT NULL
    BEGIN
        DELETE FROM dbo.SYSQR_DEFAULT WHERE M_IDX BETWEEN 2501 AND 2508;
        SET @n = @@ROWCOUNT;
        PRINT N'[314] SYSQR_DEFAULT 删除：' + CAST(@n AS NVARCHAR(10)) + N' 行';
    END

    IF OBJECT_ID('dbo.SYSQR_USER', 'U') IS NOT NULL
    BEGIN
        DELETE FROM dbo.SYSQR_USER WHERE M_IDX BETWEEN 2501 AND 2508;
        SET @n = @@ROWCOUNT;
        PRINT N'[314] SYSQR_USER 删除：' + CAST(@n AS NVARCHAR(10)) + N' 行';
    END

    IF OBJECT_ID('dbo.REPORT_INBOX', 'U') IS NOT NULL
    BEGIN
        DELETE FROM dbo.REPORT_INBOX
        WHERE M_IDX BETWEEN 2501 AND 2508
           OR REPORT_ID IN (SELECT REPORT_ID FROM #DEAD_REPORT);
        SET @n = @@ROWCOUNT;
        PRINT N'[314] REPORT_INBOX 删除：' + CAST(@n AS NVARCHAR(10)) + N' 行';
    END

    IF OBJECT_ID('dbo.REPORT_SUBSCRIPTION', 'U') IS NOT NULL
    BEGIN
        DELETE FROM dbo.REPORT_SUBSCRIPTION
        WHERE M_IDX BETWEEN 2501 AND 2508
           OR REPORT_ID IN (SELECT REPORT_ID FROM #DEAD_REPORT);
        SET @n = @@ROWCOUNT;
        PRINT N'[314] REPORT_SUBSCRIPTION 删除：' + CAST(@n AS NVARCHAR(10)) + N' 行';
    END

    IF OBJECT_ID('dbo.SYSDD_REPORT', 'U') IS NOT NULL
    BEGIN
        DELETE FROM dbo.SYSDD_REPORT
        WHERE M_IDX BETWEEN 2501 AND 2508
           OR REPORT_ID IN (SELECT REPORT_ID FROM #DEAD_REPORT);
        SET @n = @@ROWCOUNT;
        PRINT N'[314] SYSDD_REPORT 删除：' + CAST(@n AS NVARCHAR(10)) + N' 行';
    END

    DELETE FROM dbo.REPORT WHERE REPORT_ID IN (SELECT REPORT_ID FROM #DEAD_REPORT);
    SET @n = @@ROWCOUNT;
    PRINT N'[314] REPORT 删除：' + CAST(@n AS NVARCHAR(10)) + N' 行';

    DROP TABLE #DEAD_REPORT;
END

-- ---------------------------------------------------------------------------
-- 2. Permission rows: the eight modules and the parent folder.
-- ---------------------------------------------------------------------------
IF OBJECT_ID('dbo.SYSDD', 'U') IS NOT NULL
BEGIN
    DELETE FROM dbo.SYSDD WHERE M_IDX BETWEEN 2501 AND 2508 OR M_IDX = 25;
    SET @n = @@ROWCOUNT;
    PRINT N'[314] SYSDD 个人权限行删除：' + CAST(@n AS NVARCHAR(10)) + N' 行';
END

IF OBJECT_ID('dbo.SYSDH', 'U') IS NOT NULL
BEGIN
    DELETE FROM dbo.SYSDH WHERE M_IDX BETWEEN 2501 AND 2508 OR M_IDX = 25;
    SET @n = @@ROWCOUNT;
    PRINT N'[314] SYSDH 组权限行删除：' + CAST(@n AS NVARCHAR(10)) + N' 行';
END

-- ---------------------------------------------------------------------------
-- 3. Runtime state (the modules are not workbench-hosted, so usually empty).
-- ---------------------------------------------------------------------------
IF OBJECT_ID('dbo.WORKBENCH_MODULE_DIRTY', 'U') IS NOT NULL
BEGIN
    DELETE FROM dbo.WORKBENCH_MODULE_DIRTY WHERE M_IDX BETWEEN 2501 AND 2508;
    SET @n = @@ROWCOUNT;
    PRINT N'[314] WORKBENCH_MODULE_DIRTY 删除：' + CAST(@n AS NVARCHAR(10)) + N' 行';
END

IF OBJECT_ID('dbo.WORKBENCH_DEFINITION_SNAPSHOT', 'U') IS NOT NULL
BEGIN
    DELETE FROM dbo.WORKBENCH_DEFINITION_SNAPSHOT WHERE M_IDX BETWEEN 2501 AND 2508;
    SET @n = @@ROWCOUNT;
    PRINT N'[314] WORKBENCH_DEFINITION_SNAPSHOT 删除：' + CAST(@n AS NVARCHAR(10)) + N' 行';
END

IF OBJECT_ID('dbo.WORKBENCH_IDEMPOTENCY', 'U') IS NOT NULL
BEGIN
    DELETE FROM dbo.WORKBENCH_IDEMPOTENCY WHERE M_IDX BETWEEN 2501 AND 2508;
    SET @n = @@ROWCOUNT;
    PRINT N'[314] WORKBENCH_IDEMPOTENCY 删除：' + CAST(@n AS NVARCHAR(10)) + N' 行';
END

-- ---------------------------------------------------------------------------
-- 4. FIELDS: clear the dead browse links (target modules are gone).
-- ---------------------------------------------------------------------------
UPDATE dbo.FIELDS
SET BROWSE_URL = NULL,
    BROWSE_M_IDX = NULL,
    LAST_UPDATE_BY = N'EOS-MIG',
    LAST_UPDATE_DATE = GETDATE()
WHERE BROWSE_M_IDX BETWEEN 2501 AND 2508;
SET @n = @@ROWCOUNT;
PRINT N'[314] FIELDS 失效浏览链接清理：' + CAST(@n AS NVARCHAR(10)) + N' 行';

-- ---------------------------------------------------------------------------
-- 5a. TABLES.T_REMARK: drop the "NNNN:名称；" entries of the retired modules.
-- ---------------------------------------------------------------------------
DECLARE @stale TABLE (M INT PRIMARY KEY);
INSERT @stale (M) VALUES (2504), (2507);

DECLARE @m INT, @pat NVARCHAR(20);
DECLARE stale_cursor CURSOR LOCAL FAST_FORWARD FOR SELECT M FROM @stale ORDER BY M;

OPEN stale_cursor;
FETCH NEXT FROM stale_cursor INTO @m;
WHILE @@FETCH_STATUS = 0
BEGIN
    SET @pat = CAST(@m AS NVARCHAR(20)) + N':';

    UPDATE dbo.TABLES
    SET T_REMARK = STUFF(T_REMARK,
                         CHARINDEX(@pat, T_REMARK),
                         CHARINDEX(N'；', T_REMARK, CHARINDEX(@pat, T_REMARK)) - CHARINDEX(@pat, T_REMARK) + 1,
                         N''),
        LAST_UPDATE_BY = N'EOS-MIG',
        LAST_UPDATE_DATE = GETDATE()
    WHERE CHARINDEX(@pat, T_REMARK) > 0
      AND CHARINDEX(N'；', T_REMARK, CHARINDEX(@pat, T_REMARK)) > CHARINDEX(@pat, T_REMARK);

    FETCH NEXT FROM stale_cursor INTO @m;
END;
CLOSE stale_cursor;
DEALLOCATE stale_cursor;
PRINT N'[314] TABLES.T_REMARK 模块号残留清理完成。';

-- ---------------------------------------------------------------------------
-- 5b. Table-level extended property (MS_Description): same entries.
--     Values are rewritten from their current content so no long literal is
--     duplicated here (the remark text may be longer than the printed preview).
-- ---------------------------------------------------------------------------
DECLARE @ep_obj SYSNAME, @ep_val NVARCHAR(4000), @ep_new NVARCHAR(4000), @ep_sql NVARCHAR(MAX);
DECLARE ep_cursor CURSOR LOCAL FAST_FORWARD FOR
    SELECT OBJECT_NAME(major_id), CAST(value AS NVARCHAR(4000))
    FROM sys.extended_properties
    WHERE class = 1 AND minor_id = 0 AND name = N'MS_Description'
      AND (CAST(value AS NVARCHAR(MAX)) LIKE N'%2501:%'
        OR CAST(value AS NVARCHAR(MAX)) LIKE N'%2502:%'
        OR CAST(value AS NVARCHAR(MAX)) LIKE N'%2503:%'
        OR CAST(value AS NVARCHAR(MAX)) LIKE N'%2504:%'
        OR CAST(value AS NVARCHAR(MAX)) LIKE N'%2505:%'
        OR CAST(value AS NVARCHAR(MAX)) LIKE N'%2506:%'
        OR CAST(value AS NVARCHAR(MAX)) LIKE N'%2507:%'
        OR CAST(value AS NVARCHAR(MAX)) LIKE N'%2508:%');

OPEN ep_cursor;
FETCH NEXT FROM ep_cursor INTO @ep_obj, @ep_val;
WHILE @@FETCH_STATUS = 0
BEGIN
    SET @ep_new = @ep_val;
    SET @ep_new = REPLACE(@ep_new, N'2501:产品查询中心；', N'');
    SET @ep_new = REPLACE(@ep_new, N'2502:客户查询中心；', N'');
    SET @ep_new = REPLACE(@ep_new, N'2503:厂商查询中心；', N'');
    SET @ep_new = REPLACE(@ep_new, N'2504:订单查询中心；', N'');
    SET @ep_new = REPLACE(@ep_new, N'2505:采购单查询中心；', N'');
    SET @ep_new = REPLACE(@ep_new, N'2506:生产查询中心；', N'');
    SET @ep_new = REPLACE(@ep_new, N'2507:人事查询中心；', N'');
    SET @ep_new = REPLACE(@ep_new, N'2508:通用查询中心；', N'');

    IF @ep_new <> @ep_val
    BEGIN
        SET @ep_sql = N'EXEC sys.sp_updateextendedproperty @name = N''MS_Description'', @value = @v, '
                    + N'@level0type = N''SCHEMA'', @level0name = N''dbo'', '
                    + N'@level1type = N''TABLE'', @level1name = @t';
        EXEC sp_executesql @ep_sql, N'@v NVARCHAR(4000), @t SYSNAME', @v = @ep_new, @t = @ep_obj;
        PRINT N'[314] 扩展属性 MS_Description 清理：' + @ep_obj;
    END

    FETCH NEXT FROM ep_cursor INTO @ep_obj, @ep_val;
END;
CLOSE ep_cursor;
DEALLOCATE ep_cursor;

-- ---------------------------------------------------------------------------
-- 6. MODULES: physical delete of the eight leaves, then the parent folder.
-- ---------------------------------------------------------------------------
DELETE FROM dbo.MODULES WHERE M_IDX BETWEEN 2501 AND 2508;
SET @n = @@ROWCOUNT;
PRINT N'[314] MODULES 2501-2508 物理删除：' + CAST(@n AS NVARCHAR(10)) + N' 行';

DELETE FROM dbo.MODULES WHERE M_IDX = 25;
SET @n = @@ROWCOUNT;
PRINT N'[314] MODULES 父目录 25 物理删除：' + CAST(@n AS NVARCHAR(10)) + N' 行';

-- ---------------------------------------------------------------------------
-- 7. History kept for traceability (counts only, no delete).
-- ---------------------------------------------------------------------------
DECLARE @SYSDF_CNT INT = 0;
IF OBJECT_ID('dbo.SYSDF', 'U') IS NOT NULL
    SELECT @SYSDF_CNT = COUNT_BIG(1) FROM dbo.SYSDF WITH (NOLOCK) WHERE M_IDX BETWEEN 2501 AND 2508 OR M_IDX = 25;
PRINT N'[314] 历史审计 SYSDF 保留 ' + CAST(@SYSDF_CNT AS NVARCHAR(10)) + N' 行（不删除，供追溯）';

DECLARE @AUDIT_CNT INT = 0;
IF OBJECT_ID('dbo.AUDIT_EVENT', 'U') IS NOT NULL
    SELECT @AUDIT_CNT = COUNT_BIG(1) FROM dbo.AUDIT_EVENT WITH (NOLOCK) WHERE M_IDX BETWEEN 2501 AND 2508 OR M_IDX = 25;
PRINT N'[314] 历史审计 AUDIT_EVENT 保留 ' + CAST(@AUDIT_CNT AS NVARCHAR(10)) + N' 行（不删除，供追溯）';

COMMIT TRANSACTION;

PRINT N'[314] 查询中心壳模块（2501-2508 与父目录 25）及挂靠报表下线完成（物理删除）。';
