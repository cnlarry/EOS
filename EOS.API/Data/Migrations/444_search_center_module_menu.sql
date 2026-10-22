-- ============================================================================
-- EOS.ERP migration 444: 查询中心 34（/search-center）恢复为一级菜单入口
-- ----------------------------------------------------------------------------
--  旧系统的「××查询中心」曾按业务域各占一个菜单叶子（2501–2508，迁移 314 下线），
--  能力本身保留为 /search-center 页面，但入口一度只剩统一工作台的一个按钮。
--  本迁移把它恢复成一个**独立的一级菜单项**（34「查询中心」），承载页即 /search-center。
--
--  为什么是一个模块行而不是又一个"发现式入口"：
--  - 菜单可见性、权限授予、样式与其它页面走同一套（SYS_*_MODULE_PERMISSION），
--    不需要为它造第二套"能不能进"的判定；
--  - 它没有主表（检索面由各业务模块的 SEARCH_1/SEARCH_2 决定），所以 MASTER_TABLE 必须为空、
--    SEARCH_1/SEARCH_2 必须为 0（无主表节点置检索位是无意义配置，check-module-node-kind 守这条）。
--
--  授权沿用 2102「我的任务」——同为跨模块聚合入口、受众相同（逐人/逐组授予，非"所有人"）。
--  进入后在页面内看到的模块与行，仍由各自的模块权限与数据范围过滤，入口不等于放大权限。
--
--  幂等：模块行已存在则跳过登记；授权行按 USER_ID / G_IDX 防重。
-- ============================================================================
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET ARITHABORT ON;

DECLARE @GuardMessage NVARCHAR(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 51920, @GuardMessage, 1;

IF OBJECT_ID(N'dbo.MODULES', N'U') IS NULL
    THROW 51921, N'MODULES 表不存在，迁移中止。', 1;

IF OBJECT_ID(N'dbo.V_MODULE_NODE', N'V') IS NULL
    THROW 51922, N'视图 dbo.V_MODULE_NODE 不存在（迁移 340 未执行？），迁移中止。', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.MODULES WHERE M_IDX = 2102)
    THROW 51923, N'模块 2102「我的任务」不存在：授权镜像以它为来源，迁移中止。', 1;

IF EXISTS (SELECT 1 FROM dbo.MODULES WHERE M_IDX = 34)
BEGIN
    -- 已登记则跳过（幂等）：末段断言会校验它的形态，不在这里重复插入。
    PRINT N'== 34 已存在，跳过登记 ==';
END
ELSE
BEGIN
    BEGIN TRANSACTION;

    /* ---------- 1. 登记 34「查询中心」：一级节点、无主表、承载页 /search-center ---------- */
    INSERT INTO dbo.MODULES
        (M_IDX, M_DESC, M_URL, M_P_IDX, M_ROOT_IDX, SORT_IDX, M_TAG, MASTER_TABLE, FILTER, DETAIL_TABLE,
         REMARK, LAST_UPDATE_BY, LAST_UPDATE_DATE, M_ALIAS, DETAIL_NO_FIELDS, DETAIL_NO_SAVE,
         NOT_BACK_FIELDS, NOT_BACK_FIELDS_M, AUTO_APPROVE, ERROR_NO_SAVE, SEARCH_1, SEARCH_2,
         SORT_FIELDS, IF_COPY, M_ICON, FORM_OPEN_MODE, FORM_DIALOG_WIDTH, FORM_DIALOG_HEIGHT, APPROVE_ENABLED)
    VALUES
        (34, N'查询中心', N'/search-center', 0, 34, 235, 1, NULL,
         NULL, NULL,
         N'跨模块检索入口：不必先进具体模块，按各模块登记的可检索字段（SEARCH_1 主表 / SEARCH_2 明细表）找记录。'
         + N'可见性与统一工作台同一真源——模块清单按当前用户权限过滤，行范围同样受模块主表过滤条件、'
         + N'用户数据范围（DATA_FILTER）与执行范围（EXEC_TAG）约束。',
         N'444', GETDATE(), NULL, NULL, NULL, NULL, NULL, 0, 0, 0, 0, NULL, 0, NULL, NULL, NULL, NULL, 0);
    PRINT N'== 已登记 34「查询中心」（承载页 /search-center） ==';

    COMMIT TRANSACTION;
END

/* ---------- 2. 授权镜像：2102「我的任务」→ 34「查询中心」 ---------- */
BEGIN TRANSACTION;

