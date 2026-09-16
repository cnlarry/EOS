-- ============================================================================
-- EOS.ERP migration 090: 删除 COMPANY.CI 冗余列（流水式遗留坑）
-- ----------------------------------------------------------------------------
-- 考证结论（2026-09-16）：COMPANY 的公司主键是 COMPANY_ID（nchar(20)，2 行：
-- DEMO/默认）；CI（nchar(16)，值 ''/'Default'）无任何消费端——无 FK、无库内
-- 对象依赖（sys.sql_expression_dependencies 零引用）、新代码零引用、旧 MagCompany
-- 页无该控件、4 个 COMPANY 数据源的 STRUCT/RETURN 只用 COMPANY_ID/NAME_CN 等列。
-- 公司数据源唯一真源即 COMPANY_ID（见 089）。
-- 本迁移：引用熔断（若出现新的 CI 引用即中止）→ 清 FIELDS 元数据行及 7 张引用表
-- （DELETE 语义对齐 FieldAdminRepository.DeleteAsync 级联）→ DROP 物理列。
-- 幂等：列不存在即跳过。可逆：加列需重建元数据（不建议，见上）。
-- 注意：SYSDD/SYSDH/SYSDF 的 CI 列不动（另一语义域的权限标记，见 089 头注）。
-- ============================================================================

SET NOCOUNT ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;

DECLARE @GUARD_MESSAGE NVARCHAR(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, @GUARD_MESSAGE, 1;

PRINT N'== 删除 COMPANY.CI 开始 ==';

-- 引用熔断：COMPANY 数据源若新增对 CI 列的引用，中止执行
IF EXISTS (SELECT 1 FROM dbo.FIELD_DATASOURCE WITH (NOLOCK)
           WHERE SOURCE_T_ID = N'COMPANY'
             AND (FILTER_STRUCT LIKE N'%"CI"%' OR RETURN_ITEMS LIKE N'%"CI"%'))
    THROW 50001, N'COMPANY 数据源出现对 CI 列的新引用，中止删除。', 1;
IF EXISTS (SELECT 1 FROM dbo.SYSQR_DEFAULT WITH (NOLOCK)
           WHERE F_ID = N'COMPANY.CI')
    THROW 50002, N'报表条件出现对 COMPANY.CI 的新引用，中止删除。', 1;

-- 元数据级联清理（对齐字段删除语义）
DELETE FROM dbo.SYSQL_FIELDS WHERE F_ID = N'CI' AND (T_ID = N'COMPANY' OR T_ID_R = N'COMPANY');
DELETE FROM dbo.SYSQL_DEFAULT WHERE F_ID = N'CI' AND (T_ID = N'COMPANY' OR T_ID_R = N'COMPANY');
DELETE FROM dbo.SYSQL_CONDITION WHERE F_ID = N'CI' AND (T_ID = N'COMPANY' OR T_ID_R = N'COMPANY');
DELETE FROM dbo.SYSQL_COND_DFT WHERE F_ID = N'CI' AND (T_ID = N'COMPANY' OR T_ID_R = N'COMPANY');
DELETE FROM dbo.SYSQD_CONDITION WHERE F_ID = N'CI' AND (T_ID = N'COMPANY' OR T_ID_R = N'COMPANY');
DELETE FROM dbo.SYSQQ WHERE F_ID = N'COMPANY.CI';
DELETE FROM dbo.SYSQR_DEFAULT WHERE F_ID = N'COMPANY.CI';
DELETE FROM dbo.FIELDS WHERE T_ID = N'COMPANY' AND LTRIM(RTRIM(F_ID)) = N'CI';

IF EXISTS (SELECT 1 FROM sys.columns c
           JOIN sys.objects o ON c.object_id = o.object_id AND o.type = N'U'
           JOIN sys.schemas s ON o.schema_id = s.schema_id
           WHERE s.name = N'dbo' AND o.name = N'COMPANY' AND c.name = N'CI')
BEGIN
    -- 先清该列绑定的默认值/检查约束（含 089 可能已建的 DF_COMPANY_CI），否则 DROP 被 5074 拦。
    DECLARE @DropSql NVARCHAR(MAX) = N'';
    SELECT @DropSql += N'ALTER TABLE dbo.COMPANY DROP CONSTRAINT ' + QUOTENAME(d.name) + N';'
    FROM sys.default_constraints d
    JOIN sys.columns c ON d.parent_object_id = c.object_id AND d.parent_column_id = c.column_id
    JOIN sys.objects o ON c.object_id = o.object_id
    JOIN sys.schemas s ON o.schema_id = s.schema_id
    WHERE s.name = N'dbo' AND o.name = N'COMPANY' AND o.type = N'U' AND c.name = N'CI';
    SELECT @DropSql += N'ALTER TABLE dbo.COMPANY DROP CONSTRAINT ' + QUOTENAME(k.name) + N';'
    FROM sys.check_constraints k
    JOIN sys.columns c ON k.parent_object_id = c.object_id AND k.parent_column_id = c.column_id
    JOIN sys.objects o ON c.object_id = o.object_id
    JOIN sys.schemas s ON o.schema_id = s.schema_id
    WHERE s.name = N'dbo' AND o.name = N'COMPANY' AND o.type = N'U' AND c.name = N'CI';
    IF @DropSql <> N'' EXEC sp_executesql @DropSql;
    ALTER TABLE dbo.COMPANY DROP COLUMN CI;
    PRINT N'  已删除 dbo.COMPANY.CI（含 FIELDS 元数据行）。';
END
ELSE
BEGIN
    PRINT N'  dbo.COMPANY.CI 已不存在，跳过。';
END

PRINT N'== 删除 COMPANY.CI 完成 ==';
