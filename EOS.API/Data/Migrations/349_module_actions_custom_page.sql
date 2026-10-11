-- ============================================================================
-- EOS.ERP migration 349: 行为动作 + 校验规则独立成页（登记 230104）＋ 修正 230103 的数据源锚点
-- ----------------------------------------------------------------------------
-- ADR-033 批 3：模块管理的最后两个行为页签（「行为动作」「校验规则」）迁到 230104
-- 「行为动作 + 校验规则」（定制承载页 /admin/module-actions，页内两个页签一起提交）。
-- 230101 此后只剩「基础 / 主表 / 子表」——它只定义"模块是什么"，不定义"模块怎么动"（ADR-033 §2）。
--
-- 同一批修正 348 的一处偏差：ADR-033 §9 的契约表写明 230103 / 230104 的 `MASTER_TABLE` **为空**
-- （它们是新定制页，不承载任何自己的数据；`MODULE_BUSINESS_ACTION` 属于**被配置的目标模块**，
-- 不是这个页面的锚点）。348 已落库、按纪律不改已应用迁移，故修正走本迁移。
--
-- 本迁移只做**登记与修正**（不搬数据）：两类配置行本就属于各目标模块。
--   ① 修正 230103：清 MASTER_TABLE（其余不动）；
--   ② 插入 230104 模块行：父/根 = 2301「系统功能」，`SORT_IDX=40`（紧跟 230101=10、230102=20、
--      230103=30），`M_URL=/admin/module-actions`，`MASTER_TABLE` 空；
--   ③ 授权镜像：**230101 → 230104**（同一批配置维护者；动作配置原本就挂在 230101 的两个页签上）；
--   ④ 收口断言：两个节点形态必须 CUSTOMPAGE / 承载页相符 / **MASTER_TABLE 必须为空** /
--      授权镜像到位 / 形态外配置为 0 / 2301、230101、230102、230103 未被顺手改动。
--
-- 路由三处登记的另一半在代码里（同批提交）：`ModuleRouteValidator.ExactRoutes` 与前端
-- `lazyRoutes` / `workspaceRoutes` / `routeMeta`；缺一处菜单会落占位页（ADR-032 §6）。
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
    THROW 51910, @GuardMessage, 1;

IF OBJECT_ID(N'dbo.MODULES', N'U') IS NULL
    THROW 51911, N'MODULES 表不存在，迁移中止。', 1;

IF OBJECT_ID(N'dbo.V_MODULE_NODE', N'V') IS NULL
    THROW 51912, N'视图 dbo.V_MODULE_NODE 不存在（迁移 340 未执行？），迁移中止。', 1;

/* 目录、镜像来源与上一个配置面都必须先就位：本迁移是 347/348 的后续。 */
IF NOT EXISTS (SELECT 1 FROM dbo.MODULES WHERE M_IDX = 2301)
    THROW 51913, N'模块 2301「系统功能」不存在：本迁移是 347/348 的后续，迁移中止。', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.MODULES WHERE M_IDX = 230101)
    THROW 51914, N'模块 230101「模块管理」不存在：授权镜像以它为来源，迁移中止。', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.MODULES WHERE M_IDX = 230103)
    THROW 51915, N'模块 230103「自定义按钮」不存在：本迁移要修正它的数据源锚点，迁移中止。', 1;

BEGIN TRANSACTION;

/* ---------- 1. 修正 230103：定制页不承载自己的数据，锚点必须为空（ADR-033 §9） ---------- */
UPDATE dbo.MODULES
   SET MASTER_TABLE = NULL, LAST_UPDATE_BY = N'349', LAST_UPDATE_DATE = GETDATE()
 WHERE M_IDX = 230103
   AND MASTER_TABLE IS NOT NULL;
PRINT N'== 230103 的数据源锚点已清空 ' + CONVERT(NVARCHAR(10), @@ROWCOUNT) + N' 行 ==';

