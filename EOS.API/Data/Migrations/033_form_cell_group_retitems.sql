-- 统一表单组合字段分组补齐（二）：按选择器回填映射补齐核心名不匹配的名称字段（2026-08-31）
--
-- 背景：032 按「字段名核心名匹配」（编号字段去 _ID/_NO 后缀 = 名称字段去 _NAME 后缀）
--       补齐了绝大部分「编号+名称」组合；但仍有名称字段与编号字段字段名不匹配，
--       却通过选择器回填（FIELD_DATASOURCE.RETURN_ITEMS）带出，例如：
--         - 类别字段（*_TYPE）→ BILL_NAME（单别名称），如 REPAIR_TYPE→BILL_NAME；
--         - 产品编号（PRO_NO）→ COLOR_NAME（颜色名，选产品连带回填）；
--       这些名称字段仍会因主表读取 includeVirtual=false 且无 FORM_CELL_GROUP 被过滤。
-- 规则：以 FIELD_DATASOURCE.RETURN_ITEMS 为权威关联——编号字段回填的 target 字段中，
--       凡虚拟 _NAME 字段且未分组，归入该编号字段所在组（ROLE=2）；编号字段自身未分组
--       则先补分组（组名 = 去 _ID/_NO 后缀的核心名）+ ROLE=1。
-- 与 032 顺序执行：032 已配组的字段此处因「无分组」条件不满足而跳过，不重复覆盖。
-- 幂等：仅更新「未配置分组」的字段。
-- 编号：033（032 已由组合字段核心名补齐占用）。

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
