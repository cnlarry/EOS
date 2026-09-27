-- Report center menu convergence: hide 23 XX98 report query directory nodes, converge to 1 "Report Center".
-- Strategy: M_TAG=0 to hide (navigation invisible), not physically deleted — MODULES keeps M_IDX
-- permission units for SYSDD/SYSDH permission rows to remain valid (the report center catalog API
-- judges visibility by REPORT_TAG, not EXEC_TAG). SYSDD/SYSDH permission rows are untouched.
-- Hidden nodes: all XX98-ending nodes with M_URL empty and /reports child nodes.
-- Their /reports leaf nodes are also hidden to avoid orphan leaves in the menu tree.

SET NOCOUNT ON;

DECLARE @GUARD_MESSAGE NVARCHAR(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, @GUARD_MESSAGE, 1;

BEGIN TRANSACTION;

-- 1. 隐藏 XX98 目录节点（M_TAG=0）
UPDATE dbo.MODULES
SET M_TAG = 0,
    REMARK = LTRIM(RTRIM(ISNULL(REMARK,''))) + N'；P2 撤节点（2026-08-30）：报表查询目录收敛至「报表中心」，本节点仅保留权限单元'
WHERE LTRIM(RTRIM(ISNULL(M_URL,''))) = ''
  AND RIGHT(CAST(M_IDX AS VARCHAR(20)), 2) = '98'
  AND EXISTS (SELECT 1 FROM dbo.MODULES c WHERE c.M_P_IDX = MODULES.M_IDX AND LTRIM(RTRIM(ISNULL(c.M_URL,''))) = '/reports')
  AND ISNULL(M_TAG,1) = 1;

-- 2. 隐藏其下 /reports 报表叶子（避免孤立叶子出现在菜单树）
UPDATE dbo.MODULES
SET M_TAG = 0,
    REMARK = LTRIM(RTRIM(ISNULL(REMARK,''))) + N'；P2 撤节点（2026-08-30）：报表查询叶子收敛至「报表中心」，本节点仅保留权限单元'
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