/* ---------- 2. 登记 230104「行为动作 + 校验规则」 ---------- */
IF NOT EXISTS (SELECT 1 FROM dbo.MODULES WHERE M_IDX = 230104)
BEGIN
    INSERT INTO dbo.MODULES
        (M_IDX, M_DESC, M_URL, M_P_IDX, M_ROOT_IDX, SORT_IDX, M_TAG, MASTER_TABLE, FILTER, DETAIL_TABLE,
         REMARK, LAST_UPDATE_BY, LAST_UPDATE_DATE, M_ALIAS, DETAIL_NO_FIELDS, DETAIL_NO_SAVE,
         NOT_BACK_FIELDS, NOT_BACK_FIELDS_M, AUTO_APPROVE, ERROR_NO_SAVE, SEARCH_1, SEARCH_2,
         SORT_FIELDS, IF_COPY, M_ICON, FORM_OPEN_MODE, FORM_DIALOG_WIDTH, FORM_DIALOG_HEIGHT, APPROVE_ENABLED)
    VALUES
        (230104, N'行为动作 + 校验规则', N'/admin/module-actions', 2301, 2301, 40, 1, NULL,
         NULL, NULL,
         N'行为动作与校验规则的配置面（定制页 /admin/module-actions，页内两个页签）：为其它统一工作台模块维护效果链动作（MODULE_BUSINESS_ACTION 里非 MANUAL 的行）与校验规则（MODULE_VALIDATION_RULE）。保存后自动发布一次；校验不通过则是"已保存，但未生效"并逐条给出原因。',
         N'349', GETDATE(), NULL, NULL, NULL, NULL, NULL, 0, 0, 0, 0, NULL, 0, NULL, NULL, NULL, NULL, 0);
    PRINT N'== 已登记 230104「行为动作 + 校验规则」（承载页 /admin/module-actions） ==';
END
ELSE
BEGIN
    PRINT N'== 230104 已存在，跳过登记 ==';
END;

/* ---------- 3. 授权镜像：230101 → 230104（逐列复制，带防重） ---------- */
INSERT INTO dbo.SYSDH
    (G_IDX, M_IDX, EXEC_TAG, ADDNEW_TAG, DELETE_TAG, EDIT_TAG, REPORT_TAG, COST_TAG, SETUP_TAG,
     SECRECY_TAG, ENDCASE_TAG, UNENDCASE_TAG, OTHER1_TAG, OTHER2_TAG, OTHER3_TAG, OTHER4_TAG,
     DENY_VIEW_FIELD_MASTER, DENY_VIEW_FIELD_DETAIL, DENY_NEW_FIELD_MASTER, DENY_NEW_FIELD_DETAIL,
     DENY_MODI_FIELD_MASTER, DENY_MODI_FIELD_DETAIL, DATA_FILTER, OPERFLAG, APPROVE_TAG, DEAPPROVE_TAG,
     FILE_VIEW_TAG, FILE_UPDA_TAG, FILE_EDIT_TAG, FILE_DELE_TAG, FORM_DESIGN_TAG, MODULE_CONFIG_TAG)
SELECT s.G_IDX, 230104, s.EXEC_TAG, s.ADDNEW_TAG, s.DELETE_TAG, s.EDIT_TAG, s.REPORT_TAG, s.COST_TAG,
       s.SETUP_TAG, s.SECRECY_TAG, s.ENDCASE_TAG, s.UNENDCASE_TAG, s.OTHER1_TAG, s.OTHER2_TAG,
       s.OTHER3_TAG, s.OTHER4_TAG, s.DENY_VIEW_FIELD_MASTER, s.DENY_VIEW_FIELD_DETAIL,
       s.DENY_NEW_FIELD_MASTER, s.DENY_NEW_FIELD_DETAIL, s.DENY_MODI_FIELD_MASTER, s.DENY_MODI_FIELD_DETAIL,
       s.DATA_FILTER, s.OPERFLAG, s.APPROVE_TAG, s.DEAPPROVE_TAG, s.FILE_VIEW_TAG, s.FILE_UPDA_TAG,
       s.FILE_EDIT_TAG, s.FILE_DELE_TAG, s.FORM_DESIGN_TAG, s.MODULE_CONFIG_TAG
FROM dbo.SYSDH s
WHERE s.M_IDX = 230101
  AND NOT EXISTS (SELECT 1 FROM dbo.SYSDH t WHERE t.M_IDX = 230104 AND t.G_IDX = s.G_IDX);

