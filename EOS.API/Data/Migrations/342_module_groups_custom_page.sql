-- ============================================================================
-- EOS.ERP migration 342: 模块分组配置面改为定制承载页（模块 2315 的形态）
-- ----------------------------------------------------------------------------
-- 341 把 2315「模块分组」建成**统一工作台模块**（主表 MODULE_GROUPS、M_URL=/workbench），
-- 靠通用表单维护 MODULE_GROUPS 自己的行。本迁移把它改成**定制承载页** /admin/module-groups。
--
-- 为什么必须换形态（两条，任一条都足以否掉工作台形态）：
--   ① 页面要的是"选一个模块 → 维护**它的**分组"，通用表单只能维护 MODULE_GROUPS 自己那些行，
--      表达不了"为其它模块配置"这件事；
--   ② 分组配置是**保存即生效**的（表达式在组装工作台定义时实时读取、不进快照），
--      而工作台形态的保存要走模块定义快照与发布——把它留在工作台里，
--      "保存"这一步的意思与实际效果不一致。
--
-- 随之清理**形态外残留**（口径与迁移 340 一致）：
--   * MODULE_GROUPS 的字段登记（FIELDS 9 行）与字段数据来源（FIELD_DATASOURCE 1 行）——
--     它们是工作台表单的输入，定制页不消费；系统配置表不进字段目录（先例：MODULE_FORM_TAB）；
--   * 2315 上的工作台配置与脏标记 / 快照（新建的模块本就没有，这里按存在性清一遍，
--     让"从工作台切走的节点不留脏标记"这条纪律照常成立）。
--
-- 路由三处登记的另一半在代码里（同批提交）：`ModuleRouteValidator.ExactRoutes` 与前端
-- `workspaceRoutes.tsx` / `lazyRoutes.tsx` / `routeMeta.ts`；缺一处菜单会落占位页。
-- 2315 也要从统一表单**写名单**（`UnifiedFormEditor.EnabledModuleIds`）里去掉：定制页不走统一表单，
-- 名单是写路径的门（名单里的模块必须是工作台形态，见 scripts/check-module-node-kind.ps1）。
--
-- 幂等：全部按存在性判断，可重复执行。
-- ============================================================================

SET NOCOUNT ON;
SET XACT_ABORT ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET ARITHABORT ON;

DECLARE @GuardMessage NVARCHAR(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 51880, @GuardMessage, 1;

IF OBJECT_ID(N'dbo.MODULES', N'U') IS NULL
    THROW 51881, N'MODULES 表不存在，迁移中止。', 1;

IF OBJECT_ID(N'dbo.V_MODULE_NODE', N'V') IS NULL
    THROW 51882, N'视图 dbo.V_MODULE_NODE 不存在（迁移 340 未执行？），迁移中止。', 1;

/* 只改 341 建出来的那一个：2315 不存在说明 341 没跑，直接停，不要凭空补一个模块。 */
IF NOT EXISTS (SELECT 1 FROM dbo.MODULES WHERE M_IDX = 2315)
    THROW 51883, N'模块 2315 不存在：本迁移是 341 的后续（它只改 341 建出来的那一行），迁移中止。', 1;

BEGIN TRANSACTION;

/* ---------- 1. 承载页改为定制页，备注写清"为其它模块配置" ---------- */
UPDATE dbo.MODULES
   SET M_URL = N'/admin/module-groups',
       REMARK = N'模块分组的配置面（定制页 /admin/module-groups）：为其它模块维护列表分组（名称 + 表达式 + 顺序）。保存即生效——分组表达式在组装工作台定义时实时读取，不进快照、不走发布。',
       LAST_UPDATE_BY = N'migration-342',
       LAST_UPDATE_DATE = SYSDATETIME()
 WHERE M_IDX = 2315;

PRINT N'== 模块 2315 的承载页已改为 /admin/module-groups ==';

/* ---------- 2. 形态外的工作台配置归一（与 MenuAdminRepository.NormalizeForKind 同口径）----------
   MASTER_TABLE 保留：定制页以它为数据源锚点（报表数据集、搜索中心与选择器的锚点口径一致）。
   FILTER 一并归零：定制页自己解析取数范围，不读模块级 FILTER。 */
UPDATE dbo.MODULES
   SET SORT_FIELDS = NULL, NOT_BACK_FIELDS = NULL, NOT_BACK_FIELDS_M = NULL, DETAIL_NO_FIELDS = NULL,
       AUTO_APPROVE = 0, IF_COPY = 0, ERROR_NO_SAVE = 0, DETAIL_NO_SAVE = 0, EFFECT_ENGINE_TAG = 0,
       SEARCH_1 = 0, SEARCH_2 = 0,
       FORM_OPEN_MODE = NULL, FORM_DIALOG_WIDTH = NULL, FORM_DIALOG_HEIGHT = NULL
 WHERE M_IDX = 2315;

PRINT N'== 模块 2315 的工作台形态外配置已归零 ==';

/* ---------- 3. 清字段目录登记与字段数据来源（定制页不消费） ---------- */
DELETE FROM dbo.FIELD_DATASOURCE WHERE T_ID = N'MODULE_GROUPS';
PRINT N'== 已清 MODULE_GROUPS 字段数据来源 ' + CONVERT(NVARCHAR(10), @@ROWCOUNT) + N' 行 ==';

DELETE FROM dbo.FIELDS WHERE T_ID = N'MODULE_GROUPS';
PRINT N'== 已清 MODULE_GROUPS 字段登记 ' + CONVERT(NVARCHAR(10), @@ROWCOUNT) + N' 行 ==';

/* ---------- 4. 清脏标记与非当前快照（从工作台形态切走的节点不留这两样） ---------- */
DELETE FROM dbo.WORKBENCH_MODULE_DIRTY WHERE M_IDX = 2315;
PRINT N'== 已清模块 2315 的脏标记 ' + CONVERT(NVARCHAR(10), @@ROWCOUNT) + N' 行 ==';

DELETE FROM dbo.WORKBENCH_DEFINITION_SNAPSHOT WHERE M_IDX = 2315 AND ISNULL(IS_CURRENT, 0) = 0;
PRINT N'== 已清模块 2315 的非当前快照 ' + CONVERT(NVARCHAR(10), @@ROWCOUNT) + N' 行 ==';

/* ---------- 5. 收口断言 ---------- */
IF NOT EXISTS (SELECT 1 FROM dbo.V_MODULE_NODE WHERE M_IDX = 2315 AND NODE_KIND = N'CUSTOMPAGE')
    THROW 51884, N'模块 2315 的形态不是自定义承载页，迁移中止。', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.MODULES
                WHERE M_IDX = 2315 AND M_URL = N'/admin/module-groups' AND MASTER_TABLE = N'MODULE_GROUPS')
    THROW 51885, N'模块 2315 的承载页或数据源锚点不符，迁移中止。', 1;

