-- 每个模块只保留一张默认报表：把"哪个偏好本来是本模块的报表"从排序规则落成数据。

--
-- 背景：
--   取报表的三处（报表定义/排序上下文、打印面板预选、单据打印默认页头页脚）在"同模块存在多张
--   `REPORT.IS_DEFAULT=1`"时，用 `CASE WHEN R_M_IDX=M_IDX THEN 0 ELSE 1 END` 破除并列——
--   那是归属归位过渡期的稳定器：归位把原挂承载页的报表并进业务模块，若直接按编号排序，
--   "默认打开的那张报表"和"默认页头页脚"会换人，属于与归位无关的行为变化。
--
--   过渡期已经结束（承载页模块已删除、`R_M_IDX` 即将退役），这条排序规则失去了依据。
--   与其在删列时把排序一起删掉（等于静默换掉用户看到的那张默认报表），
--   不如**按当前并列结果**把多余默认降级：决策落成数据，语义变成可读可查的
--   "每个模块至多一张默认报表"，此后排序无关紧要。
--
-- 前置：逐模块复算并列赢家（同一条规则）；赢家不唯一即中止。
-- 留底：`logs/report-default-dedup.csv`（被降级的行 + 保留的赢家）。

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @GUARD_MESSAGE NVARCHAR(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, @GUARD_MESSAGE, 1;

BEGIN TRANSACTION;

-- ---------------------------------------------------------------- ① 并列赢家
IF OBJECT_ID('tempdb..#WIN') IS NOT NULL DROP TABLE #WIN;
CREATE TABLE #WIN (M_IDX INT NOT NULL PRIMARY KEY, REPORT_ID NVARCHAR(100) NOT NULL, Defaults INT NOT NULL);

INSERT INTO #WIN (M_IDX, REPORT_ID, Defaults)
SELECT g.M_IDX,
       (SELECT TOP 1 x.REPORT_ID FROM dbo.REPORT x WITH (NOLOCK)
        WHERE x.M_IDX = g.M_IDX AND ISNULL(x.IS_DEFAULT, 0) = 1
        ORDER BY CASE WHEN ISNULL(x.R_M_IDX, 0) = x.M_IDX THEN 0 ELSE 1 END, x.REPORT_ID),
       g.Defaults
FROM (SELECT M_IDX, COUNT(*) AS Defaults FROM dbo.REPORT WITH (NOLOCK)
      WHERE ISNULL(IS_DEFAULT, 0) = 1 GROUP BY M_IDX HAVING COUNT(*) > 1) g;

DECLARE @Groups INT = (SELECT COUNT(*) FROM #WIN);
DECLARE @Losers INT = (SELECT ISNULL(SUM(Defaults), 0) FROM #WIN) - @Groups;

-- 前置：赢家必须能唯一确定（复算不到赢家说明数据自相矛盾，先停下）
IF EXISTS (SELECT 1 FROM #WIN w WHERE w.REPORT_ID IS NULL OR LTRIM(RTRIM(w.REPORT_ID)) = N'')
    THROW 55700, N'存在多默认模块复算不出赢家，中止。', 1;

IF EXISTS (SELECT 1 FROM #WIN w
           WHERE NOT EXISTS (SELECT 1 FROM dbo.REPORT x WITH (NOLOCK)
                             WHERE x.M_IDX = w.M_IDX AND ISNULL(x.IS_DEFAULT, 0) = 1
                               AND LTRIM(RTRIM(x.REPORT_ID)) = LTRIM(RTRIM(w.REPORT_ID))))
    THROW 55701, N'并列赢家复算结果与数据不一致，中止。', 1;

-- ---------------------------------------------------------------- ② 降级非赢家
UPDATE r
SET r.IS_DEFAULT = 0
FROM dbo.REPORT r
INNER JOIN #WIN w ON w.M_IDX = r.M_IDX
WHERE ISNULL(r.IS_DEFAULT, 0) = 1
  AND LTRIM(RTRIM(r.REPORT_ID)) <> LTRIM(RTRIM(w.REPORT_ID));

DECLARE @Affected INT = @@ROWCOUNT;
IF @Affected <> @Losers
    THROW 55702, N'实际降级行数与复算不符，中止。', 1;

-- ---------------------------------------------------------------- ③ 不变量：每模块至多一张默认
IF EXISTS (SELECT 1 FROM dbo.REPORT WITH (NOLOCK)
           WHERE ISNULL(IS_DEFAULT, 0) = 1
           GROUP BY M_IDX HAVING COUNT(*) > 1)
    THROW 55703, N'仍有模块存在多张默认报表，中止。', 1;

DECLARE @Detail NVARCHAR(900) = STUFF((
    SELECT N'；' + CAST(w.M_IDX AS NVARCHAR(20)) + N' 保留 ' + LTRIM(RTRIM(w.REPORT_ID))
    FROM #WIN w ORDER BY w.M_IDX FOR XML PATH('')), 1, 1, N'');
PRINT CONCAT(N'== 默认报表收敛：', @Groups, N' 个模块降级 ', @Affected, N' 张（', @Detail, N'）==');

DROP TABLE #WIN;

COMMIT TRANSACTION;
