-- ============================================================================
-- EOS.ERP migration 350: 230104 更名「行为动作、校验规则」
-- ----------------------------------------------------------------------------
-- 命名方式与同族先例一致：2302「数据表、字段维护」——**同类多项用顿号并列**，
-- 不用"+"（加号更像算式，也容易被读成"行为动作 加 校验规则"这种从属关系，
-- 而它们是同一条执行链上的两件事：先拦、后做）。
--
-- 只改显示名，不动形态 / 承载页 / 数据源锚点 / 授权行；路由与权限常量仍按 230104。
--   ① 更新 M_DESC；
--   ② 收口断言：名称正确、承载页与形态未变、顿号命名方式与 2302 一致（顺序无关）。
-- 幂等：按当前值判断，可重复执行。
-- ============================================================================

SET NOCOUNT ON;
SET XACT_ABORT ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET ARITHABORT ON;

IF DB_NAME() <> N'EOS.ERP'
    THROW 51930, N'本脚本只能在 EOS.ERP 数据库内执行。', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.MODULES WHERE M_IDX = 230104)
    THROW 51931, N'模块 230104 不存在：本迁移是 349 的后续，迁移中止。', 1;

BEGIN TRANSACTION;

UPDATE dbo.MODULES
   SET M_DESC = N'行为动作、校验规则', LAST_UPDATE_BY = N'350', LAST_UPDATE_DATE = GETDATE()
 WHERE M_IDX = 230104
   AND M_DESC <> N'行为动作、校验规则';
PRINT N'== 230104 已更名「行为动作、校验规则」 ' + CONVERT(NVARCHAR(10), @@ROWCOUNT) + N' 行 ==';

IF NOT EXISTS (SELECT 1 FROM dbo.MODULES
                WHERE M_IDX = 230104
                  AND M_DESC = N'行为动作、校验规则'
                  AND M_URL = N'/admin/module-actions'
                  AND MASTER_TABLE IS NULL)
    THROW 51932, N'230104 的名称、承载页或数据源锚点不符，迁移中止。', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.V_MODULE_NODE WHERE M_IDX = 230104 AND NODE_KIND = N'CUSTOMPAGE')
    THROW 51933, N'230104 的形态不是自定义承载页，迁移中止。', 1;

/* 命名方式对齐 2302「数据表、字段维护」：同族两项都用顿号并列。 */
IF (SELECT M_DESC FROM dbo.MODULES WHERE M_IDX = 2302) NOT LIKE N'%、%'
    THROW 51934, N'参照模块 2302 不再是顿号并列的命名方式，请先确认命名规范再执行本迁移。', 1;

COMMIT TRANSACTION;

DECLARE @name104 NVARCHAR(200) = (SELECT M_DESC FROM dbo.MODULES WHERE M_IDX = 230104);
DECLARE @name2302 NVARCHAR(200) = (SELECT M_DESC FROM dbo.MODULES WHERE M_IDX = 2302);
PRINT N'== 收口：230104 = ' + ISNULL(@name104, N'(缺失)')
    + N'（参照 2302 = ' + ISNULL(@name2302, N'(缺失)') + N'） ==';
