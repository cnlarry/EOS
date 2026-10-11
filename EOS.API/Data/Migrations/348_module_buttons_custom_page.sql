-- ============================================================================
-- EOS.ERP migration 348: 自定义按钮配置面独立成页（登记模块 230103）
-- ----------------------------------------------------------------------------
-- ADR-033 批 2：模块管理的配置面按"配什么"拆开——230101「模块管理」保留基础属性与
-- 行为动作/校验规则（批 3 再拆成 230104），**自定义按钮**独立为 230103「自定义按钮」
-- （定制承载页 /admin/module-buttons）。
--
-- 为什么按钮先拆：按钮是 `MODULE_BUSINESS_ACTION` 里 `EVENT_CODE='MANUAL'` 的行，与效果链动作
-- **同表**；它的编辑对象（处理器键、按钮文案、确认提示）与动作行完全不同，混在一个页签里
-- 既难判权（模块配置权要覆盖两类），也难分工（配按钮的人不必能改效果链）。
--
-- 先决条件：**写路径的显式作用域必须先落地**（ADR-033 §6，同批代码已就位）。两个配置面各自保存时，
-- 服务端按 `scope=manual|actions|all` 的谓词删除/插入——否则保存按钮会删光该模块的行为动作与
-- 校验规则（反之亦然），且两边都会写审计"保存成功"。真库防线见 ModuleBusinessConfigScopeLiveTests。
--
-- 本迁移只做**登记**（不搬数据）：按钮行本就属于各目标模块，230103 只是"配它们的页面"。
--   ① 插入 230103 模块行：父/根 = 2301「系统功能」，`SORT_IDX=30`（紧跟 230101=10、230102=20），
--      数据源锚点 `MASTER_TABLE='MODULE_BUSINESS_ACTION'`（定制页以它为锚点）；
--   ② 授权镜像：**230101 → 230103**（同一批配置维护者；230101 是 230103 的镜像来源，
--      因为按钮配置原本就挂在 230101 的那一页签上）；
--   ③ 收口断言：形态必须是 CUSTOMPAGE / 承载页与锚点相符 / 授权镜像到位 / 形态外配置为 0 /
--      2301、230101、230102 未被顺手改动。
--
-- 路由三处登记的另一半在代码里（同批提交）：`ModuleRouteValidator.ExactRoutes` 与前端
-- `workspaceRoutes.tsx` / `lazyRoutes` / `routeMeta.ts`；缺一处菜单会落占位页（ADR-032 §6）。
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
    THROW 51890, @GuardMessage, 1;

IF OBJECT_ID(N'dbo.MODULES', N'U') IS NULL
    THROW 51891, N'MODULES 表不存在，迁移中止。', 1;

IF OBJECT_ID(N'dbo.V_MODULE_NODE', N'V') IS NULL
    THROW 51892, N'视图 dbo.V_MODULE_NODE 不存在（迁移 340 未执行？），迁移中止。', 1;

/* 目录与镜像来源都必须先就位：2301 是父目录，230101 是授权镜像的来源。 */
IF NOT EXISTS (SELECT 1 FROM dbo.MODULES WHERE M_IDX = 2301)
    THROW 51893, N'模块 2301「系统功能」不存在：本迁移是 347 的后续，迁移中止。', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.MODULES WHERE M_IDX = 230101)
    THROW 51894, N'模块 230101「模块管理」不存在：授权镜像以它为来源，迁移中止。', 1;

BEGIN TRANSACTION;

/* ---------- 1. 登记 230103「自定义按钮」 ---------- */
IF NOT EXISTS (SELECT 1 FROM dbo.MODULES WHERE M_IDX = 230103)
BEGIN
    INSERT INTO dbo.MODULES
        (M_IDX, M_DESC, M_URL, M_P_IDX, M_ROOT_IDX, SORT_IDX, M_TAG, MASTER_TABLE, FILTER, DETAIL_TABLE,
         REMARK, LAST_UPDATE_BY, LAST_UPDATE_DATE, M_ALIAS, DETAIL_NO_FIELDS, DETAIL_NO_SAVE,
         NOT_BACK_FIELDS, NOT_BACK_FIELDS_M, AUTO_APPROVE, ERROR_NO_SAVE, SEARCH_1, SEARCH_2,
         SORT_FIELDS, IF_COPY, M_ICON, FORM_OPEN_MODE, FORM_DIALOG_WIDTH, FORM_DIALOG_HEIGHT, APPROVE_ENABLED)
    VALUES
        (230103, N'自定义按钮', N'/admin/module-buttons', 2301, 2301, 30, 1, N'MODULE_BUSINESS_ACTION',
         NULL, NULL,
         N'自定义按钮的配置面（定制页 /admin/module-buttons）：为其它统一工作台模块维护 MODULE_BUSINESS_ACTION 里 EVENT_CODE=MANUAL 的按钮行（处理器键 + 按钮文案 + 确认提示）。保存后随模块发布生效。',
         N'348', GETDATE(), NULL, NULL, NULL, NULL, NULL, 0, 0, 0, 0, NULL, 0, NULL, NULL, NULL, NULL, 0);
    PRINT N'== 已登记 230103「自定义按钮」（承载页 /admin/module-buttons） ==';
