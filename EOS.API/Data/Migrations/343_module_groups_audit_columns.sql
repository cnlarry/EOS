-- ============================================================================
-- EOS.ERP migration 343: MODULE_GROUPS 的建立人 / 建立日期收敛到全库约定
-- ----------------------------------------------------------------------------
-- 341 建 MODULE_GROUPS 时把 CREATE_PERSON / CREATE_DATE 建成了可空且无默认值。这两列名
-- 属全库的"建立组"约定（同口径见 MODULE_BUSINESS_ACTION：CREATE_PERSON nvarchar(80)
-- NOT NULL DEFAULT('')、CREATE_DATE NOT NULL DEFAULT(sysdatetime())），由
-- `scripts/check-lifecycle-columns.ps1` 按 dbo 用户表逐列核对：**必须 NOT NULL 且有 DEFAULT**。
--
-- 为什么这不是形式主义：这两列在写入路径上由 DEFAULT 兜底，读路径（审计展示、按建立人筛选）
-- 依赖它们非空；建成可空又无默认值时，任何漏填的插入都会留下一行"不知道谁建的"。
-- 门禁先报了 MODULE_GROUPS — 说明它是有效的（这条 341 的疏漏被它抓出来了）。
--
-- 处置：先补 NULL，再收紧为 NOT NULL，最后补 DEFAULT 约束。三步都按当前结构判断，可重复执行。
-- 类型保持 341 建表时的取值（CREATE_PERSON nchar(20)、CREATE_DATE datetime），只改可空性与默认值：
-- 收紧不改类型，避免与既有行的隐式转换风险。
-- ============================================================================

SET NOCOUNT ON;
SET XACT_ABORT ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;

DECLARE @GuardMessage NVARCHAR(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 51890, @GuardMessage, 1;

IF OBJECT_ID(N'dbo.MODULE_GROUPS', N'U') IS NULL
    THROW 51891, N'dbo.MODULE_GROUPS 不存在（迁移 341 未执行？），迁移中止。', 1;

BEGIN TRANSACTION;

/* ---------- 1. 补 NULL（既有行里理论上不该有，兜底；列已 NOT NULL 时 UPDATE 为空操作） ---------- */
DECLARE @PersonNullable BIT = (SELECT is_nullable FROM sys.columns
                                WHERE object_id = OBJECT_ID(N'dbo.MODULE_GROUPS') AND name = N'CREATE_PERSON');
DECLARE @DateNullable BIT = (SELECT is_nullable FROM sys.columns
                              WHERE object_id = OBJECT_ID(N'dbo.MODULE_GROUPS') AND name = N'CREATE_DATE');

IF @PersonNullable = 1
BEGIN
    UPDATE dbo.MODULE_GROUPS SET CREATE_PERSON = N'' WHERE CREATE_PERSON IS NULL;
    PRINT N'== 已补 CREATE_PERSON 空值 ' + CONVERT(NVARCHAR(10), @@ROWCOUNT) + N' 行 ==';
END

IF @DateNullable = 1
BEGIN
    UPDATE dbo.MODULE_GROUPS SET CREATE_DATE = SYSDATETIME() WHERE CREATE_DATE IS NULL;
    PRINT N'== 已补 CREATE_DATE 空值 ' + CONVERT(NVARCHAR(10), @@ROWCOUNT) + N' 行 ==';
END

/* ---------- 2. 收紧为 NOT NULL ---------- */
IF @PersonNullable = 1
    ALTER TABLE dbo.MODULE_GROUPS ALTER COLUMN CREATE_PERSON NCHAR(20) NOT NULL;

IF @DateNullable = 1
    ALTER TABLE dbo.MODULE_GROUPS ALTER COLUMN CREATE_DATE DATETIME NOT NULL;

/* ---------- 3. 补默认值约束（漏填时由库兜底） ---------- */
IF NOT EXISTS (SELECT 1 FROM sys.default_constraints
                WHERE parent_object_id = OBJECT_ID(N'dbo.MODULE_GROUPS') AND name = N'DF_MODULE_GROUPS_CREATE_PERSON')
    ALTER TABLE dbo.MODULE_GROUPS ADD CONSTRAINT DF_MODULE_GROUPS_CREATE_PERSON DEFAULT (N'') FOR CREATE_PERSON;

IF NOT EXISTS (SELECT 1 FROM sys.default_constraints
                WHERE parent_object_id = OBJECT_ID(N'dbo.MODULE_GROUPS') AND name = N'DF_MODULE_GROUPS_CREATE_DATE')
    ALTER TABLE dbo.MODULE_GROUPS ADD CONSTRAINT DF_MODULE_GROUPS_CREATE_DATE DEFAULT (SYSDATETIME()) FOR CREATE_DATE;

/* ---------- 4. 收口断言：建立组两列都必须是 NOT NULL 且有 DEFAULT ---------- */
IF EXISTS (SELECT 1 FROM sys.columns c
            LEFT JOIN sys.default_constraints dc ON dc.parent_object_id = c.object_id AND dc.parent_column_id = c.column_id
            WHERE c.object_id = OBJECT_ID(N'dbo.MODULE_GROUPS') AND c.name = N'CREATE_PERSON'
              AND (c.is_nullable = 1 OR dc.object_id IS NULL))
    THROW 51892, N'MODULE_GROUPS.CREATE_PERSON 未收敛为 NOT NULL + DEFAULT，迁移中止。', 1;

IF EXISTS (SELECT 1 FROM sys.columns c
            LEFT JOIN sys.default_constraints dc ON dc.parent_object_id = c.object_id AND dc.parent_column_id = c.column_id
            WHERE c.object_id = OBJECT_ID(N'dbo.MODULE_GROUPS') AND c.name = N'CREATE_DATE'
              AND (c.is_nullable = 1 OR dc.object_id IS NULL))
    THROW 51893, N'MODULE_GROUPS.CREATE_DATE 未收敛为 NOT NULL + DEFAULT，迁移中止。', 1;

COMMIT TRANSACTION;

PRINT N'== 收口：MODULE_GROUPS 建立组两列已 NOT NULL + DEFAULT（与 MODULE_BUSINESS_ACTION 同口径）==';
