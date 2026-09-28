-- 报表级例外行的锚点归位：从**报表承载页模块**改指**报表归属业务模块**。

--
-- 背景：报表可见性有两层，两层都按"某个模块号"取——
--   ① 模块级闸门 SYSDD.REPORT_TAG（个人行存在即覆盖）否则 SYSDH.REPORT_TAG（组 OR）；
--   ② 报表级例外 SYSDD_REPORT / SYSDH_REPORT，按 (用户, 模块号, 报表号) 命中才生效。
-- 报表归属搬去业务模块（267）之后，第 ① 层已经按归属模块取；第 ② 层的行还留在承载页上，
-- 于是这些行**永远命中不了**：既不再收紧可见（PREVIEW_TAG=0 的排除失效，报表反而被放出来），
-- 也不再收窄行（DATA_FILTER 失效，明细行反而更多）。本脚本把第 ② 层的锚点一起搬到归属模块。
--
-- 口径：
--   · 逐步重指 236 行（实测 226 行"三列全 1 且无行级过滤"= 相对默认全开无差别，10 行有语义）；
--   · **撞键即中止**：目标键 (用户, 归属模块, 报表) 上若已有行，语义冲突（谁盖谁）需人工裁决，不猜；
--   · 报表号对不上任何 REPORT 行的例外行一并中止——重指会静默漏掉它们，那等于悄悄放宽；
--   · 收尾断言"例外行锚点 = 报表归属模块"，把不变量钉死，后续阶段可直接依赖它。
--
-- 重指前后逐人可见集合的实测：`logs/report-visibility-diff.csv`。
-- 例外行归位**不改变可见集合**（中间态与目标态逐格相同），它改变的是：
--   · 4 行 PREVIEW_TAG=0 重新咬合（这 4 行的账号在归属模块上闸门本就关着，故集合无变化）；
--   · 4 行 DATA_FILTER 重新咬合（aaron 2 张、lesson / long 各 1 张——闸门在归属模块上是开的，
--     归位后这些账号在报表里看到的**行**变少，正是这些例外行本来的用意）。
-- 重指前 236 行留底：`logs/report-r2-sysdd-report-repoint.csv`。

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @GUARD_MESSAGE NVARCHAR(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, @GUARD_MESSAGE, 1;

BEGIN TRANSACTION;

-- ---------------------------------------------------------------- ① 待重指集合
IF OBJECT_ID('tempdb..#MOVE') IS NOT NULL DROP TABLE #MOVE;
CREATE TABLE #MOVE (
    USER_ID   NVARCHAR(50)  NOT NULL,
    OLD_IDX   INT           NOT NULL,
    NEW_IDX   INT           NOT NULL,
    REPORT_ID NVARCHAR(100) NOT NULL,
    HAD_DATA  BIT           NOT NULL,
    HAD_HIDE  BIT           NOT NULL,
    PRIMARY KEY (USER_ID, OLD_IDX, REPORT_ID)
);

INSERT INTO #MOVE (USER_ID, OLD_IDX, NEW_IDX, REPORT_ID, HAD_DATA, HAD_HIDE)
SELECT LTRIM(RTRIM(d.USER_ID)), d.M_IDX, r.M_IDX, LTRIM(RTRIM(d.REPORT_ID)),
       CASE WHEN ISNULL(LTRIM(RTRIM(d.DATA_FILTER)), '') = '' THEN 0 ELSE 1 END,
       CASE WHEN ISNULL(d.PREVIEW_TAG, 0) = 0 THEN 1 ELSE 0 END
FROM dbo.SYSDD_REPORT d WITH (NOLOCK)
INNER JOIN dbo.REPORT r WITH (NOLOCK) ON LTRIM(RTRIM(r.REPORT_ID)) = LTRIM(RTRIM(d.REPORT_ID))
WHERE d.M_IDX = r.R_M_IDX
  AND r.M_IDX <> r.R_M_IDX;