END
ELSE
BEGIN
    PRINT N'== 230103 已存在，跳过登记 ==';
END;

/* ---------- 2. 授权镜像：230101 → 230103（逐列复制，带防重） ---------- */
INSERT INTO dbo.SYSDH
    (G_IDX, M_IDX, EXEC_TAG, ADDNEW_TAG, DELETE_TAG, EDIT_TAG, REPORT_TAG, COST_TAG, SETUP_TAG,
     SECRECY_TAG, ENDCASE_TAG, UNENDCASE_TAG, OTHER1_TAG, OTHER2_TAG, OTHER3_TAG, OTHER4_TAG,
     DENY_VIEW_FIELD_MASTER, DENY_VIEW_FIELD_DETAIL, DENY_NEW_FIELD_MASTER, DENY_NEW_FIELD_DETAIL,
     DENY_MODI_FIELD_MASTER, DENY_MODI_FIELD_DETAIL, DATA_FILTER, OPERFLAG, APPROVE_TAG, DEAPPROVE_TAG,
     FILE_VIEW_TAG, FILE_UPDA_TAG, FILE_EDIT_TAG, FILE_DELE_TAG, FORM_DESIGN_TAG, MODULE_CONFIG_TAG)
SELECT s.G_IDX, 230103, s.EXEC_TAG, s.ADDNEW_TAG, s.DELETE_TAG, s.EDIT_TAG, s.REPORT_TAG, s.COST_TAG,
       s.SETUP_TAG, s.SECRECY_TAG, s.ENDCASE_TAG, s.UNENDCASE_TAG, s.OTHER1_TAG, s.OTHER2_TAG,
       s.OTHER3_TAG, s.OTHER4_TAG, s.DENY_VIEW_FIELD_MASTER, s.DENY_VIEW_FIELD_DETAIL,
       s.DENY_NEW_FIELD_MASTER, s.DENY_NEW_FIELD_DETAIL, s.DENY_MODI_FIELD_MASTER, s.DENY_MODI_FIELD_DETAIL,
       s.DATA_FILTER, s.OPERFLAG, s.APPROVE_TAG, s.DEAPPROVE_TAG, s.FILE_VIEW_TAG, s.FILE_UPDA_TAG,
       s.FILE_EDIT_TAG, s.FILE_DELE_TAG, s.FORM_DESIGN_TAG, s.MODULE_CONFIG_TAG
FROM dbo.SYSDH s
WHERE s.M_IDX = 230101
  AND NOT EXISTS (SELECT 1 FROM dbo.SYSDH t WHERE t.M_IDX = 230103 AND t.G_IDX = s.G_IDX);

INSERT INTO dbo.SYSDD
    (USER_ID, M_IDX, EXEC_TAG, ADDNEW_TAG, DELETE_TAG, EDIT_TAG, REPORT_TAG, COST_TAG, SETUP_TAG,
     SECRECY_TAG, ENDCASE_TAG, UNENDCASE_TAG, OTHER1_TAG, OTHER2_TAG, OTHER3_TAG, OTHER4_TAG,
     DENY_VIEW_FIELD_MASTER, DENY_VIEW_FIELD_DETAIL, DENY_NEW_FIELD_MASTER, DENY_NEW_FIELD_DETAIL,
     DENY_MODI_FIELD_MASTER, DENY_MODI_FIELD_DETAIL, DATA_FILTER, OPERFLAG, APPROVE_TAG, DEAPPROVE_TAG,
     FILE_VIEW_TAG, FILE_UPDA_TAG, FILE_EDIT_TAG, FILE_DELE_TAG, FORM_DESIGN_TAG, MODULE_CONFIG_TAG)
SELECT s.USER_ID, 230103, s.EXEC_TAG, s.ADDNEW_TAG, s.DELETE_TAG, s.EDIT_TAG, s.REPORT_TAG, s.COST_TAG,
       s.SETUP_TAG, s.SECRECY_TAG, s.ENDCASE_TAG, s.UNENDCASE_TAG, s.OTHER1_TAG, s.OTHER2_TAG,
       s.OTHER3_TAG, s.OTHER4_TAG, s.DENY_VIEW_FIELD_MASTER, s.DENY_VIEW_FIELD_DETAIL,
       s.DENY_NEW_FIELD_MASTER, s.DENY_NEW_FIELD_DETAIL, s.DENY_MODI_FIELD_MASTER, s.DENY_MODI_FIELD_DETAIL,
       s.DATA_FILTER, s.OPERFLAG, s.APPROVE_TAG, s.DEAPPROVE_TAG, s.FILE_VIEW_TAG, s.FILE_UPDA_TAG,
       s.FILE_EDIT_TAG, s.FILE_DELE_TAG, s.FORM_DESIGN_TAG, s.MODULE_CONFIG_TAG
FROM dbo.SYSDD s
WHERE s.M_IDX = 230101
  AND NOT EXISTS (SELECT 1 FROM dbo.SYSDD t WHERE t.M_IDX = 230103 AND t.USER_ID = s.USER_ID);