IF EXISTS (SELECT 1 FROM dbo.MODULES
            WHERE M_IDX = 2315
              AND (NULLIF(LTRIM(RTRIM(ISNULL(SORT_FIELDS, N''))), N'') IS NOT NULL
                OR NULLIF(LTRIM(RTRIM(ISNULL(NOT_BACK_FIELDS, N''))), N'') IS NOT NULL
                OR NULLIF(LTRIM(RTRIM(ISNULL(NOT_BACK_FIELDS_M, N''))), N'') IS NOT NULL
                OR NULLIF(LTRIM(RTRIM(ISNULL(DETAIL_NO_FIELDS, N''))), N'') IS NOT NULL
                OR NULLIF(LTRIM(RTRIM(ISNULL(FORM_OPEN_MODE, N''))), N'') IS NOT NULL
                OR FORM_DIALOG_WIDTH IS NOT NULL OR FORM_DIALOG_HEIGHT IS NOT NULL
                OR ISNULL(AUTO_APPROVE, 0) = 1 OR ISNULL(IF_COPY, 0) = 1 OR ISNULL(ERROR_NO_SAVE, 0) = 1
                OR ISNULL(DETAIL_NO_SAVE, 0) = 1 OR ISNULL(EFFECT_ENGINE_TAG, 0) = 1
                OR ISNULL(SEARCH_1, 0) = 1 OR ISNULL(SEARCH_2, 0) = 1))
    THROW 51886, N'模块 2315 仍残留工作台形态外配置，迁移中止。', 1;

IF EXISTS (SELECT 1 FROM dbo.FIELDS WHERE T_ID = N'MODULE_GROUPS')
    THROW 51887, N'MODULE_GROUPS 仍残留字段登记，迁移中止。', 1;

IF EXISTS (SELECT 1 FROM dbo.FIELD_DATASOURCE WHERE T_ID = N'MODULE_GROUPS')
    THROW 51888, N'MODULE_GROUPS 仍残留字段数据来源，迁移中止。', 1;

IF EXISTS (SELECT 1 FROM dbo.WORKBENCH_MODULE_DIRTY WHERE M_IDX = 2315)
    THROW 51889, N'模块 2315 仍带脏标记（非工作台模块不该有），迁移中止。', 1;

COMMIT TRANSACTION;

DECLARE @Kind NVARCHAR(20) = (SELECT NODE_KIND FROM dbo.V_MODULE_NODE WHERE M_IDX = 2315);
DECLARE @Groups INT = (SELECT COUNT(*) FROM dbo.MODULE_GROUPS);
DECLARE @Modules INT = (SELECT COUNT(DISTINCT M_IDX) FROM dbo.MODULE_GROUPS);
PRINT N'== 收口：模块 2315 形态 ' + ISNULL(@Kind, N'(编号不存在)')
    + N'、承载页 /admin/module-groups；MODULE_GROUPS ' + CONVERT(NVARCHAR(10), @Groups) + N' 行 / '
    + CONVERT(NVARCHAR(10), @Modules) + N' 个模块 ==';
