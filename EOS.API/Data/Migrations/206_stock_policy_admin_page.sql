-- ============================================================================
-- EOS.ERP migration 207: 库存策略（110310）改由专属管理页承载
-- ----------------------------------------------------------------------------
-- 背景：110310 原先登记为**工作台模块**（M_URL=/workbench）但从未发布快照，也没有界面，
-- 所以这个模块在菜单里点进去是空的。策略的读写只能走官方端点
-- （/api/v1/admin/depot-stock-policy，含组合规则、破坏性下调的二次确认与归并/归位、审计），
-- 而通用工作台表单会把写入直接打到策略表、绕过这些把关 —— 这正是此前登记并已封死的那颗雷。
--
-- 处置：把 M_URL 指向专属管理页 /admin/depot-stock-policy（与 2303 字段审计、2305/2306
-- 用户与权限同一形态的定制页：前端路由 + ModuleRouteValidator 精确路径 + 模块 110310 权限门），
-- 使得**读**能看到、**写**仍然只能经过策略服务。
--
-- 影响面：模块可见性、权限行、报批核能力一律不动；NEW_URL/MODI_URL 本就为空（保持），
-- 因此统一表单的写入端点依旧对 110310 返回 404，雷不会复燃。
--
-- 幂等：按 M_IDX 更新，重复执行结果一致。
-- 回滚：UPDATE dbo.MODULES SET M_URL = N'/workbench' WHERE M_IDX = 110310;
-- ============================================================================

SET NOCOUNT ON;

SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET ARITHABORT ON;

DECLARE @GUARD_MESSAGE NVARCHAR(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 52760, @GUARD_MESSAGE, 1;

/* 前提守卫：目标模块存在、是策略表承载页、且没有发布过快照
   （有快照说明它已经被当成工作台模块发布过，改 URL 前必须先按发布流程处理）。 */
IF NOT EXISTS (SELECT 1 FROM dbo.MODULES WHERE M_IDX = 110310 AND MASTER_TABLE = N'DEPOT_STOCK_POLICY')
    THROW 52761, N'模块 110310 不存在或主表不是 DEPOT_STOCK_POLICY，迁移中止。', 1;
IF EXISTS (SELECT 1 FROM dbo.WORKBENCH_DEFINITION_SNAPSHOT WHERE MODULE_ID = 110310)
    THROW 52762, N'模块 110310 已有发布快照（已被当作工作台模块发布），迁移中止。', 1;

UPDATE dbo.MODULES
   SET M_URL = N'/admin/depot-stock-policy',
       NEW_URL = NULL,
       MODI_URL = NULL,
       LAST_UPDATE_BY = N'DbUp',
       LAST_UPDATE_DATE = SYSDATETIME()
 WHERE M_IDX = 110310;

/* 收口断言：URL 必须指向专属管理页，且不得留下任何统一表单入口
   （留下 NEW_URL/MODI_URL 会让通用工作台重新具备写能力，绕过策略服务）。 */
IF EXISTS (SELECT 1 FROM dbo.MODULES
           WHERE M_IDX = 110310
             AND (LTRIM(RTRIM(ISNULL(M_URL, N''))) <> N'/admin/depot-stock-policy'
                  OR NULLIF(LTRIM(RTRIM(ISNULL(NEW_URL, N''))), N'') IS NOT NULL
                  OR NULLIF(LTRIM(RTRIM(ISNULL(MODI_URL, N''))), N'') IS NOT NULL))
    THROW 52763, N'模块 110310 的 URL 收口未达预期，迁移中止。', 1;

PRINT N'== 110310 库存策略已改由专属管理页 /admin/depot-stock-policy 承载 ==';
