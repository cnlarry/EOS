-- Unified form cell grouping backfill.
--
-- Context: the unified form "code + name" combination cell (e.g. client = client code +
-- client name) depends on FIELDS.FORM_CELL_GROUP / FORM_CELL_ROLE. This mechanism was
-- introduced by the new system and only sporadically filled in by hand, so most
-- "code + name" combos were ungrouped: name fields are virtual (no physical column;
-- value is brought back by the code field's chooser), but master-table reads use
-- includeVirtual=false, so virtual fields without FORM_CELL_GROUP were filtered out
-- and the master grid showed only the code.
-- Rule: cover only master tables (T_ID ending in _M). The code field's core name =
-- strip _ID/_NO suffix; the name field's core name = strip _NAME suffix; equal core
-- names are grouped, group name = code field core name.
--   1) Code field (has chooser, matching virtual _NAME exists, not yet grouped) → group + ROLE=1;
--   2) Name field (virtual _NAME, ungrouped, matching code field already grouped) → same group + ROLE=2.
-- Detail tables (_D) are not backfilled: detail reads use includeVirtual=true.
-- Idempotent: only ungrouped fields are updated.

SET NOCOUNT ON;

DECLARE @GUARD_MESSAGE NVARCHAR(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, @GUARD_MESSAGE, 1;

-- 编号字段核心名（去 _ID/_NO 后缀；其余保留）
-- 名称字段核心名（去 _NAME 后缀）

-- 步骤 1：为主表「编号字段」补分组（对应虚拟 _NAME 存在、自身未分组；不要求编号字段有选择器）
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
        SELECT 1 FROM dbo.FIELDS fname
        WHERE fname.T_ID = fid.T_ID
          AND RIGHT(fname.F_ID, 5) = N'_NAME'
          AND COALESCE(fname.IS_VIRTUAL, 0) = 1
          AND (fname.FORM_CELL_GROUP IS NULL OR LTRIM(RTRIM(fname.FORM_CELL_GROUP)) = N'')
          AND LEFT(fname.F_ID, LEN(fname.F_ID) - 5)
              = CASE WHEN RIGHT(fid.F_ID, 3) IN (N'_ID', N'_NO')
                     THEN LEFT(fid.F_ID, LEN(fid.F_ID) - 3)
                     ELSE fid.F_ID END
      );

-- 步骤 2：为主表「名称字段」补分组（虚拟 _NAME、未分组、核心名匹配的编号字段已有分组）
UPDATE fname
SET fname.FORM_CELL_GROUP   = fid.FORM_CELL_GROUP,
    fname.FORM_CELL_ROLE    = 2,
    fname.LAST_UPDATE_BY    = N'MIGRATION',
    fname.LAST_UPDATE_DATE  = GETDATE()
FROM dbo.FIELDS fname
JOIN dbo.FIELDS fid
  ON fid.T_ID = fname.T_ID
  AND LEFT(fname.F_ID, LEN(fname.F_ID) - 5)
      = CASE WHEN RIGHT(fid.F_ID, 3) IN (N'_ID', N'_NO')
             THEN LEFT(fid.F_ID, LEN(fid.F_ID) - 3)
             ELSE fid.F_ID END
WHERE fname.T_ID LIKE N'%_M'
  AND RIGHT(fname.F_ID, 5) = N'_NAME'
  AND COALESCE(fname.IS_VIRTUAL, 0) = 1
  AND (fname.FORM_CELL_GROUP IS NULL OR LTRIM(RTRIM(fname.FORM_CELL_GROUP)) = N'')
  AND fid.FORM_CELL_GROUP IS NOT NULL
  AND LTRIM(RTRIM(fid.FORM_CELL_GROUP)) <> N''
  AND fid.F_ID <> fname.F_ID;

DECLARE @BACKFILL_COUNT INT = @@ROWCOUNT;
PRINT N'[032] 组合字段名称字段分组补齐完成：' + CAST(@BACKFILL_COUNT AS NVARCHAR(10)) + N' 个。';
