-- ============================================================================
-- EOS.ERP 迁移 025：报表过滤条件设置（2205）管理页下线（ADR-009 §11/P4）
-- ----------------------------------------------------------------------------
-- 背景（2026-08-30，ADR-009 §11 用户拍板）：
--   2205（旧 RPT/SysqrDft.aspx）维护报表查看器条件面板的默认条件定义。
--   ADR-009 §11 裁定五个报表管理页（2201-2205）全部下线：报表定义/版式/条件
--   迁往开发态资产（进 Git），不再保留运行时管理页。
--   P4 起条件定义存储已改为结构化 FILTER_TEMPLATE（迁移 024），运行时读取
--   ReportRepository.ReadConditionsAsync 仍消费 SYSQR_DEFAULT，用户填值
--   SYSQR_USER 保留为运行态。
--
-- 处理范围（对比 016 物理删除：此处数据表保留，仅撤管理页）：
--   1. MODULES 2205：M_TAG=0 隐藏菜单节点 + 清空 M_URL/NEW_URL/MODI_URL（页面路由已删）；
--   2. WORKBENCH_MODULE_DIRTY / WORKBENCH_DEFINITION_SNAPSHOT：防御性清理（2205 无工作台承载）；
--   3. SYSDD/SYSDH/SYSQR_DA/SYSQR_DEFAULT/SYSQR_USER：保留（运行时读取 + 权限单元）；
--   4. 审计/历史保持原样。
--
-- 幂等：全程以 EXISTS/IF 守卫，重复执行无副作用。
-- 命名约定（AGENTS.md 强制）：对象名全大写。
-- ============================================================================

SET NOCOUNT ON;

DECLARE @GUARD_MESSAGE NVARCHAR(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, @GUARD_MESSAGE, 1;

BEGIN TRANSACTION;

-- 1. MODULES 2205：隐藏菜单节点 + 清空页面路由（M_URL 指向已删除的 /admin/report-conditions）
UPDATE dbo.MODULES
SET M_TAG = 0,
    M_URL = NULL,
    NEW_URL = NULL,
    MODI_URL = NULL,
    REMARK = LTRIM(RTRIM(ISNULL(REMARK,''))) + N'；ADR-009 §11 下线（2026-08-30）：报表过滤条件设置管理页退役，条件定义转开发态资产（FILTER_TEMPLATE，迁移 024），SYSQR_DA/SYSQR_DEFAULT 保留供运行时读取'
WHERE M_IDX = 2205;

-- 2. WORKBENCH 快照/脏标记防御性清理（2205 已无工作台承载）
DELETE FROM dbo.WORKBENCH_MODULE_DIRTY WHERE MODULE_ID = 2205;
DELETE FROM dbo.WORKBENCH_DEFINITION_SNAPSHOT WHERE MODULE_ID = 2205;

-- 3. 审计计数留档
SELECT '2205 下线' AS KIND, COUNT(*) AS CNT FROM dbo.MODULES WHERE M_IDX = 2205 AND ISNULL(M_TAG,0) = 0;

COMMIT TRANSACTION;