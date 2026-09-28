-- 报表筛选条件的归位：承载页上的条件行搬到报表的归属模块（REPORT.M_IDX）。
--
-- 为什么必须搬：条件是按模块读的（SYSQR_DEFAULT.M_IDX），承载页一旦随影子模块退役，
-- 挂在它上面的条件就一起消失，报表会退化成"没有筛选条件"——不报错、只是少了一批筛选面板。
--
-- 三条规则：
--   ① 目标模块 = 该承载页所载报表的归属模块。承载页同时载有归属不同的报表时，只能在**报表粒度**上
--      分开处理：实测只有 139808（批次主档报表 → 1302 料件批号资料；批次效期报表的 5/6 号条件 →
--      1303 料件库存资料）。出现第二个这样的承载页即中止，不猜。
--   ② 序号沿用原值；在目标模块已被占用（被目标既有行、或先搬到的同行占用）时改为"目标当前最大序号 + 1"
--      并依次递增。同一目标内按（源模块号, 原序号）升序处理 ⇒ 结果确定、可复跑。
--      重排逐条 PRINT，由执行方落清单留底。
--   ③ 条件的序号空间取 SYSQR_DEFAULT 与 SYSQR_USER 的并集统一映射：用户填值与它所属的条件必须落在
--      同一个序号上，否则"条件还在、用户填的值串到了另一个条件上"。
--
-- SYSQR_DA 目前没有任何读取点（库内它只带审计列），仍按同一映射搬移以免遗漏；目标已有行时保留目标行。
-- SYSQR（用户打印偏好）按报表编号重指到归属模块：同一 (用户, 报表) 在目标模块已有行时保留目标行、
-- 删掉源行（键是 (USER_ID, 模块, REPORT_ID)，两个都留会撞键）。
--
-- 幂等：源模块上已无条件行时，全部 UPDATE 命中 0 行。

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @GUARD_MESSAGE NVARCHAR(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, @GUARD_MESSAGE, 1;

BEGIN TRANSACTION;

-- ---------------------------------------------------------------- ① 待退役的承载子树
DECLARE @RETIRE TABLE (M_IDX INT NOT NULL PRIMARY KEY);
INSERT INTO @RETIRE (M_IDX)
SELECT d.M_IDX FROM dbo.MODULES d WITH (NOLOCK)
WHERE LTRIM(RTRIM(ISNULL(d.M_URL, N''))) = N''
  AND RIGHT(LTRIM(RTRIM(CAST(d.M_IDX AS NVARCHAR(20)))), 2) = N'98';
IF (SELECT COUNT(*) FROM @RETIRE) <> 23
    THROW 55300, N'待退役的报表查询目录不是 23 个，与删除面口径不符，中止。', 1;

DECLARE @SUBTREE TABLE (M_IDX INT NOT NULL PRIMARY KEY);
INSERT INTO @SUBTREE (M_IDX) SELECT M_IDX FROM @RETIRE;
INSERT INTO @SUBTREE (M_IDX)
SELECT c.M_IDX FROM dbo.MODULES c WITH (NOLOCK) WHERE c.M_P_IDX IN (SELECT M_IDX FROM @RETIRE);
IF (SELECT COUNT(*) FROM @SUBTREE) <> 179
    THROW 55301, N'待退役承载子树不是 179 个模块，与删除面口径不符，中止。', 1;

-- ---------------------------------------------------------------- ② 搬移映射（源承载页 → 归属模块）
-- 多目标承载页只允许 139808 一个：其余情况下"这个模块的条件该跟哪张报表走"没有唯一答案。
IF EXISTS (
    SELECT 1 FROM dbo.REPORT r WITH (NOLOCK)
    WHERE r.R_M_IDX IN (SELECT M_IDX FROM @SUBTREE) AND r.R_M_IDX <> 139808
    GROUP BY r.R_M_IDX HAVING COUNT(DISTINCT r.M_IDX) > 1)
    THROW 55302, N'存在 139808 以外的多目标承载页，搬运映射不唯一，中止。', 1;

IF OBJECT_ID('tempdb..#MOVEMAP') IS NOT NULL DROP TABLE #MOVEMAP;
CREATE TABLE #MOVEMAP (SRC INT NOT NULL PRIMARY KEY, TGT INT NOT NULL);
INSERT INTO #MOVEMAP (SRC, TGT)
SELECT r.R_M_IDX, MIN(r.M_IDX)
FROM dbo.REPORT r WITH (NOLOCK)
WHERE r.R_M_IDX IN (SELECT M_IDX FROM @SUBTREE)
  AND r.R_M_IDX <> 139808
GROUP BY r.R_M_IDX HAVING MIN(r.M_IDX) <> r.R_M_IDX;   -- 自我映射（承载页即归属模块）不需要搬

-- ---------------------------------------------------------------- ③ 条件行 → 目标
-- 序号空间取两张表的并集；139808 按报表粒度拆开
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

-- 139808：批次主档报表（1-4 号）归 1302；批次效期报表的两条筛选项（5/6 号）归 1303
INSERT INTO #COND (SRC, OLD_SERIAL, TGT)
SELECT x.SRC, x.SERIAL_NO, CASE WHEN x.SERIAL_NO >= 5 THEN 1303 ELSE 1302 END
FROM (
    SELECT dd.M_IDX AS SRC, dd.SERIAL_NO FROM dbo.SYSQR_DEFAULT dd WITH (NOLOCK) WHERE dd.M_IDX = 139808
    UNION
    SELECT uu.M_IDX, uu.SERIAL_NO FROM dbo.SYSQR_USER uu WITH (NOLOCK) WHERE uu.M_IDX = 139808
) x
WHERE NOT EXISTS (SELECT 1 FROM #COND c WHERE c.SRC = x.SRC AND c.OLD_SERIAL = x.SERIAL_NO);

-- ---------------------------------------------------------------- ④ 确定性重排
-- 目标上"已被占用"的序号 = 目标既有行（含 SYSQR_USER 的既有行；两表共用同一序号空间）
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
    -- 首次进入某个目标：把它的既有序号装进占用集。此刻源模块的行还没搬过来，故"既有"就是原样；
    -- 后续同一目标的迭代靠每次赋值时写入占用集来去重（游标已按 TGT 排序，同目标的行连续处理）。
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
PRINT CONCAT(N'== 条件序号映射 ', @MovedSerials, N' 条，其中重排 ', @RekeyCount, N' 条 ==');

-- ---------------------------------------------------------------- ⑤ 落库（三张表共用同一映射）
UPDATE dd SET M_IDX = sm.TGT, SERIAL_NO = sm.NEW_SERIAL
FROM dbo.SYSQR_DEFAULT dd
JOIN #SERIALMAP sm ON sm.SRC = dd.M_IDX AND sm.OLD_SERIAL = dd.SERIAL_NO;
DECLARE @MovedDefault INT = @@ROWCOUNT;

UPDATE uu SET M_IDX = sm.TGT, SERIAL_NO = sm.NEW_SERIAL
FROM dbo.SYSQR_USER uu
JOIN #SERIALMAP sm ON sm.SRC = uu.M_IDX AND sm.OLD_SERIAL = uu.SERIAL_NO;
DECLARE @MovedUser INT = @@ROWCOUNT;

DECLARE @MovedDefinition INT = 0;

-- 条件定义行（SYSQR_DA）每模块只允许一行（主键就是 M_IDX），且库内没有任何读取点。
-- 因此：目标已有行 ⇒ 源行直接丢弃（目标行权威）；多个源汇到同一目标 ⇒ 只留源模块号最小的那一行。
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

-- 139808 一页两目标：条件定义行归批次主档报表一侧（1302），规则同上
DELETE FROM dbo.SYSQR_DA
WHERE M_IDX = 139808
  AND EXISTS (SELECT 1 FROM dbo.SYSQR_DA t WITH (NOLOCK) WHERE t.M_IDX = 1302);
UPDATE dbo.SYSQR_DA SET M_IDX = 1302 WHERE M_IDX = 139808;
SET @MovedDefinition = @MovedDefinition + @@ROWCOUNT;

-- 用户打印偏好：按报表编号重指到该报表的归属模块。
-- 主键是 (USER_ID, 模块, REPORT_ID)，同一份偏好可能同时挂在目标模块与承载页上（存量漂移所致），
-- 重指后两者会撞键 ⇒ 同一 (用户, 报表) 只保留一行：优先保留已经落在目标模块上的，
-- 否则保留模块号最小的那一行（确定性，可复跑）。
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

-- ---------------------------------------------------------------- ⑥ 后置自证
IF EXISTS (SELECT 1 FROM dbo.SYSQR_DEFAULT WITH (NOLOCK) WHERE M_IDX IN (SELECT M_IDX FROM @SUBTREE))
    THROW 55310, N'待退役承载子树上仍有 SYSQR_DEFAULT 条件行未归位。', 1;
IF EXISTS (SELECT 1 FROM dbo.SYSQR_USER WITH (NOLOCK) WHERE M_IDX IN (SELECT M_IDX FROM @SUBTREE))
    THROW 55311, N'待退役承载子树上仍有 SYSQR_USER 用户填值未归位。', 1;
IF EXISTS (SELECT 1 FROM dbo.SYSQR_DA WITH (NOLOCK) WHERE M_IDX IN (SELECT M_IDX FROM @SUBTREE))
    THROW 55312, N'待退役承载子树上仍有 SYSQR_DA 条件定义行未归位。', 1;

-- 不变量：SYSQR_USER 的每一行都必须能对上同模块同序号的 SYSQR_DEFAULT 条件行
-- （用户填值是"给某个条件填的值"，序号错位就等于把值串到了另一个条件上）
IF EXISTS (
    SELECT 1 FROM dbo.SYSQR_USER u WITH (NOLOCK)
    WHERE NOT EXISTS (SELECT 1 FROM dbo.SYSQR_DEFAULT d WITH (NOLOCK)
                      WHERE d.M_IDX = u.M_IDX AND d.SERIAL_NO = u.SERIAL_NO))
    THROW 55313, N'存在对不上条件行的用户填值（序号错位），中止。', 1;

PRINT CONCAT(N'== 归位完成：条件 ', @MovedDefault, N' 行 / 用户填值 ', @MovedUser,
             N' 行 / 条件定义 ', @MovedDefinition, N' 行（丢弃重复 ', @DroppedDefinition,
             N' 行）/ 打印偏好 ', @MovedPrintPreference, N' 行（丢弃重复 ', @DroppedPrintPreference, N' 行）==');

DROP TABLE #COND;
DROP TABLE #SERIALMAP;
DROP TABLE #TAKEN;
DROP TABLE #MOVEMAP;

COMMIT TRANSACTION;
