-- 统一表单组合字段分组补齐（2026-08-31）
--
-- 背景：统一表单「编号+名称」同格组合（如 客户 = 客户编号 + 客户名）依赖
--       FIELDS.FORM_CELL_GROUP / FORM_CELL_ROLE 分组配置。旧系统（ERP/*.aspx）没有
--       该机制（页面硬编码布局），新系统引入后仅零散手工录入，导致绝大多数
--       「编号+名称」组合未配置分组：名称字段是虚拟字段（无物理列、靠编号字段
--       选择器回填带出），主表读取 includeVirtual=false，无物理列且无
--       FORM_CELL_GROUP 的虚拟字段被 WHERE 过滤，于是主表只显示编号。
-- 规则：仅覆盖主表（T_ID 以 _M 结尾）的「编号字段 + 虚拟 _NAME 名称字段」组合。
--       编号字段核心名 = 去 _ID/_NO 后缀（如 CLIENT_ID→CLIENT、PRO_NO→PRO），
--       名称字段核心名 = 去 _NAME 后缀（如 CLIENT_NAME→CLIENT、PRO_NAME→PRO）；
--       两者核心名相等即判定为一组，组名取编号字段核心名。
--       1) 编号字段（有选择器、对应虚拟 _NAME 存在、自身未分组）→ 补分组 + ROLE=1；
--       2) 名称字段（虚拟 _NAME、未分组、核心名匹配的编号字段已有分组）→ 补同组 + ROLE=2。
-- 明细表（_D）不回填：明细读取 includeVirtual=true，虚拟回填字段照常显示。
-- 幂等：仅更新「未配置分组」的字段，重复执行不重复覆盖。
-- 编号：032（031 已由报表表单布局 031_report_form_layout.sql 占用）。

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
