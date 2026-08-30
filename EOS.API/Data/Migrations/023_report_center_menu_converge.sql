-- ADR-009 P2 入口收敛：撤 22 个 XX98 报表查询目录节点，收敛为 1 个「报表中心」
-- 策略：M_TAG=0 隐藏（导航不可见），不物理删除——MODULES 保留 M_IDX 权限单元供
--   SYSDD/SYSDH 权限行继续生效（报表中心目录 API 按 REPORT_TAG 判定可见性，
--   与 SYSDD.EXEC_TAG 无依赖）；SYSDD/SYSDH 权限行本身不动。
-- 保留证据：本迁移 = 撤节点决策史；SYSDD_REPORT override 表与 FAVORITE/LAST_RUN 列承载
--   收藏/最近使用；REPORT 表承载报表目录。
--
-- 撤节点清单 = 以 98 结尾、M_URL 为空、且有 /reports 子节点的目录节点
--   （1198/1298/1398/1498/1598/1698/1998/2098/2198/2298/2398/2498/2698/2798/2898/2998/
--    170198/170298/180198/180298/180398/180498/180698，共 23 个）。
-- 其下 /reports 子节点（报表叶子）一并隐藏，避免菜单树出现孤立叶子。

SET NOCOUNT ON;

DECLARE @GUARD_MESSAGE NVARCHAR(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, @GUARD_MESSAGE, 1;

BEGIN TRANSACTION;

-- 1. 隐藏 XX98 目录节点（M_TAG=0）
UPDATE dbo.MODULES
SET M_TAG = 0,
    REMARK = LTRIM(RTRIM(ISNULL(REMARK,''))) + N'；ADR-009 P2 撤节点（2026-08-30）：报表查询目录收敛至「报表中心」，本节点仅保留权限单元'
WHERE LTRIM(RTRIM(ISNULL(M_URL,''))) = ''
  AND RIGHT(CAST(M_IDX AS VARCHAR(20)), 2) = '98'
  AND EXISTS (SELECT 1 FROM dbo.MODULES c WHERE c.M_P_IDX = MODULES.M_IDX AND LTRIM(RTRIM(ISNULL(c.M_URL,''))) = '/reports')
  AND ISNULL(M_TAG,1) = 1;

-- 2. 隐藏其下 /reports 报表叶子（避免孤立叶子出现在菜单树）
UPDATE dbo.MODULES
SET M_TAG = 0,
    REMARK = LTRIM(RTRIM(ISNULL(REMARK,''))) + N'；ADR-009 P2 撤节点（2026-08-30）：报表查询叶子收敛至「报表中心」，本节点仅保留权限单元'
WHERE LTRIM(RTRIM(ISNULL(M_URL,''))) = '/reports'
  AND M_P_IDX IN (
    SELECT M_IDX FROM dbo.MODULES
    WHERE LTRIM(RTRIM(ISNULL(M_URL,''))) = ''
      AND RIGHT(CAST(M_IDX AS VARCHAR(20)), 2) = '98')
  AND ISNULL(M_TAG,1) = 1;

-- 3. 审计：被隐藏的 XX98 目录节点与报表叶子计数
SELECT 'XX98目录' AS KIND, COUNT(*) AS CNT FROM dbo.MODULES
WHERE LTRIM(RTRIM(ISNULL(M_URL,''))) = '' AND RIGHT(CAST(M_IDX AS VARCHAR(20)),2)='98' AND ISNULL(M_TAG,0)=0;
SELECT '报表叶子' AS KIND, COUNT(*) AS CNT FROM dbo.MODULES
WHERE LTRIM(RTRIM(ISNULL(M_URL,''))) = '/reports' AND ISNULL(M_TAG,0)=0;

COMMIT TRANSACTION;