INSERT INTO dbo.SYS_USER_MODULE_PERMISSION
    (USER_ID, M_IDX, EXEC_TAG, ADDNEW_TAG, DELETE_TAG, EDIT_TAG, REPORT_TAG, COST_TAG, SETUP_TAG,
     SECRECY_TAG, ENDCASE_TAG, UNENDCASE_TAG, OTHER1_TAG, OTHER2_TAG, OTHER3_TAG, OTHER4_TAG,
     DENY_VIEW_FIELD_MASTER, DENY_VIEW_FIELD_DETAIL, DENY_NEW_FIELD_MASTER, DENY_NEW_FIELD_DETAIL,
     DENY_MODI_FIELD_MASTER, DENY_MODI_FIELD_DETAIL, DATA_FILTER, OPERFLAG, APPROVE_TAG, DEAPPROVE_TAG,
     FILE_VIEW_TAG, FILE_UPDA_TAG, FILE_EDIT_TAG, FILE_DELE_TAG, FORM_DESIGN_TAG, MODULE_CONFIG_TAG)
SELECT s.USER_ID, 34, s.EXEC_TAG, 0, 0, 0, 0, 0, 0,
       0, 0, 0, 0, 0, 0, 0,
       NULL, NULL, NULL, NULL, NULL, NULL, NULL,
       0, 0, 0, 0, 0, 0, 0, 0, 0
FROM dbo.SYS_USER_MODULE_PERMISSION s
WHERE s.M_IDX = 2102
  AND NOT EXISTS (SELECT 1 FROM dbo.SYS_USER_MODULE_PERMISSION t WHERE t.M_IDX = 34 AND t.USER_ID = s.USER_ID);

INSERT INTO dbo.SYS_GROUP_MODULE_PERMISSION
    (G_IDX, M_IDX, EXEC_TAG, ADDNEW_TAG, DELETE_TAG, EDIT_TAG, REPORT_TAG, COST_TAG, SETUP_TAG,
     SECRECY_TAG, ENDCASE_TAG, UNENDCASE_TAG, OTHER1_TAG, OTHER2_TAG, OTHER3_TAG, OTHER4_TAG,
     DENY_VIEW_FIELD_MASTER, DENY_VIEW_FIELD_DETAIL, DENY_NEW_FIELD_MASTER, DENY_NEW_FIELD_DETAIL,
     DENY_MODI_FIELD_MASTER, DENY_MODI_FIELD_DETAIL, DATA_FILTER, OPERFLAG, APPROVE_TAG, DEAPPROVE_TAG,
     FILE_VIEW_TAG, FILE_UPDA_TAG, FILE_EDIT_TAG, FILE_DELE_TAG, FORM_DESIGN_TAG, MODULE_CONFIG_TAG)
SELECT s.G_IDX, 34, s.EXEC_TAG, 0, 0, 0, 0, 0, 0,
       0, 0, 0, 0, 0, 0, 0,
       NULL, NULL, NULL, NULL, NULL, NULL, NULL,
       0, 0, 0, 0, 0, 0, 0, 0, 0
FROM dbo.SYS_GROUP_MODULE_PERMISSION s
WHERE s.M_IDX = 2102
  AND NOT EXISTS (SELECT 1 FROM dbo.SYS_GROUP_MODULE_PERMISSION t WHERE t.M_IDX = 34 AND t.G_IDX = s.G_IDX);

PRINT N'== 授权行已镜像 2102 → 34 ==';

COMMIT TRANSACTION;

/* ---------- 3. 收口断言 ---------- */
IF NOT EXISTS (SELECT 1 FROM dbo.MODULES
                WHERE M_IDX = 34
                  AND M_URL = N'/search-center'
                  AND MASTER_TABLE IS NULL
                  AND M_P_IDX = 0 AND M_ROOT_IDX = 34
                  AND ISNULL(SEARCH_1, 0) = 0 AND ISNULL(SEARCH_2, 0) = 0)
    THROW 51925, N'34 的承载页、数据源锚点、检索位或父级不符，迁移中止。', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.V_MODULE_NODE WHERE M_IDX = 34 AND NODE_KIND = N'CUSTOMPAGE')
    THROW 51926, N'34 的形态不是自定义承载页，迁移中止。', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.SYS_USER_MODULE_PERMISSION WHERE M_IDX = 34)
    THROW 51927, N'34 没有任何个人授权行（镜像源 2102 为空？），迁移中止。', 1;
