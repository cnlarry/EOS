-- ============================================================================
-- EOS.ERP migration 461：评估用例模块的版式设计权限
-- ----------------------------------------------------------------------------
--  458 建了模块 3106（统一工作台），459 只给了 admin 基础授权。要让它的表单版式
--  能被保存（从而持有快照时也有版式行，check-form-layout 要求"持快照的模块必须有版式行"），
--  admin 还需要 FORM_DESIGN_TAG（版式设计位）与 MODULE_CONFIG_TAG（模块配置位）。
--
--  只调整 admin 在 3106 上的这两个位；其它账号的授权仍由管理员在 2306 分配。
-- ============================================================================
SET NOCOUNT ON;
SET XACT_ABORT ON;

IF DB_NAME() <> N'EOS.ERP'
    THROW 51960, N'本脚本只允许在 EOS.ERP 数据库上执行。', 1;

UPDATE dbo.SYS_USER_MODULE_PERMISSION
SET FORM_DESIGN_TAG = 1,
    MODULE_CONFIG_TAG = 1
WHERE USER_ID = N'admin'
  AND M_IDX = 3106
  AND (ISNULL(FORM_DESIGN_TAG, 0) = 0 OR ISNULL(MODULE_CONFIG_TAG, 0) = 0);

PRINT N'== 3106 的版式设计位已就绪（admin） ==';
