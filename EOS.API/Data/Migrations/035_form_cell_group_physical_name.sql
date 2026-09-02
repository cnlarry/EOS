-- Unified form cell grouping backfill (part 4): physical _NAME columns.
--
-- Context: part 1 only handled virtual _NAME fields (IS_VIRTUAL=1). Some master tables
-- store the name redundantly as a physical column (e.g. COP_QUOTE_M.CLIENT_NAME); those
-- need the same "code + name" grouping but were missed by part 1's virtual-field gate,
-- leaving the combo split across cells (client code and name shown separately).
-- Rule: group master-table (T_ID ending _M) physical _NAME fields (IS_VIRTUAL=0):
-- group name = the FORM_CELL_GROUP of the core-name-matching code field, ROLE=2.
-- Code fields are not re-grouped here.
-- Idempotent: only ungrouped physical name fields are updated.

SET NOCOUNT ON;

DECLARE @GUARD_MESSAGE NVARCHAR(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, @GUARD_MESSAGE, 1;

-- 名称字段核心名 = 去 _NAME 后缀；编号字段核心名 = 去 _ID/_NO 后缀；
-- 两者相等即判定为一组，组名沿用编号字段已有分组（如 CLIENT_ID→CLIENT）。
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
  AND COALESCE(fname.IS_VIRTUAL, 0) = 0
  AND (fname.FORM_CELL_GROUP IS NULL OR LTRIM(RTRIM(fname.FORM_CELL_GROUP)) = N'')
  AND fid.FORM_CELL_GROUP IS NOT NULL
  AND LTRIM(RTRIM(fid.FORM_CELL_GROUP)) <> N''
  AND fid.F_ID <> fname.F_ID;

DECLARE @BACKFILL_COUNT INT = @@ROWCOUNT;
PRINT N'[035] 物理名称字段分组补齐完成：' + CAST(@BACKFILL_COUNT AS NVARCHAR(10)) + N' 个。';
