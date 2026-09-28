-- 报表承载页模块物理删除（不可逆）：23 个 XX98 目录 + 156 个 /reports 承载页 + 3 个子树内非 /reports 子节点 − 1（保留 129802）。

--
-- 为什么删：报表的归属锚点已经搬到业务模块（267/268/269），承载页不再是任何报表、条件、权限的锚点。
-- 留着它们的代价不是"占位"，而是每个承载页都还挂在菜单树/模块树上，会被当成可授权的模块继续流通。
--
-- 删除集（182 个候选，实删 181）：
--   A. 23 个 XX98 目录（地址为空且编号尾数为 98）
--   B. 156 个 /reports 承载页（含子树内 153 个与子树外 3 个：1310 / 139901 / 329801）
--   C. 3 个子树内非 /reports 子节点（其中 2 个地址为空、1 个是 /bom-expand）
--   保留：129802（`/bom-expand`，全库唯一承载 BOM 展开的模块，菜单可见 M_TAG=1，不得删）
--
-- 前置（任一不满足即中止，不"尽力而为"）：
--   ① 删除集规模必须恰为 181，候选恰为 182，且 129802 是唯一保留项；
--   ② 权威引用面必须**全为 0**：`REPORT.M_IDX`、`SYSQR_DEFAULT/USER/DA.M_IDX`、`SYSDD_REPORT.M_IDX`、
--      `FIELDS.BROWSE_M_IDX`、`MODULE_*`、`ATTACHMENT/TASK/WF_APPROVE/REPORT_INBOX/REPORT_SUBSCRIPTION`。
--      有引用说明归位没做干净，先补归位再删——带着引用删就会制造悬空。
--
-- 残余清理（按 `logs/adr024-r8-cleanup.csv` 工作单，实测 790 行）：
--   · `REPORT.R_M_IDX`/`Q_M_IDX`：**改指该行自己的 `M_IDX`**（不是删报表）。这两列是历史命名，
--     业务上等价于"报表挂在哪个模块"，指着一个即将消失的模块没有意义；退役列本身在收口期做。
--   · `SYSDD`/`SYSDH`/`SYSDD_BUTTON`/`SYSDH_BUTTON`/`WORKBENCH_MODULE_DIRTY`/`SYSQR(R_M_IDX)`/`SYSDF`：
--     指向承载页的行直接删（这些行描述的都是"在承载页上的权限/脏标记/打印偏好"，承载页没了它们没有落点）。
--   · `AUDIT_EVENT` 保留不删（审计留痕）；`MODULES_Backup_ADR004` / `FIELDS_GHOST_BAK_20260909`
--     是历史备份表，按约定不清理但不得作为参考。
--
-- 留底：`logs/adr024-r8-modules-backup.csv`（182 行原始 MODULES 行，含处置列）。

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @GUARD NVARCHAR(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, @GUARD, 1;

BEGIN TRANSACTION;

-- ---------------------------------------------------------------- ① 重算删除集
IF OBJECT_ID('tempdb..#DEL') IS NOT NULL DROP TABLE #DEL;
CREATE TABLE #DEL (M_IDX INT NOT NULL PRIMARY KEY);

INSERT INTO #DEL (M_IDX)
SELECT m.M_IDX
FROM dbo.MODULES m WITH (NOLOCK)
WHERE (LTRIM(RTRIM(ISNULL(m.M_URL, ''))) = '/reports'
    OR (LTRIM(RTRIM(ISNULL(m.M_URL, ''))) = '' AND RIGHT(LTRIM(RTRIM(CAST(m.M_IDX AS varchar(20)))), 2) = '98')
    OR (LTRIM(RTRIM(ISNULL(m.M_URL, ''))) <> '/reports'
        AND EXISTS (SELECT 1 FROM dbo.MODULES p WITH (NOLOCK)
                    WHERE p.M_IDX = m.M_P_IDX
                      AND LTRIM(RTRIM(ISNULL(p.M_URL, ''))) = ''
                      AND RIGHT(LTRIM(RTRIM(CAST(p.M_IDX AS varchar(20)))), 2) = '98')))
  AND m.M_IDX <> 129802;

DECLARE @Del INT = (SELECT COUNT(*) FROM #DEL);
IF @Del <> 181
    THROW 55610, N'删除集规模与留底不符（期望 181）；口径变了要先人工确认，中止。', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.MODULES WITH (NOLOCK) WHERE M_IDX = 129802)
    THROW 55611, N'必须保留的 129802 不存在；删除集口径已变，中止。', 1;

IF EXISTS (SELECT 1 FROM #DEL d WHERE d.M_IDX = 129802)
    THROW 55612, N'129802 被算进删除集；中止。', 1;

-- ---------------------------------------------------------------- ② 前置：权威引用面必须全为 0
IF EXISTS (SELECT 1 FROM dbo.REPORT r WITH (NOLOCK) WHERE EXISTS (SELECT 1 FROM #DEL d WHERE d.M_IDX = r.M_IDX))
    THROW 55613, N'仍有报表的归属模块在删除集里；先补归位，中止。', 1;
IF EXISTS (SELECT 1 FROM dbo.SYSQR_DEFAULT x WITH (NOLOCK) WHERE EXISTS (SELECT 1 FROM #DEL d WHERE d.M_IDX = x.M_IDX))
    THROW 55614, N'仍有筛选条件落在删除集模块上；先归位，中止。', 1;
IF EXISTS (SELECT 1 FROM dbo.SYSQR_USER x WITH (NOLOCK) WHERE EXISTS (SELECT 1 FROM #DEL d WHERE d.M_IDX = x.M_IDX))
    THROW 55615, N'仍有用户填值落在删除集模块上；先归位，中止。', 1;
IF EXISTS (SELECT 1 FROM dbo.SYSQR_DA x WITH (NOLOCK) WHERE EXISTS (SELECT 1 FROM #DEL d WHERE d.M_IDX = x.M_IDX))
    THROW 55616, N'仍有条件定义落在删除集模块上；先归位，中止。', 1;
IF EXISTS (SELECT 1 FROM dbo.SYSDD_REPORT x WITH (NOLOCK) WHERE EXISTS (SELECT 1 FROM #DEL d WHERE d.M_IDX = x.M_IDX))
    THROW 55617, N'仍有报表权限例外行落在删除集模块上；先归位，中止。', 1;
IF EXISTS (SELECT 1 FROM dbo.FIELDS f WITH (NOLOCK) WHERE EXISTS (SELECT 1 FROM #DEL d WHERE d.M_IDX = f.BROWSE_M_IDX))
    THROW 55618, N'仍有字段浏览指向删除集模块；先改指，中止。', 1;
IF EXISTS (SELECT 1 FROM dbo.MODULE_FORM_LAYOUT x WITH (NOLOCK) WHERE EXISTS (SELECT 1 FROM #DEL d WHERE d.M_IDX = x.M_IDX))
    THROW 55619, N'仍有模块版式落在删除集模块上；先处置，中止。', 1;
IF EXISTS (SELECT 1 FROM dbo.MODULE_FORM_TAB x WITH (NOLOCK) WHERE EXISTS (SELECT 1 FROM #DEL d WHERE d.M_IDX = x.M_IDX))
    THROW 55620, N'仍有模块页签配置落在删除集模块上；先处置，中止。', 1;
IF EXISTS (SELECT 1 FROM dbo.ATTACHMENT x WITH (NOLOCK) WHERE EXISTS (SELECT 1 FROM #DEL d WHERE d.M_IDX = x.M_IDX))
    THROW 55621, N'删除集模块上仍挂着附件；先处置，中止。', 1;
IF EXISTS (SELECT 1 FROM dbo.TASK x WITH (NOLOCK) WHERE EXISTS (SELECT 1 FROM #DEL d WHERE d.M_IDX = x.M_IDX))
    THROW 55622, N'删除集模块上仍挂着任务；先处置，中止。', 1;
IF EXISTS (SELECT 1 FROM dbo.WF_APPROVE x WITH (NOLOCK) WHERE EXISTS (SELECT 1 FROM #DEL d WHERE d.M_IDX = x.M_IDX))
    THROW 55623, N'删除集模块上仍挂着审批记录；先处置，中止。', 1;
IF EXISTS (SELECT 1 FROM dbo.REPORT_INBOX x WITH (NOLOCK) WHERE EXISTS (SELECT 1 FROM #DEL d WHERE d.M_IDX = x.M_IDX))
    THROW 55624, N'删除集模块上仍挂着报表订阅收件箱行；先处置，中止。', 1;
IF EXISTS (SELECT 1 FROM dbo.REPORT_SUBSCRIPTION x WITH (NOLOCK) WHERE EXISTS (SELECT 1 FROM #DEL d WHERE d.M_IDX = x.M_IDX))
    THROW 55625, N'删除集模块上仍挂着报表订阅；先处置，中止。', 1;

-- ---------------------------------------------------------------- ②b 悬空引用的棘轮基线
-- 库里**本来就存在**与本次删除无关的悬空引用（实测：字段浏览 161 行、筛选条件 1 行指向 99000001）。
-- 那些不是本次任务的对象，也不该被"顺手"抹掉——所以这里不要求"零悬空"，而是按棘轮口径：
-- 删完之后悬空**只允许减少、不允许增加**。新增任何一条都说明删除动作制造了新的悬空。
DECLARE @DangleReportBefore INT = (SELECT COUNT(*) FROM dbo.REPORT r WITH (NOLOCK)
    WHERE NOT EXISTS (SELECT 1 FROM dbo.MODULES m WITH (NOLOCK) WHERE m.M_IDX = r.M_IDX));
DECLARE @DangleCondBefore INT = (SELECT COUNT(*) FROM dbo.SYSQR_DEFAULT x WITH (NOLOCK)
    WHERE NOT EXISTS (SELECT 1 FROM dbo.MODULES m WITH (NOLOCK) WHERE m.M_IDX = x.M_IDX));
DECLARE @DangleCondUserBefore INT = (SELECT COUNT(*) FROM dbo.SYSQR_USER x WITH (NOLOCK)
    WHERE NOT EXISTS (SELECT 1 FROM dbo.MODULES m WITH (NOLOCK) WHERE m.M_IDX = x.M_IDX));
DECLARE @DangleExceptBefore INT = (SELECT COUNT(*) FROM dbo.SYSDD_REPORT x WITH (NOLOCK)
    WHERE NOT EXISTS (SELECT 1 FROM dbo.MODULES m WITH (NOLOCK) WHERE m.M_IDX = x.M_IDX));
DECLARE @DangleBrowseBefore INT = (SELECT COUNT(*) FROM dbo.FIELDS f WITH (NOLOCK)
    WHERE ISNULL(f.BROWSE_M_IDX, 0) <> 0
      AND NOT EXISTS (SELECT 1 FROM dbo.MODULES m WITH (NOLOCK) WHERE m.M_IDX = f.BROWSE_M_IDX));

-- ---------------------------------------------------------------- ③ 残余清理
-- FK 先解（这两张表有指向 MODULES 的外键）
DELETE x FROM dbo.MODULE_BUSINESS_ACTION x WHERE EXISTS (SELECT 1 FROM #DEL d WHERE d.M_IDX = x.M_IDX);
DELETE x FROM dbo.MODULE_VALIDATION_RULE x WHERE EXISTS (SELECT 1 FROM #DEL d WHERE d.M_IDX = x.M_IDX);

-- 报表的历史锚点列改指自己当前的归属模块（不删报表行）
DECLARE @Repointed INT = 0;
UPDATE r
SET r.R_M_IDX = r.M_IDX
FROM dbo.REPORT r
WHERE EXISTS (SELECT 1 FROM #DEL d WHERE d.M_IDX = r.R_M_IDX);
SET @Repointed = @Repointed + @@ROWCOUNT;

UPDATE r
SET r.Q_M_IDX = r.M_IDX
FROM dbo.REPORT r
WHERE EXISTS (SELECT 1 FROM #DEL d WHERE d.M_IDX = r.Q_M_IDX);
SET @Repointed = @Repointed + @@ROWCOUNT;

-- 落在承载页上的权限/脏标记/打印偏好行：直接删（承载页没了，这些行没有落点）
DECLARE @Purged INT = 0;
DELETE x FROM dbo.SYSDD x WHERE EXISTS (SELECT 1 FROM #DEL d WHERE d.M_IDX = x.M_IDX);
SET @Purged = @Purged + @@ROWCOUNT;
DELETE x FROM dbo.SYSDH x WHERE EXISTS (SELECT 1 FROM #DEL d WHERE d.M_IDX = x.M_IDX);
SET @Purged = @Purged + @@ROWCOUNT;
DELETE x FROM dbo.SYSDD_BUTTON x WHERE EXISTS (SELECT 1 FROM #DEL d WHERE d.M_IDX = x.M_IDX);
SET @Purged = @Purged + @@ROWCOUNT;
DELETE x FROM dbo.SYSDH_BUTTON x WHERE EXISTS (SELECT 1 FROM #DEL d WHERE d.M_IDX = x.M_IDX);
SET @Purged = @Purged + @@ROWCOUNT;
DELETE x FROM dbo.WORKBENCH_MODULE_DIRTY x WHERE EXISTS (SELECT 1 FROM #DEL d WHERE d.M_IDX = x.M_IDX);
SET @Purged = @Purged + @@ROWCOUNT;
DELETE x FROM dbo.SYSQR x WHERE EXISTS (SELECT 1 FROM #DEL d WHERE d.M_IDX = x.R_M_IDX);
SET @Purged = @Purged + @@ROWCOUNT;
DELETE x FROM dbo.SYSDF x WHERE EXISTS (SELECT 1 FROM #DEL d WHERE d.M_IDX = x.M_IDX);
SET @Purged = @Purged + @@ROWCOUNT;

-- ---------------------------------------------------------------- ④ 删模块（子节点先删，目录后删）
DECLARE @ModulesBefore INT = (SELECT COUNT(*) FROM dbo.MODULES WITH (NOLOCK));

DELETE m FROM dbo.MODULES m
INNER JOIN #DEL d ON d.M_IDX = m.M_IDX
WHERE LTRIM(RTRIM(ISNULL(m.M_URL, ''))) <> '';   -- 先删有地址的（承载页/子节点）

DELETE m FROM dbo.MODULES m
INNER JOIN #DEL d ON d.M_IDX = m.M_IDX;          -- 再删目录

DECLARE @ModulesAfter INT = (SELECT COUNT(*) FROM dbo.MODULES WITH (NOLOCK));
IF @ModulesBefore - @ModulesAfter <> 181
    THROW 55630, N'实际删除的模块行数与删除集不符，中止。', 1;

-- ---------------------------------------------------------------- ⑤ 后置不变量
IF EXISTS (SELECT 1 FROM dbo.MODULES WITH (NOLOCK) WHERE LTRIM(RTRIM(ISNULL(M_URL, ''))) = '/reports')
    THROW 55631, N'仍存在 /reports 型模块，中止。', 1;

IF EXISTS (SELECT 1 FROM dbo.MODULES m WITH (NOLOCK) WHERE EXISTS (SELECT 1 FROM #DEL d WHERE d.M_IDX = m.M_IDX))
    THROW 55632, N'删除集里仍有模块残留，中止。', 1;

IF EXISTS (SELECT 1 FROM dbo.MODULES WITH (NOLOCK)
           WHERE M_URL = '/reports' AND ISNULL(M_TAG, 0) = 1)
    THROW 55633, N'仍存在可见的承载页菜单节点，中止。', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.MODULES WITH (NOLOCK) WHERE M_IDX = 129802 AND ISNULL(M_TAG, 0) = 1)
    THROW 55634, N'129802 的菜单入口丢失；中止。', 1;

-- 棘轮：悬空引用只允许减少
DECLARE @DangleReportAfter INT = (SELECT COUNT(*) FROM dbo.REPORT r WITH (NOLOCK)
    WHERE NOT EXISTS (SELECT 1 FROM dbo.MODULES m WITH (NOLOCK) WHERE m.M_IDX = r.M_IDX));
DECLARE @DangleCondAfter INT = (SELECT COUNT(*) FROM dbo.SYSQR_DEFAULT x WITH (NOLOCK)
    WHERE NOT EXISTS (SELECT 1 FROM dbo.MODULES m WITH (NOLOCK) WHERE m.M_IDX = x.M_IDX));
DECLARE @DangleCondUserAfter INT = (SELECT COUNT(*) FROM dbo.SYSQR_USER x WITH (NOLOCK)
    WHERE NOT EXISTS (SELECT 1 FROM dbo.MODULES m WITH (NOLOCK) WHERE m.M_IDX = x.M_IDX));
DECLARE @DangleExceptAfter INT = (SELECT COUNT(*) FROM dbo.SYSDD_REPORT x WITH (NOLOCK)
    WHERE NOT EXISTS (SELECT 1 FROM dbo.MODULES m WITH (NOLOCK) WHERE m.M_IDX = x.M_IDX));
DECLARE @DangleBrowseAfter INT = (SELECT COUNT(*) FROM dbo.FIELDS f WITH (NOLOCK)
    WHERE ISNULL(f.BROWSE_M_IDX, 0) <> 0
      AND NOT EXISTS (SELECT 1 FROM dbo.MODULES m WITH (NOLOCK) WHERE m.M_IDX = f.BROWSE_M_IDX));

IF @DangleReportAfter > @DangleReportBefore
    THROW 55635, N'报表归属出现新的悬空引用，中止。', 1;
IF @DangleCondAfter > @DangleCondBefore
    THROW 55636, N'筛选条件出现新的悬空引用，中止。', 1;
IF @DangleCondUserAfter > @DangleCondUserBefore
    THROW 55636, N'用户填值出现新的悬空引用，中止。', 1;
IF @DangleExceptAfter > @DangleExceptBefore
    THROW 55637, N'报表权限例外行出现新的悬空引用，中止。', 1;
IF @DangleBrowseAfter > @DangleBrowseBefore
    THROW 55638, N'字段浏览出现新的悬空引用，中止。', 1;

PRINT CONCAT(N'== 报表承载页模块删除：实删 ', @Del, N' 个；报表历史锚点改指 ', @Repointed,
             N' 行；权限/脏标记/打印偏好清理 ', @Purged, N' 行；MODULES ', @ModulesBefore, N' -> ', @ModulesAfter, N' ==');
PRINT CONCAT(N'== 悬空引用棘轮（本次未新增）：报表 ', @DangleReportBefore, N'->', @DangleReportAfter,
             N'；条件 ', @DangleCondBefore, N'->', @DangleCondAfter,
             N'；用户填值 ', @DangleCondUserBefore, N'->', @DangleCondUserAfter,
             N'；例外行 ', @DangleExceptBefore, N'->', @DangleExceptAfter,
             N'；字段浏览 ', @DangleBrowseBefore, N'->', @DangleBrowseAfter, N' ==');

DROP TABLE #DEL;

COMMIT TRANSACTION;