DECLARE @Moved INT = (SELECT COUNT(*) FROM #MOVE);
DECLARE @Users INT = (SELECT COUNT(DISTINCT USER_ID) FROM #MOVE);
DECLARE @WithFilter INT = (SELECT COUNT(*) FROM #MOVE WHERE HAD_DATA = 1);
DECLARE @WithHide INT = (SELECT COUNT(*) FROM #MOVE WHERE HAD_HIDE = 1);

-- ---------------------------------------------------------------- ② 前置检查：撞键、目标模块、孤儿行
IF EXISTS (
    SELECT 1 FROM #MOVE m
    WHERE EXISTS (SELECT 1 FROM dbo.SYSDD_REPORT t WITH (NOLOCK)
                  WHERE LTRIM(RTRIM(t.USER_ID)) = m.USER_ID
                    AND t.M_IDX = m.NEW_IDX
                    AND LTRIM(RTRIM(t.REPORT_ID)) = m.REPORT_ID))
    THROW 55500, N'重指目标键上已存在同账号同报表的例外行，谁盖谁需人工裁决，中止。', 1;

IF EXISTS (
    SELECT 1 FROM #MOVE m
    WHERE NOT EXISTS (SELECT 1 FROM dbo.MODULES x WITH (NOLOCK) WHERE x.M_IDX = m.NEW_IDX))
    THROW 55501, N'重指目标模块在 MODULES 中不存在，中止。', 1;

IF EXISTS (
    SELECT 1 FROM dbo.SYSDD_REPORT d WITH (NOLOCK)
    WHERE NOT EXISTS (SELECT 1 FROM dbo.REPORT r WITH (NOLOCK)
                      WHERE LTRIM(RTRIM(r.REPORT_ID)) = LTRIM(RTRIM(d.REPORT_ID))))
    THROW 55502, N'存在报表号对不上任何 REPORT 行的例外行；重指会静默漏掉它，等于悄悄放宽，中止。', 1;

-- ---------------------------------------------------------------- ③ 重指
UPDATE d
SET d.M_IDX = m.NEW_IDX
FROM dbo.SYSDD_REPORT d
INNER JOIN #MOVE m
        ON LTRIM(RTRIM(d.USER_ID)) = m.USER_ID
       AND d.M_IDX = m.OLD_IDX
       AND LTRIM(RTRIM(d.REPORT_ID)) = m.REPORT_ID;

DECLARE @Affected INT = @@ROWCOUNT;
IF @Affected <> @Moved
    THROW 55503, N'实际重指行数与待重指集合不符，中止。', 1;

-- ---------------------------------------------------------------- ④ 不变量：例外行锚点 = 报表归属模块
IF EXISTS (
    SELECT 1 FROM dbo.SYSDD_REPORT d WITH (NOLOCK)
    INNER JOIN dbo.REPORT r WITH (NOLOCK) ON LTRIM(RTRIM(r.REPORT_ID)) = LTRIM(RTRIM(d.REPORT_ID))
    WHERE d.M_IDX <> r.M_IDX)
    THROW 55504, N'仍有例外行锚点不等于报表归属模块，中止。', 1;

-- SYSDH_REPORT 组例外行同口径（实测 0 行；有行才检查，退役并入收口期）
IF OBJECT_ID('dbo.SYSDH_REPORT') IS NOT NULL
BEGIN
    IF EXISTS (
        SELECT 1 FROM dbo.SYSDH_REPORT g WITH (NOLOCK)
        INNER JOIN dbo.REPORT r WITH (NOLOCK) ON LTRIM(RTRIM(r.REPORT_ID)) = LTRIM(RTRIM(g.REPORT_ID))
        WHERE g.M_IDX <> r.M_IDX)
        THROW 55505, N'组例外行锚点不等于报表归属模块，中止。', 1;
END

PRINT CONCAT(N'== 报表例外行锚点归位：重指 ', @Moved, N' 行（涉及 ', @Users,
             N' 个账号；其中含行级过滤 ', @WithFilter, N' 行、含隐藏 ', @WithHide, N' 行）==');

DROP TABLE #MOVE;

COMMIT TRANSACTION;