PRINT N'== 授权行已镜像 230101 → 230103 ==';

/* ---------- 3. 收口断言 ---------- */
IF NOT EXISTS (SELECT 1 FROM dbo.MODULES
                WHERE M_IDX = 230103
                  AND M_URL = N'/admin/module-buttons'
                  AND MASTER_TABLE = N'MODULE_BUSINESS_ACTION'
                  AND M_P_IDX = 2301 AND M_ROOT_IDX = 2301)
    THROW 51895, N'230103 的承载页、数据源锚点或父级不符，迁移中止。', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.V_MODULE_NODE WHERE M_IDX = 230103 AND NODE_KIND = N'CUSTOMPAGE')
    THROW 51896, N'230103 的形态不是自定义承载页，迁移中止。', 1;

/* 形态外配置必须为 0（与迁移 340 的归一口径一致；列 EFFECT_ENGINE_TAG 已由迁移 346 删除，不再引用）。 */
IF EXISTS (SELECT 1 FROM dbo.MODULES
            WHERE M_IDX = 230103
              AND (NULLIF(LTRIM(RTRIM(ISNULL(SORT_FIELDS, N''))), N'') IS NOT NULL
                OR NULLIF(LTRIM(RTRIM(ISNULL(NOT_BACK_FIELDS, N''))), N'') IS NOT NULL
                OR NULLIF(LTRIM(RTRIM(ISNULL(NOT_BACK_FIELDS_M, N''))), N'') IS NOT NULL
                OR NULLIF(LTRIM(RTRIM(ISNULL(DETAIL_NO_FIELDS, N''))), N'') IS NOT NULL
                OR NULLIF(LTRIM(RTRIM(ISNULL(FORM_OPEN_MODE, N''))), N'') IS NOT NULL
                OR FORM_DIALOG_WIDTH IS NOT NULL OR FORM_DIALOG_HEIGHT IS NOT NULL
                OR ISNULL(AUTO_APPROVE, 0) = 1 OR ISNULL(IF_COPY, 0) = 1 OR ISNULL(ERROR_NO_SAVE, 0) = 1
                OR ISNULL(DETAIL_NO_SAVE, 0) = 1 OR ISNULL(APPROVE_ENABLED, 0) = 1
                OR ISNULL(SEARCH_1, 0) = 1 OR ISNULL(SEARCH_2, 0) = 1))
    THROW 51897, N'230103 残留工作台形态外配置，迁移中止。', 1;

/* 授权镜像到位：230101 的每个主体在 230103 上都要有（反向不必——230103 可以更宽）。 */
IF EXISTS (SELECT 1 FROM dbo.SYSDH s
            WHERE s.M_IDX = 230101
              AND NOT EXISTS (SELECT 1 FROM dbo.SYSDH t WHERE t.M_IDX = 230103 AND t.G_IDX = s.G_IDX))
    THROW 51898, N'230103 的群组授权未与 230101 镜像，迁移中止。', 1;

IF EXISTS (SELECT 1 FROM dbo.SYSDD s
            WHERE s.M_IDX = 230101
              AND NOT EXISTS (SELECT 1 FROM dbo.SYSDD t WHERE t.M_IDX = 230103 AND t.USER_ID = s.USER_ID))
    THROW 51899, N'230103 的用户授权未与 230101 镜像，迁移中止。', 1;

/* 目录与两个兄弟页不得被顺手改动。 */
IF EXISTS (SELECT 1 FROM dbo.MODULES
            WHERE (M_IDX = 2301 AND (M_URL IS NOT NULL OR MASTER_TABLE IS NOT NULL))
               OR (M_IDX = 230101 AND (M_URL <> N'/admin/menus' OR M_P_IDX <> 2301))
               OR (M_IDX = 230102 AND (M_URL <> N'/admin/module-groups' OR M_P_IDX <> 2301)))
    THROW 51900, N'2301 / 230101 / 230102 的承载页或父级被改动，迁移中止。', 1;

COMMIT TRANSACTION;

DECLARE @Kind NVARCHAR(20) = (SELECT NODE_KIND FROM dbo.V_MODULE_NODE WHERE M_IDX = 230103);
DECLARE @Groups INT = (SELECT COUNT(*) FROM dbo.SYSDH WHERE M_IDX = 230103);
DECLARE @Users INT = (SELECT COUNT(*) FROM dbo.SYSDD WHERE M_IDX = 230103);
DECLARE @Buttons INT = (SELECT COUNT(*) FROM dbo.MODULE_BUSINESS_ACTION WHERE EVENT_CODE = N'MANUAL');
PRINT N'== 收口：230103 形态 ' + ISNULL(@Kind, N'(编号不存在)') + N'、承载页 /admin/module-buttons；'
    + N'授权镜像群组 ' + CONVERT(NVARCHAR(10), @Groups) + N' 行 / 用户 ' + CONVERT(NVARCHAR(10), @Users)
    + N' 行；库内按钮行（各模块合计）' + CONVERT(NVARCHAR(10), @Buttons) + N' 行 ==';
