-- 报表编号规范收紧：清掉 4 个不合规编号，并把"改名要级联到哪些表"一次做对。

--
-- 依据：`REPORT.REPORT_ID` 是报表的**身份**（不可变、被 7 张表按编号引用），
-- 规范为 `^[A-Za-z0-9_-]{3,40}$`（字母/数字/下划线/连字符，3–40 位）。
-- 实测有 4 个编号带空格或尾点号，它们的问题不只是"难看"：
--   · 带空格/点号的编号在 URL 与 JSON 里要求调用方正确转义，少转一处就取不到报表；
--   · 尾点号在 Windows 文件名与部分工具链里有"被吞掉"的历史坑，导出文件名会与编号不一致。
--
-- 引用报表编号的表共 7 张（无外键，纯逻辑引用，改名必须逐表级联）：
--   REPORT（身份本体）/ REPORT_SORT（排序方案）/ REPORT_INBOX（订阅产物）/
--   REPORT_SUBSCRIPTION（订阅）/ SYSDD_REPORT（个人例外行）/ SYSDH_REPORT（组例外行）/
--   SYSQR（打印偏好）
--
-- 前置：4 个旧编号都必须存在、4 个新编号都必须未被占用（撞名即中止，不做"随便加个后缀"）。
-- 留底：`logs/report-id-renames.csv`

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @GUARD_MESSAGE NVARCHAR(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, @GUARD_MESSAGE, 1;

BEGIN TRANSACTION;

IF OBJECT_ID('tempdb..#RENAME') IS NOT NULL DROP TABLE #RENAME;
CREATE TABLE #RENAME (OLD_ID NVARCHAR(50) NOT NULL PRIMARY KEY, NEW_ID NVARCHAR(50) NOT NULL,
                      SORT_ROWS INT NOT NULL DEFAULT 0, INBOX_ROWS INT NOT NULL DEFAULT 0,
                      SUB_ROWS INT NOT NULL DEFAULT 0, SDD_ROWS INT NOT NULL DEFAULT 0,
                      SDH_ROWS INT NOT NULL DEFAULT 0, SQR_ROWS INT NOT NULL DEFAULT 0);

INSERT INTO #RENAME (OLD_ID, NEW_ID) VALUES
    (N'COP_Send_List _jd',   N'COP_Send_List_jd'),
    (N'INV_Occur_In_List.',  N'INV_Occur_In_List'),
    (N'MOC_Get.',            N'MOC_Get'),
    (N'SYS_Company.',        N'SYS_Company');

-- ---------------------------------------------------------------- ① 前置
IF EXISTS (SELECT 1 FROM #RENAME n
           WHERE NOT EXISTS (SELECT 1 FROM dbo.REPORT r WITH (NOLOCK)
                             WHERE LTRIM(RTRIM(r.REPORT_ID)) = n.OLD_ID))
    THROW 55900, N'有待改名编号在 REPORT 中不存在，中止。', 1;

IF EXISTS (SELECT 1 FROM #RENAME n
           WHERE EXISTS (SELECT 1 FROM dbo.REPORT r WITH (NOLOCK)
                         WHERE LTRIM(RTRIM(r.REPORT_ID)) = n.NEW_ID)
              OR n.NEW_ID = n.OLD_ID)
    THROW 55901, N'目标编号已被占用或与原名相同，中止（改名不得靠加后缀蒙过去）。', 1;

