-- ============================================================================
-- EOS.ERP migration 096: 删除 DEPT/SYSDN.COMPANY_ID（公司列统一为 CI）
-- ----------------------------------------------------------------------------
-- DEPT/SYSDN 同时存在 CI 与 COMPANY_ID 两列，两者都指向 COMPANY.COMPANY_ID；
-- 全库业务表的公司列统一用 CI 命名。本迁移保留 CI、删除 COMPANY_ID，并把指向该列
-- 的受控元数据整体改挂到 CI，避免删列后留下悬空引用：
--   · TABLES.QUERY_RELATION：DEPT/SYSDN 关联 COMPANY 的 ON 条件改用 CI
--     （COMPANY_NAME/COMPANY_NAME_CN 虚拟字段依赖此 JOIN，不改则字段不渲染）；
--   · FIELD_DATASOURCE：两表的公司选择器改挂 CI，RETURN_ITEMS 回填目标同步改 CI；
--   · SYSQL_FIELDS/SYSQL_DEFAULT：用户已存列与模块默认列改指 CI（已存在 CI 行则删除旧行，
--     避免同一列出现重复）；
--   · FIELDS：删除 COMPANY_ID 元数据行（物理列随之消失）。
-- 数据：CI 为 NOT NULL 且有默认值，仅当 CI 为空时用 COMPANY_ID 回填（当前无空值）；
-- 与 CI 不一致的 COMPANY_ID 值按"保留 CI"口径丢弃，不回写 CI。
-- 前置盘点（执行前已核对）：两列无索引/外键/默认值/检查约束，无触发器，
-- 无视图·函数·存储过程引用，MODULES.FILTER 与 TABLES.DF_CONDITION/DF_VERIFY 无引用。
-- 幂等：元数据更新带条件、列不存在即跳过；回填走动态 SQL，删列后重复执行也不会编译失败。
-- 可逆：需重建列与元数据（不建议）。
-- ============================================================================

SET NOCOUNT ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;

DECLARE @GUARD_MESSAGE NVARCHAR(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, @GUARD_MESSAGE, 1;

PRINT N'== 删除 DEPT/SYSDN.COMPANY_ID 开始 ==';

-- 引用熔断：库内对象若新增对这两列的引用，中止执行
IF EXISTS (SELECT 1 FROM sys.sql_modules m
           WHERE CHARINDEX(N'DEPT.COMPANY_ID', m.definition) > 0
              OR CHARINDEX(N'SYSDN.COMPANY_ID', m.definition) > 0)
    THROW 50001, N'库内对象出现对 DEPT/SYSDN.COMPANY_ID 的引用，中止删除。', 1;

-- 1) 数据保全：CI 为空时以 COMPANY_ID 回填（CI 为 NOT NULL，正常为空操作）
-- 动态执行：列一旦删除，静态引用会让整批无法编译（重复执行本脚本时会踩到）
DECLARE @BackfillRows INT = 0;
DECLARE @BackfillSql NVARCHAR(MAX) = N'
UPDATE dbo.@T SET CI = LTRIM(RTRIM(COMPANY_ID))
WHERE NULLIF(LTRIM(RTRIM(CI)), N'''') IS NULL AND NULLIF(LTRIM(RTRIM(COMPANY_ID)), N'''') IS NOT NULL;
SET @n = @@ROWCOUNT;';
IF EXISTS (SELECT 1 FROM sys.columns c JOIN sys.objects o ON c.object_id = o.object_id
           JOIN sys.schemas s ON o.schema_id = s.schema_id
           WHERE s.name = N'dbo' AND o.type = N'U' AND o.name = N'DEPT' AND c.name = N'COMPANY_ID')
BEGIN
    DECLARE @DeptRows INT = 0;
    DECLARE @DeptSql NVARCHAR(MAX) = REPLACE(@BackfillSql, N'@T', N'DEPT');
    EXEC sp_executesql @DeptSql, N'@n INT OUTPUT', @n = @DeptRows OUTPUT;
    SET @BackfillRows += @DeptRows;
END
IF EXISTS (SELECT 1 FROM sys.columns c JOIN sys.objects o ON c.object_id = o.object_id
           JOIN sys.schemas s ON o.schema_id = s.schema_id
           WHERE s.name = N'dbo' AND o.type = N'U' AND o.name = N'SYSDN' AND c.name = N'COMPANY_ID')
BEGIN
    DECLARE @SysdnRows INT = 0;
    DECLARE @SysdnSql NVARCHAR(MAX) = REPLACE(@BackfillSql, N'@T', N'SYSDN');
    EXEC sp_executesql @SysdnSql, N'@n INT OUTPUT', @n = @SysdnRows OUTPUT;
    SET @BackfillRows += @SysdnRows;
END
SELECT CONCAT(N'  CI 空值回填行数=', @BackfillRows) AS SUMMARY;

-- 2) 关联关系改挂 CI（虚拟字段 COMPANY_NAME/COMPANY_NAME_CN 依赖此 JOIN）
UPDATE dbo.TABLES
SET QUERY_RELATION = REPLACE(QUERY_RELATION, N'DEPT.COMPANY_ID=COMPANY.COMPANY_ID', N'DEPT.CI=COMPANY.COMPANY_ID')
WHERE LTRIM(RTRIM(T_ID)) = N'DEPT'
  AND CHARINDEX(N'DEPT.COMPANY_ID=COMPANY.COMPANY_ID', QUERY_RELATION) > 0;
