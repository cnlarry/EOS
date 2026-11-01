-- ============================================================================
-- EOS.ERP migration 464：指标口径维护面（模块 2317）
-- ----------------------------------------------------------------------------
--  背景（ADR-046）：`REPORT_METRIC` 此前只有 GET，客户侧口径名/维度不合适只能找开发。
--  后端写路径已就绪（`/api/v1/report-admin/metrics`，见 ReportAdminController），本迁移补**承载页**：
--  一个定制管理页 `/admin/metrics` + 模块号 2317，权限走模块权限（谁维护由管理员在 2306 分配）。
--
--  为什么单独开模块而不是挂 2201：同一条口径被报表与助手共同消费，
--  把它绑在"报表设置"的权限上会让"谁能改口径"取决于"谁能改报表"。
--
--  默认只给 admin 一条开箱授权（与 3106/3105 同形：EXEC_TAG='Z'，各布尔位全开）。
-- ============================================================================
SET NOCOUNT ON;
SET XACT_ABORT ON;

IF DB_NAME() <> N'EOS.ERP'
    THROW 51990, N'本脚本只允许在 EOS.ERP 数据库上执行。', 1;

/* ---------- 1. 模块 2317 · 指标口径（系统管理 23 下的定制承页） ---------- */
IF NOT EXISTS (SELECT 1 FROM dbo.MODULES WHERE M_IDX = 2317)
BEGIN
    INSERT INTO dbo.MODULES
        (M_IDX, M_DESC, M_URL, M_P_IDX, M_ROOT_IDX, SORT_IDX, M_TAG, MASTER_TABLE, FILTER, DETAIL_TABLE,
         REMARK, MODIFIED_BY, MODIFIED_TIME, M_ALIAS, DETAIL_NO_FIELDS, DETAIL_NO_SAVE,
         NOT_BACK_FIELDS, NOT_BACK_FIELDS_M, AUTO_APPROVE, ERROR_NO_SAVE, SEARCH_1, SEARCH_2,
         SORT_FIELDS, IF_COPY, M_ICON, FORM_OPEN_MODE, FORM_DIALOG_WIDTH, FORM_DIALOG_HEIGHT,
         APPROVE_ENABLED, UNIFIED_FORM_ENABLED, UNIFIED_FORM_EDIT_ENABLED)
    VALUES
        (2317, N'指标口径', N'/admin/metrics', 23, 23, 50, 1, NULL, NULL, NULL,
         N'指标口径（REPORT_METRIC）维护：新增/修改/确认/删除。校验在服务端（受控表达式解析 +'
         + N' 物理列校验 + 行过滤归属），删除只允许候选口径。可见性与维护权由管理员在 2306 分配。',
         N'464', GETDATE(), NULL, NULL, NULL, NULL, NULL, 0, 0, 0, 0,
         NULL, 0, NULL, NULL, NULL, NULL, 0, 0, 0);

    PRINT N'== 模块 2317 已登记 ==';
END
ELSE
    PRINT N'== 模块 2317 已存在，跳过 ==';

/* ---------- 2. 开箱可用：给现有 admin 一条 2317 授权 ---------- */
IF EXISTS (SELECT 1 FROM dbo.SYS_USER_ACCOUNT WHERE USER_ID = N'admin')
   AND NOT EXISTS (SELECT 1 FROM dbo.SYS_USER_MODULE_PERMISSION WHERE USER_ID = N'admin' AND M_IDX = 2317)
BEGIN
    INSERT INTO dbo.SYS_USER_MODULE_PERMISSION
        (USER_ID, M_IDX, EXEC_TAG, ADDNEW_TAG, DELETE_TAG, EDIT_TAG, REPORT_TAG, COST_TAG,
         SETUP_TAG, SECRECY_TAG, ENDCASE_TAG, UNENDCASE_TAG, OTHER1_TAG, OTHER2_TAG, OTHER3_TAG, OTHER4_TAG,
         DENY_VIEW_FIELD_MASTER, DENY_VIEW_FIELD_DETAIL, DENY_NEW_FIELD_MASTER, DENY_NEW_FIELD_DETAIL,
         DENY_MODI_FIELD_MASTER, DENY_MODI_FIELD_DETAIL, DATA_FILTER, OPERFLAG,
         APPROVE_TAG, DEAPPROVE_TAG, FILE_VIEW_TAG, FILE_UPDA_TAG, FILE_EDIT_TAG, FILE_DELE_TAG,
         FORM_DESIGN_TAG, MODULE_CONFIG_TAG)
    VALUES
        (N'admin', 2317, N'Z', 1, 1, 1, 1, 1,
         1, 1, 0, 0, 0, 0, 0, 0,
         N'', N'', N'', N'',
         N'', N'', N'', 1,
         0, 0, 1, 0, 0, 0,
         0, 0);

    PRINT N'== 已给 admin 授权模块 2317 ==';
END
ELSE
    PRINT N'== admin 已存在或已授权 2317，跳过 ==';
