-- 报表菜单入口收敛：承载页不再占菜单，报表入口只剩「报表中心」一个。

--
-- 依据：
--   · D4「报表不进菜单」是硬约束——报表的发现走报表中心目录 / 搜索 / 深链 / 情境入口（表单工具条），
--     不走菜单树。现存承载页节点按设计"保持隐藏、随承载页退役"，但实测仍有 3 个是可见的
--     （1310 工单库存明细表 / 139901 库存日报表 / 329801 会计日记帐明细）。
--   · 报表归属搬去业务模块（267）之后，这 3 个承载页**手里已经没有报表了**（各 0 张、条件各 0 条），
--     留着菜单入口就是引导用户点进一个空页；实测确认后才收敛，不是按编号一刀切。
--
-- 只改可见性（M_TAG=1 → 0），**不删行**：行的物理删除在收口期按删除面口径一并做，
-- 那一步不可逆，先把菜单可见性收干净、可回退。
--
-- 收敛前留底：`logs/report-carrier-menu-nodes.csv`（M_IDX / M_DESC / M_URL / M_TAG / 报表数 / 条件数）。

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @GUARD_MESSAGE NVARCHAR(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, @GUARD_MESSAGE, 1;

BEGIN TRANSACTION;

-- ---------------------------------------------------------------- ① 待收敛集合
IF OBJECT_ID('tempdb..#NODES') IS NOT NULL DROP TABLE #NODES;
CREATE TABLE #NODES (M_IDX INT NOT NULL PRIMARY KEY, M_DESC NVARCHAR(200) NOT NULL);

INSERT INTO #NODES (M_IDX, M_DESC)
SELECT m.M_IDX, LTRIM(RTRIM(ISNULL(m.M_DESC, '')))
FROM dbo.MODULES m WITH (NOLOCK)
WHERE m.M_URL = '/reports' AND ISNULL(m.M_TAG, 0) = 1;

DECLARE @Nodes INT = (SELECT COUNT(*) FROM #NODES);

-- ---------------------------------------------------------------- ② 前置：空页面才算收敛对象
-- 若某个可见承载页手上还有报表，把它藏起来就不是"收敛菜单"而是"藏掉真入口"，必须人工先处置。
IF EXISTS (
    SELECT 1 FROM #NODES n
    WHERE EXISTS (SELECT 1 FROM dbo.REPORT r WITH (NOLOCK) WHERE r.M_IDX = n.M_IDX))
    THROW 55600, N'可见的承载页菜单节点下仍挂着报表；先确认归属再收敛，中止。', 1;

IF EXISTS (
    SELECT 1 FROM #NODES n
    WHERE EXISTS (SELECT 1 FROM dbo.SYSQR_DEFAULT d WITH (NOLOCK) WHERE d.M_IDX = n.M_IDX)
       OR EXISTS (SELECT 1 FROM dbo.SYSQR_USER u WITH (NOLOCK) WHERE u.M_IDX = n.M_IDX))
    THROW 55601, N'可见的承载页菜单节点下仍留着筛选条件；先归位再收敛，中止。', 1;

-- ---------------------------------------------------------------- ③ 收敛
UPDATE m
SET m.M_TAG = 0
FROM dbo.MODULES m
INNER JOIN #NODES n ON n.M_IDX = m.M_IDX;

DECLARE @Affected INT = @@ROWCOUNT;
IF @Affected <> @Nodes
    THROW 55602, N'实际收敛的菜单节点数与待收敛集合不符，中止。', 1;

-- ---------------------------------------------------------------- ④ 不变量：不得再有可见的承载页菜单节点
IF EXISTS (SELECT 1 FROM dbo.MODULES WITH (NOLOCK) WHERE M_URL = '/reports' AND ISNULL(M_TAG, 0) = 1)
    THROW 55603, N'仍有可见的报表承载页菜单节点，中止。', 1;

DECLARE @Detail NVARCHAR(400) = STUFF((
    SELECT N'；' + CAST(n.M_IDX AS NVARCHAR(20)) + N' ' + n.M_DESC FROM #NODES n ORDER BY n.M_IDX FOR XML PATH('')), 1, 1, N'');
PRINT CONCAT(N'== 报表承载页菜单节点收敛：', @Nodes, N' 个转为隐藏（', @Detail, N'）==');

DROP TABLE #NODES;

COMMIT TRANSACTION;
