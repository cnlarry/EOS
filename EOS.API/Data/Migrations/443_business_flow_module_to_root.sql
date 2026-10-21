-- ============================================================================
-- EOS.ERP migration 443: 业务流程图 2314 提升到 23 系统管理根下
-- ----------------------------------------------------------------------------
--  模块 2314「业务流程图」（/admin/business-flow）原挂在 2311 数据表维护下（迁移 310）。
--  它的读图对象已从「表与表的字段引用」改为「单据与单据的走向、模块与模块的关系」，
--  因此不再属于"数据表维护"这一类运维页，改为 23 系统管理的直接子项：
--  SORT_IDX 取 35，紧接 2311（30）之后、2304（40）之前。
--
--  只改菜单层级，不动 URL、不动 MAIN_TABLE 的空值、不动任何授权行：
--  权限面仍是模块 2314 的 CanBrowse（迁移 310 按 2302 镜像），读者集合不变。
--
--  幂等：已就位则跳过 UPDATE，可重复执行。
-- ============================================================================
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET ARITHABORT ON;

DECLARE @GuardMessage NVARCHAR(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 51900, @GuardMessage, 1;

IF NOT EXISTS (SELECT 1 FROM dbo.MODULES WHERE M_IDX = 23)
    THROW 51901, N'根模块 23 系统管理不存在，迁移中止（业务流程图要挂到它下面）。', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.MODULES WHERE M_IDX = 2314 AND M_URL = N'/admin/business-flow')
    THROW 51902, N'模块 2314 未登记或 URL 不是 /admin/business-flow，迁移中止（编号不回收、不复用）。', 1;

BEGIN TRANSACTION;

UPDATE dbo.MODULES
SET M_P_IDX = 23,
    M_ROOT_IDX = 23,
    SORT_IDX = 35
WHERE M_IDX = 2314
  AND (M_P_IDX <> 23 OR M_ROOT_IDX <> 23 OR SORT_IDX <> 35);

DECLARE @Moved INT = @@ROWCOUNT;
PRINT N'== 业务流程图 2314 改挂 23 根下（M_P_IDX=23 / SORT_IDX=35），本次变更 '
    + CONVERT(NVARCHAR(10), @Moved) + N' 行 ==';

/* ---------- 收口断言 ---------- */
IF NOT EXISTS (
        SELECT 1 FROM dbo.MODULES
        WHERE M_IDX = 2314
          AND M_URL = N'/admin/business-flow'
          AND M_P_IDX = 23
          AND M_ROOT_IDX = 23
          AND SORT_IDX = 35
          AND ISNULL(M_TAG, 1) = 1
          AND MASTER_TABLE IS NULL)
    THROW 51903, N'模块 2314 未落到 23 根下或关键列被改动，迁移中止。', 1;

IF EXISTS (SELECT 1 FROM dbo.MODULES WHERE M_IDX = 2314 AND M_DESC <> N'业务流程图')
    THROW 51904, N'模块 2314 的显示名被改动，迁移中止：本迁移只调整层级。', 1;

COMMIT TRANSACTION;

PRINT N'== 收口：业务流程图 2314 现为 23 系统管理 › 业务流程图 ==';