IF EXISTS (SELECT 1 FROM dbo.REPORT WITH (NOLOCK)
           WHERE (LEN(LTRIM(RTRIM(REPORT_ID))) < 3 OR LEN(LTRIM(RTRIM(REPORT_ID))) > 40
                  OR LTRIM(RTRIM(REPORT_ID)) LIKE N'%[^A-Za-z0-9_-]%')
             AND LTRIM(RTRIM(REPORT_ID)) NOT IN (SELECT n.OLD_ID FROM #RENAME n))
    THROW 55902, N'除登记的 4 个之外还有不合规编号，先核对清单，中止。', 1;

-- ---------------------------------------------------------------- ② 级联改名
UPDATE s SET s.REPORT_ID = n.NEW_ID
FROM dbo.REPORT_SORT s
INNER JOIN #RENAME n ON LTRIM(RTRIM(s.REPORT_ID)) = n.OLD_ID;

UPDATE i SET i.REPORT_ID = n.NEW_ID
FROM dbo.REPORT_INBOX i
INNER JOIN #RENAME n ON LTRIM(RTRIM(i.REPORT_ID)) = n.OLD_ID;

UPDATE b SET b.REPORT_ID = n.NEW_ID
FROM dbo.REPORT_SUBSCRIPTION b
INNER JOIN #RENAME n ON LTRIM(RTRIM(b.REPORT_ID)) = n.OLD_ID;

UPDATE d SET d.REPORT_ID = n.NEW_ID
FROM dbo.SYSDD_REPORT d
INNER JOIN #RENAME n ON LTRIM(RTRIM(d.REPORT_ID)) = n.OLD_ID;

UPDATE g SET g.REPORT_ID = n.NEW_ID
FROM dbo.SYSDH_REPORT g
INNER JOIN #RENAME n ON LTRIM(RTRIM(g.REPORT_ID)) = n.OLD_ID;

UPDATE q SET q.REPORT_ID = n.NEW_ID
FROM dbo.SYSQR q
INNER JOIN #RENAME n ON LTRIM(RTRIM(q.REPORT_ID)) = n.OLD_ID;

UPDATE r SET r.REPORT_ID = n.NEW_ID
FROM dbo.REPORT r
INNER JOIN #RENAME n ON LTRIM(RTRIM(r.REPORT_ID)) = n.OLD_ID;

-- ---------------------------------------------------------------- ③ 不变量
-- 旧编号在 7 张表里都必须绝迹（任一表漏掉，就是一条永远取不到的孤儿行）
IF EXISTS (
    SELECT 1 FROM dbo.REPORT WITH (NOLOCK) WHERE LTRIM(RTRIM(REPORT_ID)) IN (SELECT OLD_ID FROM #RENAME)
    UNION ALL SELECT 1 FROM dbo.REPORT_SORT WITH (NOLOCK) WHERE LTRIM(RTRIM(REPORT_ID)) IN (SELECT OLD_ID FROM #RENAME)
    UNION ALL SELECT 1 FROM dbo.REPORT_INBOX WITH (NOLOCK) WHERE LTRIM(RTRIM(REPORT_ID)) IN (SELECT OLD_ID FROM #RENAME)
    UNION ALL SELECT 1 FROM dbo.REPORT_SUBSCRIPTION WITH (NOLOCK) WHERE LTRIM(RTRIM(REPORT_ID)) IN (SELECT OLD_ID FROM #RENAME)
    UNION ALL SELECT 1 FROM dbo.SYSDD_REPORT WITH (NOLOCK) WHERE LTRIM(RTRIM(REPORT_ID)) IN (SELECT OLD_ID FROM #RENAME)
    UNION ALL SELECT 1 FROM dbo.SYSDH_REPORT WITH (NOLOCK) WHERE LTRIM(RTRIM(REPORT_ID)) IN (SELECT OLD_ID FROM #RENAME)
    UNION ALL SELECT 1 FROM dbo.SYSQR WITH (NOLOCK) WHERE LTRIM(RTRIM(REPORT_ID)) IN (SELECT OLD_ID FROM #RENAME))
    THROW 55903, N'改名后仍有表残留旧编号，中止。', 1;

IF EXISTS (SELECT 1 FROM dbo.REPORT WITH (NOLOCK)
           WHERE LEN(LTRIM(RTRIM(REPORT_ID))) < 3 OR LEN(LTRIM(RTRIM(REPORT_ID))) > 40
              OR LTRIM(RTRIM(REPORT_ID)) LIKE N'%[^A-Za-z0-9_-]%')
    THROW 55904, N'仍有不合规编号残留，中止。', 1;

DECLARE @Renamed INT = (SELECT COUNT(*) FROM #RENAME);
DECLARE @Detail NVARCHAR(600) = STUFF((
    SELECT N'；' + n.OLD_ID + N' → ' + n.NEW_ID FROM #RENAME n ORDER BY n.OLD_ID FOR XML PATH('')), 1, 1, N'');
PRINT CONCAT(N'== 报表编号规范化：', @Renamed, N' 个（', @Detail, N'）==');

DROP TABLE #RENAME;

COMMIT TRANSACTION;