UPDATE dbo.TABLES
SET QUERY_RELATION = REPLACE(QUERY_RELATION, N'SYSDN.COMPANY_ID=COMPANY.COMPANY_ID', N'SYSDN.CI=COMPANY.COMPANY_ID')
WHERE LTRIM(RTRIM(T_ID)) = N'SYSDN'
  AND CHARINDEX(N'SYSDN.COMPANY_ID=COMPANY.COMPANY_ID', QUERY_RELATION) > 0;
PRINT N'  TABLES.QUERY_RELATION 已改为按 CI 关联 COMPANY。';

-- 3) 公司选择器改挂 CI（RETURN_ITEMS 的回填目标同步改 CI；历史大小写两种写法都覆盖）
UPDATE dbo.FIELD_DATASOURCE
SET F_ID = N'CI',
    RETURN_ITEMS = REPLACE(REPLACE(RETURN_ITEMS,
        N'"target":"COMPANY_ID"', N'"target":"CI"'),
        N'"Target":"COMPANY_ID"', N'"Target":"CI"')
WHERE LTRIM(RTRIM(T_ID)) IN (N'DEPT', N'SYSDN')
  AND LTRIM(RTRIM(F_ID)) = N'COMPANY_ID';
PRINT N'  FIELD_DATASOURCE 公司选择器已改挂 CI。';

-- 4) 已存列/默认列改指 CI；目标已存在 CI 行时删除旧行避免重复
UPDATE f SET F_ID = N'CI'
FROM dbo.SYSQL_FIELDS f
WHERE LTRIM(RTRIM(f.F_ID)) = N'COMPANY_ID'
  AND (LTRIM(RTRIM(f.T_ID)) IN (N'DEPT', N'SYSDN') OR LTRIM(RTRIM(f.T_ID_R)) IN (N'DEPT', N'SYSDN'))
  AND NOT EXISTS (SELECT 1 FROM dbo.SYSQL_FIELDS x
                  WHERE x.STEP_ID = f.STEP_ID AND x.USER_ID = f.USER_ID
                    AND LTRIM(RTRIM(x.F_ID)) = N'CI');
DELETE FROM dbo.SYSQL_FIELDS
WHERE LTRIM(RTRIM(F_ID)) = N'COMPANY_ID'
  AND (LTRIM(RTRIM(T_ID)) IN (N'DEPT', N'SYSDN') OR LTRIM(RTRIM(T_ID_R)) IN (N'DEPT', N'SYSDN'));

