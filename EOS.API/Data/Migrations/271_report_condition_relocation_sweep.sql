-- 条件归位的补齐扫描：把**所有**"报表归属已不在本模块、但条件仍挂在本模块"的模块扫干净。
--
-- 为什么还要一条：上一条（268）把范围限定在"待退役的报表查询子树"（编号尾数 98 的目录及其子节点）。
-- 实测该子树之外还有两类载体：
--   ① 挂在**非**尾数 98 目录下的报表承载页（如 1399 仓库综合报表下的 139901 库存日报表）；
--   ② 兼作承载页的业务明细页（如 1310 工单库存明细表）。
-- 它们的报表同样已经归位到业务模块，条件却留在原处——条件按模块读，留在这里等于报表没有筛选条件。
--
-- 规则与 268 逐条一致（序号沿用原值、被占用则接在目标当前最大序号之后；条件的序号空间取
-- SYSQR_DEFAULT 与 SYSQR_USER 的并集，保证用户填值与它所属条件落在同一序号上）。
-- 对 268 已搬过的模块是空操作（那些模块上已无条件行）。

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @GUARD_MESSAGE NVARCHAR(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, @GUARD_MESSAGE, 1;

BEGIN TRANSACTION;

-- ---------------------------------------------------------------- ① 搬移映射：归属与宿主不一致的模块
-- 多目标承载页只允许 139808（该页同时载有归属不同的报表），其余出现多目标即中止，不猜。
IF EXISTS (
    SELECT 1 FROM dbo.REPORT r WITH (NOLOCK)
    WHERE r.R_M_IDX <> 139808
    GROUP BY r.R_M_IDX HAVING COUNT(DISTINCT r.M_IDX) > 1)
    THROW 55400, N'存在 139808 以外的多目标承载页，搬运映射不唯一，中止。', 1;

IF OBJECT_ID('tempdb..#MOVEMAP') IS NOT NULL DROP TABLE #MOVEMAP;
CREATE TABLE #MOVEMAP (SRC INT NOT NULL PRIMARY KEY, TGT INT NOT NULL);
INSERT INTO #MOVEMAP (SRC, TGT)
SELECT r.R_M_IDX, MIN(r.M_IDX)
FROM dbo.REPORT r WITH (NOLOCK)
WHERE r.R_M_IDX <> 139808
GROUP BY r.R_M_IDX HAVING MIN(r.M_IDX) <> r.R_M_IDX;   -- 自我映射不需要搬

-- ---------------------------------------------------------------- ② 条件行 → 目标
IF OBJECT_ID('tempdb..#COND') IS NOT NULL DROP TABLE #COND;
CREATE TABLE #COND (SRC INT NOT NULL, OLD_SERIAL SMALLINT NOT NULL, TGT INT NOT NULL, PRIMARY KEY (SRC, OLD_SERIAL));

INSERT INTO #COND (SRC, OLD_SERIAL, TGT)
SELECT x.SRC, x.SERIAL_NO, m.TGT
FROM (
    SELECT dd.M_IDX AS SRC, dd.SERIAL_NO FROM dbo.SYSQR_DEFAULT dd WITH (NOLOCK) WHERE dd.M_IDX IN (SELECT SRC FROM #MOVEMAP)
    UNION
    SELECT uu.M_IDX, uu.SERIAL_NO FROM dbo.SYSQR_USER uu WITH (NOLOCK) WHERE uu.M_IDX IN (SELECT SRC FROM #MOVEMAP)
) x
JOIN #MOVEMAP m ON m.SRC = x.SRC;

-- ---------------------------------------------------------------- ③ 确定性重排（与 268 同一规则）
IF OBJECT_ID('tempdb..#TAKEN') IS NOT NULL DROP TABLE #TAKEN;
CREATE TABLE #TAKEN (TGT INT NOT NULL, SERIAL_NO SMALLINT NOT NULL, PRIMARY KEY (TGT, SERIAL_NO));

IF OBJECT_ID('tempdb..#SERIALMAP') IS NOT NULL DROP TABLE #SERIALMAP;
CREATE TABLE #SERIALMAP (SRC INT NOT NULL, OLD_SERIAL SMALLINT NOT NULL, TGT INT NOT NULL, NEW_SERIAL SMALLINT NOT NULL, REKEYED BIT NOT NULL, PRIMARY KEY (SRC, OLD_SERIAL));

DECLARE @tgt INT = NULL, @old SMALLINT, @src INT, @new SMALLINT, @rekeyed BIT, @next SMALLINT;
DECLARE relocate CURSOR LOCAL FAST_FORWARD FOR
    SELECT SRC, OLD_SERIAL, TGT FROM #COND ORDER BY TGT, SRC, OLD_SERIAL;
OPEN relocate;
FETCH NEXT FROM relocate INTO @src, @old, @tgt;
WHILE @@FETCH_STATUS = 0
BEGIN
    IF NOT EXISTS (SELECT 1 FROM #TAKEN WHERE TGT = @tgt)
    BEGIN
        INSERT INTO #TAKEN (TGT, SERIAL_NO)
        SELECT @tgt, dd.SERIAL_NO FROM dbo.SYSQR_DEFAULT dd WITH (NOLOCK) WHERE dd.M_IDX = @tgt
        UNION
        SELECT @tgt, uu.SERIAL_NO FROM dbo.SYSQR_USER uu WITH (NOLOCK) WHERE uu.M_IDX = @tgt;
    END

    IF NOT EXISTS (SELECT 1 FROM #TAKEN WHERE TGT = @tgt AND SERIAL_NO = @old)
    BEGIN
        SET @new = @old;
        SET @rekeyed = 0;
    END
    ELSE
    BEGIN
        SELECT @next = ISNULL(MAX(SERIAL_NO), 0) + 1 FROM #TAKEN WHERE TGT = @tgt;
        SET @new = @next;
        SET @rekeyed = 1;
        PRINT CONCAT(N'REKEY 模块 ', @tgt, N'：源 ', @src, N' 的序号 ', @old, N' → ', @new);
    END

    INSERT INTO #TAKEN (TGT, SERIAL_NO) VALUES (@tgt, @new);
    INSERT INTO #SERIALMAP (SRC, OLD_SERIAL, TGT, NEW_SERIAL, REKEYED) VALUES (@src, @old, @tgt, @new, @rekeyed);
    FETCH NEXT FROM relocate INTO @src, @old, @tgt;
END
CLOSE relocate;
DEALLOCATE relocate;

DECLARE @RekeyCount INT = (SELECT COUNT(*) FROM #SERIALMAP WHERE REKEYED = 1);
DECLARE @MovedSerials INT = (SELECT COUNT(*) FROM #SERIALMAP);
PRINT CONCAT(N'== 补齐扫描：条件序号映射 ', @MovedSerials, N' 条，其中重排 ', @RekeyCount, N' 条 ==');

-- ---------------------------------------------------------------- ④ 落库
UPDATE dd SET M_IDX = sm.TGT, SERIAL_NO = sm.NEW_SERIAL
FROM dbo.SYSQR_DEFAULT dd
JOIN #SERIALMAP sm ON sm.SRC = dd.M_IDX AND sm.OLD_SERIAL = dd.SERIAL_NO;
DECLARE @MovedDefault INT = @@ROWCOUNT;

UPDATE uu SET M_IDX = sm.TGT, SERIAL_NO = sm.NEW_SERIAL
FROM dbo.SYSQR_USER uu
JOIN #SERIALMAP sm ON sm.SRC = uu.M_IDX AND sm.OLD_SERIAL = uu.SERIAL_NO;
DECLARE @MovedUser INT = @@ROWCOUNT;

DECLARE @MovedDefinition INT = 0;
DELETE da
FROM dbo.SYSQR_DA da
JOIN #MOVEMAP m ON m.SRC = da.M_IDX
WHERE EXISTS (SELECT 1 FROM dbo.SYSQR_DA t WITH (NOLOCK) WHERE t.M_IDX = m.TGT)
   OR m.SRC > (SELECT MIN(m2.SRC) FROM #MOVEMAP m2 WHERE m2.TGT = m.TGT);
DECLARE @DroppedDefinition INT = @@ROWCOUNT;

UPDATE da SET M_IDX = m.TGT
FROM dbo.SYSQR_DA da
JOIN #MOVEMAP m ON m.SRC = da.M_IDX;
SET @MovedDefinition = @@ROWCOUNT;

-- 用户打印偏好：列名沿用历史命名，取值改为报表归属模块
DELETE s
FROM dbo.SYSQR s
JOIN dbo.REPORT r WITH (NOLOCK) ON LTRIM(RTRIM(r.REPORT_ID)) = LTRIM(RTRIM(s.REPORT_ID))
WHERE s.R_M_IDX <> r.M_IDX
  AND EXISTS (
        SELECT 1 FROM dbo.SYSQR t WITH (NOLOCK)
        WHERE t.USER_ID = s.USER_ID AND LTRIM(RTRIM(t.REPORT_ID)) = LTRIM(RTRIM(s.REPORT_ID))
          AND (t.R_M_IDX = r.M_IDX OR t.R_M_IDX < s.R_M_IDX));
DECLARE @DroppedPrintPreference INT = @@ROWCOUNT;

UPDATE s SET R_M_IDX = r.M_IDX
FROM dbo.SYSQR s
JOIN dbo.REPORT r WITH (NOLOCK) ON LTRIM(RTRIM(r.REPORT_ID)) = LTRIM(RTRIM(s.REPORT_ID))
WHERE s.R_M_IDX <> r.M_IDX;
DECLARE @MovedPrintPreference INT = @@ROWCOUNT;

-- ---------------------------------------------------------------- ⑤ 后置自证
-- 归位后的不变量：**承载模块**上不得再留条件行——它承载的报表已经归位到别处，
-- 条件留在那儿等于那些报表没有筛选条件。（条件可以挂在没有报表的模块上，那是另一回事。）
IF EXISTS (
    SELECT 1 FROM dbo.REPORT r WITH (NOLOCK)
    WHERE r.R_M_IDX <> r.M_IDX
      AND EXISTS (SELECT 1 FROM dbo.SYSQR_DEFAULT d WITH (NOLOCK) WHERE d.M_IDX = r.R_M_IDX))
    THROW 55410, N'仍有承载模块留着条件行，而它承载的报表已归位到别处。', 1;

IF EXISTS (
    SELECT 1 FROM dbo.REPORT r WITH (NOLOCK)
    WHERE r.R_M_IDX <> r.M_IDX
      AND EXISTS (SELECT 1 FROM dbo.SYSQR_USER u WITH (NOLOCK) WHERE u.M_IDX = r.R_M_IDX))
    THROW 55412, N'仍有承载模块留着用户填值，而它承载的报表已归位到别处。', 1;

IF EXISTS (
    SELECT 1 FROM dbo.SYSQR_USER u WITH (NOLOCK)
    WHERE NOT EXISTS (SELECT 1 FROM dbo.SYSQR_DEFAULT d WITH (NOLOCK)
                      WHERE d.M_IDX = u.M_IDX AND d.SERIAL_NO = u.SERIAL_NO))
    THROW 55411, N'存在对不上条件行的用户填值（序号错位），中止。', 1;

PRINT CONCAT(N'== 补齐完成：条件 ', @MovedDefault, N' 行 / 用户填值 ', @MovedUser,
             N' 行 / 条件定义 ', @MovedDefinition, N' 行（丢弃重复 ', @DroppedDefinition,
             N' 行）/ 打印偏好 ', @MovedPrintPreference, N' 行（丢弃重复 ', @DroppedPrintPreference, N' 行）==');

DROP TABLE #COND;
DROP TABLE #SERIALMAP;
DROP TABLE #TAKEN;
DROP TABLE #MOVEMAP;

COMMIT TRANSACTION;
