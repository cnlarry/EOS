-- Unified form cell grouping backfill (part 2): group name fields via the chooser return mapping.
--
-- Context: part 1 grouped "code + name" combos by core-name match, but some name fields
-- do not name-match their code field yet are still filled via the chooser return mapping
-- (FIELD_DATASOURCE.RETURN_ITEMS), e.g.:
--   - category fields (*_TYPE) → BILL_NAME (bill type name), e.g. REPAIR_TYPE→BILL_NAME;
--   - product code (PRO_NO) → COLOR_NAME (color name returned with product selection).
-- These name fields were still filtered because master reads use includeVirtual=false and
-- they had no FORM_CELL_GROUP.
-- Rule: use FIELD_DATASOURCE.RETURN_ITEMS as the authoritative link. Any virtual _NAME
-- target of a code field's return mapping that is ungrouped joins that code field's group
-- (ROLE=2); if the code field itself is ungrouped, it is grouped first (ROLE=1).
-- Runs after part 1; fields already grouped there are skipped (ungrouped-only update).
-- Idempotent: only ungrouped fields are updated.

SET NOCOUNT ON;

DECLARE @GUARD_MESSAGE NVARCHAR(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, @GUARD_MESSAGE, 1;

-- 步骤 1：为主表「编号字段」补分组（自身未分组，且回填映射含未分组的虚拟 _NAME 字段）
UPDATE fid
SET fid.FORM_CELL_GROUP   = CASE WHEN RIGHT(fid.F_ID, 3) IN (N'_ID', N'_NO')
                                 THEN LEFT(fid.F_ID, LEN(fid.F_ID) - 3)
                                 ELSE fid.F_ID END,
    fid.FORM_CELL_ROLE    = 1,
    fid.LAST_UPDATE_BY    = N'MIGRATION',
    fid.LAST_UPDATE_DATE  = GETDATE()
FROM dbo.FIELDS fid
WHERE fid.T_ID LIKE N'%_M'
  AND (fid.FORM_CELL_GROUP IS NULL OR LTRIM(RTRIM(fid.FORM_CELL_GROUP)) = N'')
  AND EXISTS (
        SELECT 1
        FROM dbo.FIELD_DATASOURCE ds
        CROSS APPLY OPENJSON(ds.RETURN_ITEMS) item
        JOIN dbo.FIELDS fname
          ON fname.T_ID = fid.T_ID
         AND fname.F_ID = JSON_VALUE(item.value, '$.target')
        WHERE ds.T_ID = fid.T_ID
          AND LTRIM(RTRIM(ds.F_ID)) = fid.F_ID
          AND JSON_VALUE(item.value, '$.target') LIKE N'%_NAME'
          AND COALESCE(fname.IS_VIRTUAL, 0) = 1
          AND (fname.FORM_CELL_GROUP IS NULL OR LTRIM(RTRIM(fname.FORM_CELL_GROUP)) = N'')
      );

-- 步骤 2：为主表「名称字段」补分组（虚拟 _NAME、未分组、回填它的编号字段已有分组）
UPDATE fname
SET fname.FORM_CELL_GROUP   = fid.FORM_CELL_GROUP,
    fname.FORM_CELL_ROLE    = 2,
    fname.LAST_UPDATE_BY    = N'MIGRATION',
    fname.LAST_UPDATE_DATE  = GETDATE()
FROM dbo.FIELDS fname
JOIN (
        SELECT DISTINCT ds.T_ID,
               LTRIM(RTRIM(ds.F_ID)) AS ID_FIELD,
               JSON_VALUE(item.value, '$.target') AS NAME_FIELD
        FROM dbo.FIELD_DATASOURCE ds
        CROSS APPLY OPENJSON(ds.RETURN_ITEMS) item
        WHERE JSON_VALUE(item.value, '$.target') LIKE N'%_NAME'
     ) rel
  ON rel.T_ID = fname.T_ID AND rel.NAME_FIELD = fname.F_ID
JOIN dbo.FIELDS fid
  ON fid.T_ID = rel.T_ID AND fid.F_ID = rel.ID_FIELD
WHERE fname.T_ID LIKE N'%_M'
  AND RIGHT(fname.F_ID, 5) = N'_NAME'
  AND COALESCE(fname.IS_VIRTUAL, 0) = 1
  AND (fname.FORM_CELL_GROUP IS NULL OR LTRIM(RTRIM(fname.FORM_CELL_GROUP)) = N'')
  AND fid.FORM_CELL_GROUP IS NOT NULL
  AND LTRIM(RTRIM(fid.FORM_CELL_GROUP)) <> N''
  AND fid.F_ID <> fname.F_ID;

DECLARE @BACKFILL_COUNT INT = @@ROWCOUNT;
PRINT N'[033] 回填映射名称字段分组补齐完成：' + CAST(@BACKFILL_COUNT AS NVARCHAR(10)) + N' 个。';
