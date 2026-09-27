-- Field data source dead-config cleanup:
-- 1) HR_EMPLOYEE.POLITY_ID source table typo: HR_PLOITY does not exist; HR_POLITY does
--    (drift guard: only when SOURCE_T_ID still reads HR_PLOITY and HR_POLITY exists).
-- 2) FIELDS orphan metadata rows for the 24 legacy CHOOSE_* columns that were dropped
--    from the physical table . Their FIELDS rows, FIELD_DATASOURCE rows
--    (cascade via FK) and dangling SYSQL_FIELDS/migration-log references are removed.
--    Drift guard: rows are only removed when the physical column does NOT exist,
--    so a future column recreation is never touched. CHOOSE_MULTI / CHOOSE_PAGE survive.

SET NOCOUNT ON;

DECLARE @GUARD NVARCHAR(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, @GUARD, 1;

BEGIN TRANSACTION;

-- 1) 政治面貌选择器源表拼写修正（漂移守卫：原文一致 + 目标表存在才落）
UPDATE ds SET SOURCE_T_ID = N'HR_POLITY', LAST_UPDATE_BY = N'EOS-MIG', LAST_UPDATE_DATE = SYSDATETIME()
FROM dbo.FIELD_DATASOURCE ds
WHERE ds.T_ID = N'HR_EMPLOYEE' AND ds.F_ID = N'POLITY_ID' AND ds.SERIAL_NO = 1
  AND LTRIM(RTRIM(ISNULL(ds.SOURCE_T_ID, N''))) = N'HR_PLOITY'
  AND EXISTS (SELECT 1 FROM sys.tables t WHERE t.name = N'HR_POLITY');

DECLARE @SRC_FIXED INT = @@ROWCOUNT;

-- 2) FIELDS 孤儿元数据（24 列已物理删除；漂移守卫：物理列不存在才删；级联清 FIELD_DATASOURCE）
DELETE FROM dbo.FIELDS
WHERE T_ID = N'FIELDS'
  AND F_ID LIKE N'CHOOSE%'
  AND F_ID NOT IN (N'CHOOSE_MULTI', N'CHOOSE_PAGE')
  AND NOT EXISTS (
        SELECT 1 FROM sys.columns c
        JOIN sys.objects o ON c.object_id = o.object_id
        JOIN sys.schemas s ON o.schema_id = s.schema_id
        WHERE s.name = N'dbo' AND o.name = N'FIELDS' AND c.name = FIELDS.F_ID);

DECLARE @META_REMOVED INT = @@ROWCOUNT;

-- 3) SYSQL_FIELDS 悬空引用（指向已物理删除列的用户列选择）
DELETE FROM f
FROM dbo.SYSQL_FIELDS f
WHERE f.T_ID = N'FIELDS'
  AND f.F_ID LIKE N'CHOOSE%'
  AND f.F_ID NOT IN (N'CHOOSE_MULTI', N'CHOOSE_PAGE')
  AND NOT EXISTS (
        SELECT 1 FROM sys.columns c
        JOIN sys.objects o ON c.object_id = o.object_id
        JOIN sys.schemas s ON o.schema_id = s.schema_id
        WHERE s.name = N'dbo' AND o.name = N'FIELDS' AND c.name = f.F_ID);

-- 4) 迁移日志中 FIELDS 孤儿项（对应字段元数据已删除，队列项失效）
DELETE FROM l
FROM dbo.CHOOSER_FILTER_MIGRATION_LOG l
WHERE l.T_ID = N'FIELDS'
  AND l.F_ID LIKE N'CHOOSE%'
  AND l.F_ID NOT IN (N'CHOOSE_MULTI', N'CHOOSE_PAGE')
  AND NOT EXISTS (SELECT 1 FROM dbo.FIELDS f WHERE f.T_ID = N'FIELDS' AND f.F_ID = l.F_ID);

PRINT N'CHOOSER cleanup: source fixed=' + CAST(@SRC_FIXED AS NVARCHAR(10))
    + N', orphan FIELDS rows removed=' + CAST(@META_REMOVED AS NVARCHAR(10)) + N'.';

COMMIT TRANSACTION;
