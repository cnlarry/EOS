-- ============================================================================
-- EOS.ERP 迁移 016：数据表数据维护模块下线（2310/2312 物理删除，EOS-23）
-- ----------------------------------------------------------------------------
-- 库：EOS.ERP（全新系统的唯一业务数据库）
--
-- 背景（2026-08-28 用户拍板，决策清单 #37）：
--   旧系统 2310（MagTableData：整表 TRUNCATE）与 2312（MagTableData2：按
--   CREATE_DATE/CLIENT_ID/SUPPLIER_ID 区间清历史 + FK_T_ID_1..5 子表孤儿清理）
--   是两个数据清理工具。现代重构明确不移植任意表破坏性清理：
--     ① 界面内「任选表→清空/删除」违背项目安全边界精神（动态表名 + 不可逆
--        破坏性操作），清库属实施期/DBA 操作，走脚本 + 备份；
--     ② 现代受控只读浏览页（/admin/table-data）直接 SELECT 物理列原始行，
--        是全系统唯一绕过成本/保密/禁止查看字段过滤与 EXEC_TAG/DATA_FILTER
--        数据范围的原始数据窗口，随页一并删除；
--     ③ 业务数据查看走各模块工作台（完整字段权限 + 数据范围）；
--       表结构巡检由 2302（数据表、字段维护）/ 2303（字段审计）承担。
--   用户拍板：2310/2312 整体下线、物理删除，无承接模块（权限直接删除）。
--
-- 处理范围：
--   1. SYSDD / SYSDH：删除 2310/2312 权限行（无承接模块，不合并）；
--   2. WORKBENCH_MODULE_DIRTY / WORKBENCH_DEFINITION_SNAPSHOT：防御性清理；
--   3. MODULES：2310/2312 物理删除；
--   4. 审计/历史（SYSDF、AUDIT_EVENT）保持原样不动（追溯），仅打印计数留档。
--
-- 幂等：全程以 EXISTS/IF 守卫，重复执行无副作用。
-- 命名约定（AGENTS.md 强制）：对象名全大写。
-- ============================================================================

SET NOCOUNT ON;

DECLARE @GUARD_MESSAGE NVARCHAR(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, @GUARD_MESSAGE, 1;

BEGIN TRANSACTION;

DECLARE @MIG NVARCHAR(20) = N'EOS-23';

-- 1. SYSDD：删除用户个人权限行（无承接模块，直接删除不合并）
DELETE FROM dbo.SYSDD WHERE M_IDX IN (2310, 2312);
PRINT N'[EOS-23] SYSDD 2310/2312 权限行删除：' + CAST(@@ROWCOUNT AS NVARCHAR(10)) + N' 行';

-- 2. SYSDH：删除用户组权限行（同上）
DELETE FROM dbo.SYSDH WHERE M_IDX IN (2310, 2312);
PRINT N'[EOS-23] SYSDH 2310/2312 权限行删除：' + CAST(@@ROWCOUNT AS NVARCHAR(10)) + N' 行';

-- 3. WORKBENCH 脏标记 / Definition 快照防御性清理（2310/2312 非工作台模块，通常为 0 行）
IF OBJECT_ID('dbo.WORKBENCH_MODULE_DIRTY','U') IS NOT NULL
BEGIN
    DELETE FROM dbo.WORKBENCH_MODULE_DIRTY WHERE MODULE_ID IN (2310, 2312);
    PRINT N'[EOS-23] WORKBENCH_MODULE_DIRTY 2310/2312 清理：' + CAST(@@ROWCOUNT AS NVARCHAR(10)) + N' 行';
END

IF OBJECT_ID('dbo.WORKBENCH_DEFINITION_SNAPSHOT','U') IS NOT NULL
BEGIN
    DELETE FROM dbo.WORKBENCH_DEFINITION_SNAPSHOT WHERE MODULE_ID IN (2310, 2312);
    PRINT N'[EOS-23] WORKBENCH_DEFINITION_SNAPSHOT 2310/2312 清理：' + CAST(@@ROWCOUNT AS NVARCHAR(10)) + N' 行';
END

-- 4. MODULES：2310/2312 物理删除（父目录 2311 下仍有 2302/2303/2307，不空组）
DELETE FROM dbo.MODULES WHERE M_IDX IN (2310, 2312);
PRINT N'[EOS-23] MODULES 2310/2312 物理删除完成。';

-- 5. 留档：审计/历史不物理删除（保持追溯），仅输出计数
DECLARE @SYSDF_CNT INT = 0;
IF OBJECT_ID('dbo.SYSDF','U') IS NOT NULL
    SELECT @SYSDF_CNT = COUNT_BIG(1) FROM dbo.SYSDF WITH (NOLOCK) WHERE M_IDX IN (2310, 2312);
PRINT N'[EOS-23] 历史审计 SYSDF.M_IDX IN (2310,2312) 保留 ' + CAST(@SYSDF_CNT AS NVARCHAR(10)) + N' 行（不删除，供追溯）';

DECLARE @AUDIT_CNT INT = 0;
IF OBJECT_ID('dbo.AUDIT_EVENT','U') IS NOT NULL
    SELECT @AUDIT_CNT = COUNT_BIG(1) FROM dbo.AUDIT_EVENT WITH (NOLOCK) WHERE MODULE_ID IN (2310, 2312);
PRINT N'[EOS-23] 历史审计 AUDIT_EVENT.MODULE_ID IN (2310,2312) 保留 ' + CAST(@AUDIT_CNT AS NVARCHAR(10)) + N' 行（不删除，供追溯）';

COMMIT TRANSACTION;
PRINT N'[EOS-23] 数据表数据维护模块（2310/2312）下线完成（物理删除，页面/端点/路由代码随本次提交移除）。';