INSERT INTO dbo.SYSDD
    (USER_ID, M_IDX, EXEC_TAG, ADDNEW_TAG, DELETE_TAG, EDIT_TAG, REPORT_TAG, COST_TAG, SETUP_TAG,
     SECRECY_TAG, ENDCASE_TAG, UNENDCASE_TAG, OTHER1_TAG, OTHER2_TAG, OTHER3_TAG, OTHER4_TAG,
     DENY_VIEW_FIELD_MASTER, DENY_VIEW_FIELD_DETAIL, DENY_NEW_FIELD_MASTER, DENY_NEW_FIELD_DETAIL,
     DENY_MODI_FIELD_MASTER, DENY_MODI_FIELD_DETAIL, DATA_FILTER, OPERFLAG, APPROVE_TAG, DEAPPROVE_TAG,
     FILE_VIEW_TAG, FILE_UPDA_TAG, FILE_EDIT_TAG, FILE_DELE_TAG, FORM_DESIGN_TAG, MODULE_CONFIG_TAG)
SELECT s.USER_ID, 230104, s.EXEC_TAG, s.ADDNEW_TAG, s.DELETE_TAG, s.EDIT_TAG, s.REPORT_TAG, s.COST_TAG,
       s.SETUP_TAG, s.SECRECY_TAG, s.ENDCASE_TAG, s.UNENDCASE_TAG, s.OTHER1_TAG, s.OTHER2_TAG,
       s.OTHER3_TAG, s.OTHER4_TAG, s.DENY_VIEW_FIELD_MASTER, s.DENY_VIEW_FIELD_DETAIL,
       s.DENY_NEW_FIELD_MASTER, s.DENY_NEW_FIELD_DETAIL, s.DENY_MODI_FIELD_MASTER, s.DENY_MODI_FIELD_DETAIL,
       s.DATA_FILTER, s.OPERFLAG, s.APPROVE_TAG, s.DEAPPROVE_TAG, s.FILE_VIEW_TAG, s.FILE_UPDA_TAG,
       s.FILE_EDIT_TAG, s.FILE_DELE_TAG, s.FORM_DESIGN_TAG, s.MODULE_CONFIG_TAG
FROM dbo.SYSDD s
WHERE s.M_IDX = 230101
  AND NOT EXISTS (SELECT 1 FROM dbo.SYSDD t WHERE t.M_IDX = 230104 AND t.USER_ID = s.USER_ID);

PRINT N'== 授权行已镜像 230101 → 230104 ==';

/* ---------- 4. 收口断言 ---------- */
IF NOT EXISTS (SELECT 1 FROM dbo.MODULES
                WHERE M_IDX = 230104
                  AND M_URL = N'/admin/module-actions'
                  AND MASTER_TABLE IS NULL
                  AND M_P_IDX = 2301 AND M_ROOT_IDX = 2301)
    THROW 51916, N'230104 的承载页、数据源锚点或父级不符（锚点必须为空，ADR-033 §9），迁移中止。', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.V_MODULE_NODE WHERE M_IDX = 230104 AND NODE_KIND = N'CUSTOMPAGE')
    THROW 51917, N'230104 的形态不是自定义承载页，迁移中止。', 1;

IF EXISTS (SELECT 1 FROM dbo.MODULES WHERE M_IDX = 230103 AND MASTER_TABLE IS NOT NULL)
    THROW 51918, N'230103 的数据源锚点未清空（ADR-033 §9 要求为空），迁移中止。', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.V_MODULE_NODE WHERE M_IDX = 230103 AND NODE_KIND = N'CUSTOMPAGE')
    THROW 51919, N'230103 的形态不是自定义承载页，迁移中止。', 1;

