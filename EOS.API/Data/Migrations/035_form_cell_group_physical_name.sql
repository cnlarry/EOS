-- 统一表单组合字段分组补齐（四）：物理 _NAME 名称字段（2026-08-31）
--
-- 背景：032 只处理「虚拟 _NAME」名称字段（IS_VIRTUAL=1）。但部分主表的名称字段
--       是冗余物理列（旧系统把名称冗余落库，如 COP_QUOTE_M.CLIENT_NAME），
--       它们同样要与编号字段同格组合，却被 032 的虚拟字段门槛漏掉，导致
--       「编号+名称」组合错开（1404 客户编号/客户名分开显示）。
-- 规则：为主表（T_ID 以 _M 结尾）「物理 _NAME 名称字段」（IS_VIRTUAL=0）补分组：
--       组名取核心名匹配的编号字段（_ID/_NO 去后缀）的现有 FORM_CELL_GROUP，
--       ROLE=2（从字段）。编号字段本身不重复补（032 已覆盖）。
-- 幂等：仅更新「未配置分组」的物理名称字段。
-- 编号：035（034 已由手工核对边界字段占用）。

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