UPDATE d SET F_ID = N'CI'
FROM dbo.SYSQL_DEFAULT d
WHERE LTRIM(RTRIM(d.F_ID)) = N'COMPANY_ID'
  AND (LTRIM(RTRIM(d.T_ID)) IN (N'DEPT', N'SYSDN') OR LTRIM(RTRIM(d.T_ID_R)) IN (N'DEPT', N'SYSDN'))
  AND NOT EXISTS (SELECT 1 FROM dbo.SYSQL_DEFAULT x
                  WHERE x.T_ID = d.T_ID AND x.T_ID_R = d.T_ID_R
                    AND LTRIM(RTRIM(x.F_ID)) = N'CI');
DELETE FROM dbo.SYSQL_DEFAULT
WHERE LTRIM(RTRIM(F_ID)) = N'COMPANY_ID'
  AND (LTRIM(RTRIM(T_ID)) IN (N'DEPT', N'SYSDN') OR LTRIM(RTRIM(T_ID_R)) IN (N'DEPT', N'SYSDN'));
PRINT N'  SYSQL_FIELDS/SYSQL_DEFAULT 引用已改指 CI。';

-- 5) 字段元数据：删除随列消失的 COMPANY_ID 行
DELETE FROM dbo.FIELDS
WHERE LTRIM(RTRIM(T_ID)) IN (N'DEPT', N'SYSDN')
  AND LTRIM(RTRIM(F_ID)) = N'COMPANY_ID';
PRINT N'  FIELDS 元数据行已清理。';

-- 6) 删列：先清该列上的默认/检查约束（盘点无依赖，此处为防御），再 DROP
DECLARE @DropSql NVARCHAR(MAX) = N'';
SELECT @DropSql += N'ALTER TABLE dbo.' + QUOTENAME(o.name) + N' DROP CONSTRAINT ' + QUOTENAME(dc.name) + N';'
FROM sys.default_constraints dc
JOIN sys.columns c ON dc.parent_object_id = c.object_id AND dc.parent_column_id = c.column_id
JOIN sys.objects o ON c.object_id = o.object_id
JOIN sys.schemas s ON o.schema_id = s.schema_id
WHERE s.name = N'dbo' AND o.type = N'U' AND o.name IN (N'DEPT', N'SYSDN') AND c.name = N'COMPANY_ID';
SELECT @DropSql += N'ALTER TABLE dbo.' + QUOTENAME(o.name) + N' DROP CONSTRAINT ' + QUOTENAME(ck.name) + N';'
FROM sys.check_constraints ck
JOIN sys.columns c ON ck.parent_object_id = c.object_id AND ck.parent_column_id = c.column_id
JOIN sys.objects o ON c.object_id = o.object_id
JOIN sys.schemas s ON o.schema_id = s.schema_id
WHERE s.name = N'dbo' AND o.type = N'U' AND o.name IN (N'DEPT', N'SYSDN') AND c.name = N'COMPANY_ID';
IF @DropSql <> N'' EXEC sp_executesql @DropSql;

IF EXISTS (SELECT 1 FROM sys.columns c JOIN sys.objects o ON c.object_id = o.object_id
           JOIN sys.schemas s ON o.schema_id = s.schema_id
           WHERE s.name = N'dbo' AND o.type = N'U' AND o.name = N'DEPT' AND c.name = N'COMPANY_ID')
BEGIN
    ALTER TABLE dbo.DEPT DROP COLUMN COMPANY_ID;
    PRINT N'  已删除 dbo.DEPT.COMPANY_ID。';
END
ELSE PRINT N'  dbo.DEPT.COMPANY_ID 已不存在，跳过。';

IF EXISTS (SELECT 1 FROM sys.columns c JOIN sys.objects o ON c.object_id = o.object_id
           JOIN sys.schemas s ON o.schema_id = s.schema_id
           WHERE s.name = N'dbo' AND o.type = N'U' AND o.name = N'SYSDN' AND c.name = N'COMPANY_ID')
BEGIN
    ALTER TABLE dbo.SYSDN DROP COLUMN COMPANY_ID;
    PRINT N'  已删除 dbo.SYSDN.COMPANY_ID。';
END
ELSE PRINT N'  dbo.SYSDN.COMPANY_ID 已不存在，跳过。';

PRINT N'== 删除 DEPT/SYSDN.COMPANY_ID 完成 ==';