/* 形态外配置必须为 0（列 EFFECT_ENGINE_TAG 已由迁移 346 删除，不再引用）。 */
IF EXISTS (SELECT 1 FROM dbo.MODULES
            WHERE M_IDX IN (230103, 230104)
              AND (NULLIF(LTRIM(RTRIM(ISNULL(SORT_FIELDS, N''))), N'') IS NOT NULL
                OR NULLIF(LTRIM(RTRIM(ISNULL(NOT_BACK_FIELDS, N''))), N'') IS NOT NULL
                OR NULLIF(LTRIM(RTRIM(ISNULL(NOT_BACK_FIELDS_M, N''))), N'') IS NOT NULL
                OR NULLIF(LTRIM(RTRIM(ISNULL(DETAIL_NO_FIELDS, N''))), N'') IS NOT NULL
                OR NULLIF(LTRIM(RTRIM(ISNULL(FORM_OPEN_MODE, N''))), N'') IS NOT NULL
                OR FORM_DIALOG_WIDTH IS NOT NULL OR FORM_DIALOG_HEIGHT IS NOT NULL
                OR ISNULL(AUTO_APPROVE, 0) = 1 OR ISNULL(IF_COPY, 0) = 1 OR ISNULL(ERROR_NO_SAVE, 0) = 1
                OR ISNULL(DETAIL_NO_SAVE, 0) = 1 OR ISNULL(APPROVE_ENABLED, 0) = 1
                OR ISNULL(SEARCH_1, 0) = 1 OR ISNULL(SEARCH_2, 0) = 1))
    THROW 51920, N'230103 / 230104 残留工作台形态外配置，迁移中止。', 1;

/* 授权镜像到位：230101 的每个主体在 230104 上都要有（反向不必）。 */
IF EXISTS (SELECT 1 FROM dbo.SYSDH s
            WHERE s.M_IDX = 230101
              AND NOT EXISTS (SELECT 1 FROM dbo.SYSDH t WHERE t.M_IDX = 230104 AND t.G_IDX = s.G_IDX))
    THROW 51921, N'230104 的群组授权未与 230101 镜像，迁移中止。', 1;

IF EXISTS (SELECT 1 FROM dbo.SYSDD s
            WHERE s.M_IDX = 230101
              AND NOT EXISTS (SELECT 1 FROM dbo.SYSDD t WHERE t.M_IDX = 230104 AND t.USER_ID = s.USER_ID))
    THROW 51922, N'230104 的用户授权未与 230101 镜像，迁移中止。', 1;

/* 目录与三个兄弟页不得被顺手改动。 */
IF EXISTS (SELECT 1 FROM dbo.MODULES
            WHERE (M_IDX = 2301 AND (M_URL IS NOT NULL OR MASTER_TABLE IS NOT NULL))
               OR (M_IDX = 230101 AND (M_URL <> N'/admin/menus' OR M_P_IDX <> 2301))
               OR (M_IDX = 230102 AND (M_URL <> N'/admin/module-groups' OR M_P_IDX <> 2301))
               OR (M_IDX = 230103 AND (M_URL <> N'/admin/module-buttons' OR M_P_IDX <> 2301)))
    THROW 51923, N'2301 / 230101 / 230102 / 230103 的承载页或父级被改动，迁移中止。', 1;

COMMIT TRANSACTION;

DECLARE @Kind NVARCHAR(20) = (SELECT NODE_KIND FROM dbo.V_MODULE_NODE WHERE M_IDX = 230104);
DECLARE @Groups INT = (SELECT COUNT(*) FROM dbo.SYSDH WHERE M_IDX = 230104);
DECLARE @Users INT = (SELECT COUNT(*) FROM dbo.SYSDD WHERE M_IDX = 230104);
DECLARE @Actions INT = (SELECT COUNT(*) FROM dbo.MODULE_BUSINESS_ACTION WHERE EVENT_CODE <> N'MANUAL');
DECLARE @Rules INT = (SELECT COUNT(*) FROM dbo.MODULE_VALIDATION_RULE);
PRINT N'== 收口：230104 形态 ' + ISNULL(@Kind, N'(编号不存在)') + N'、承载页 /admin/module-actions；'
    + N'授权镜像群组 ' + CONVERT(NVARCHAR(10), @Groups) + N' 行 / 用户 ' + CONVERT(NVARCHAR(10), @Users)
    + N' 行；库内效果链动作 ' + CONVERT(NVARCHAR(10), @Actions) + N' 行、校验规则 '
    + CONVERT(NVARCHAR(10), @Rules) + N' 行 ==';
