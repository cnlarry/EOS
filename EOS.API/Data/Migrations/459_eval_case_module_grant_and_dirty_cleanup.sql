-- ============================================================================
-- EOS.ERP migration 459：评估用例模块的开箱可用与脏标记清理
-- ----------------------------------------------------------------------------
--  458 把"尺子"搬进库（表 + 模块 3106 + 字段 + 种子），但权限按设计**不预置**，
--  结果是连 admin 自己都进不去（权限是真源：没有授权行就没有浏览权）。
--  这把"谁维护"的决定权交给管理员，同时保证开箱即有一个人能进去做第一次分配：
--  只给现有的 admin 账号一条与 3101/3102/3105 同形的授权（EXEC_TAG='Z'，各布尔位全开）。
--
--  另一件：WORKBENCH_MODULE_DIRTY 里可能残留指向**不存在模块**的行（历史测试产物）。
--  它会让"待发布清单"永远挂着一个幽灵项，check-module-node-kind 会因此判失败。
--  这里只删"模块已不存在"的行，不碰任何现存模块的脏标记。
-- ============================================================================
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;

IF DB_NAME() <> N'EOS.ERP'
    THROW 51940, N'本脚本只允许在 EOS.ERP 数据库上执行。', 1;

/* ---------- 1. 开箱可用：给现有 admin 一条 3106 授权 ---------- */
IF EXISTS (SELECT 1 FROM dbo.SYS_USER_ACCOUNT WHERE USER_ID = N'admin')
   AND NOT EXISTS (SELECT 1 FROM dbo.SYS_USER_MODULE_PERMISSION WHERE USER_ID = N'admin' AND M_IDX = 3106)
BEGIN
    INSERT INTO dbo.SYS_USER_MODULE_PERMISSION
        (USER_ID, M_IDX, EXEC_TAG, ADDNEW_TAG, DELETE_TAG, EDIT_TAG, REPORT_TAG, COST_TAG,
         SETUP_TAG, SECRECY_TAG, ENDCASE_TAG, UNENDCASE_TAG, OTHER1_TAG, OTHER2_TAG, OTHER3_TAG, OTHER4_TAG,
         DENY_VIEW_FIELD_MASTER, DENY_VIEW_FIELD_DETAIL, DENY_NEW_FIELD_MASTER, DENY_NEW_FIELD_DETAIL,
         DENY_MODI_FIELD_MASTER, DENY_MODI_FIELD_DETAIL, DATA_FILTER, OPERFLAG,
         APPROVE_TAG, DEAPPROVE_TAG, FILE_VIEW_TAG, FILE_UPDA_TAG, FILE_EDIT_TAG, FILE_DELE_TAG,
         FORM_DESIGN_TAG, MODULE_CONFIG_TAG)
    VALUES
        (N'admin', 3106, N'Z', 1, 1, 1, 1, 1,
         1, 1, 0, 0, 0, 0, 0, 0,
         N'', N'', N'', N'',
         N'', N'', N'', 1,
         0, 0, 1, 0, 0, 0,
         0, 0);

    PRINT N'== 已给 admin 授权模块 3106 ==';
END
ELSE
    PRINT N'== admin 已存在或已授权 3106，跳过 ==';

/* ---------- 2. 清理指向不存在模块的脏标记（幽灵待发布项） ---------- */
DECLARE @ghost INT = 0;
SELECT @ghost = COUNT(*)
FROM dbo.WORKBENCH_MODULE_DIRTY d
WHERE NOT EXISTS (SELECT 1 FROM dbo.MODULES m WHERE m.M_IDX = d.M_IDX);

IF @ghost > 0
BEGIN
    DELETE d
    FROM dbo.WORKBENCH_MODULE_DIRTY d
    WHERE NOT EXISTS (SELECT 1 FROM dbo.MODULES m WHERE m.M_IDX = d.M_IDX);

    PRINT N'== 已清理指向不存在模块的脏标记：' + CONVERT(nvarchar(10), @ghost) + N' 行 ==';
END
ELSE
    PRINT N'== 没有幽灵脏标记，跳过清理 ==';
