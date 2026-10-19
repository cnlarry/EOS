-- 一次性｜锚点：SYSQL_DEFAULT 生命周期列统一追加（2026-10-19）
-- ----------------------------------------------------------------------------
-- 把「建立人、建立日期、修改人、修改日期、批核人、批核日期、批核状态、结案人、结案日期、结案状态」
-- 按上述顺序排到每个表对默认列（dbo.SYSQL_DEFAULT）的末尾。
--
-- 范围：统一工作台模块（dbo.V_MODULE_NODE.NODE_KIND = 'WORKBENCH'）的表对：
--   主表对 (MASTER_TABLE, MASTER_TABLE) 与主→副表对 (MASTER_TABLE, DETAIL_TABLE)，
--   按表对去重（同一张表被多个模块复用时只处理一次）。
--
-- 口径：
--   1. 字段按物理列代号识别，并限定在既有标签集合内（同一张表里代号被挪作他用时跳过，
--      例如 COP_SHIPMENT_M.CREATE_DATE 的标签是「出货日期」，不是建立日期）。
--   2. 只处理 FIELDS 中可见（IS_VISIBLE=1）、非虚拟、且物理列真实存在的字段。
--   3. 缺少某个字段就跳过该字段，不因同组缺项而整组跳过。
--   4. 已存在于默认列的这些字段一并移到末尾：最终默认列末尾固定是这组字段按序排列，
--      其余列保持各自相对顺序不变。
--
-- 幂等：先删后插、F_IDX 由「非目标列的最大值 + 序号」重算，重复执行结果一致。
-- 执行后自证四条：目标字段齐全、均排在末尾、组内按序、无重复键；任一不成立即回滚。
-- ----------------------------------------------------------------------------
SET NOCOUNT ON;
SET XACT_ABORT ON;
-- 引用视图与索引的 DML 需要这两个选项，脚本自带以免受执行入口影响
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;

IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, N'本脚本只能在 EOS.ERP 数据库内执行。', 1;

-- ① 字段规格：顺序 + 可接受的既有标签 -----------------------------------------
CREATE TABLE #spec (seq int NOT NULL PRIMARY KEY, fid nvarchar(50) NOT NULL);
INSERT INTO #spec (seq, fid) VALUES
    (1, N'CREATE_PERSON'),
    (2, N'CREATE_DATE'),
    (3, N'LAST_UPDATE_BY'),
    (4, N'LAST_UPDATE_DATE'),
    (5, N'CONFIRM_PERSON'),
    (6, N'CONFIRM_DATE'),
    (7, N'CONFIRM_TAG'),
    (8, N'FINISHED_PERSON'),
    (9, N'FINISHED_DATE'),
    (10, N'FINISHED_TAG');

CREATE TABLE #labels (seq int NOT NULL, label nvarchar(50) NOT NULL, PRIMARY KEY (seq, label));
INSERT INTO #labels (seq, label) VALUES
    (1, N'建立人'),
    (2, N'建立日期'),
    (3, N'修改人'), (3, N'更新人'),
    (4, N'修改日期'), (4, N'更新日期'),
    (5, N'批核人'), (5, N'确认人'),
    (6, N'批核日期'), (6, N'确认日期'),
    (7, N'批核状态'), (7, N'确认状态'),
    (8, N'结案人'), (8, N'确认人'),
    (9, N'结案日期'), (9, N'确认日期'),
    (10, N'结案状态'), (10, N'结案'), (10, N'结案标志');

-- ② 表对（去重）与本次目标字段 ------------------------------------------------
CREATE TABLE #pairs (M varchar(50) NOT NULL, R varchar(50) NOT NULL, PRIMARY KEY (M, R));
INSERT INTO #pairs (M, R)
SELECT DISTINCT LTRIM(RTRIM(m.MASTER_TABLE)), LTRIM(RTRIM(m.MASTER_TABLE))
FROM dbo.MODULES m
INNER JOIN dbo.V_MODULE_NODE n ON n.M_IDX = m.M_IDX
WHERE n.NODE_KIND = N'WORKBENCH'
  AND NULLIF(LTRIM(RTRIM(m.MASTER_TABLE)), N'') IS NOT NULL
UNION
SELECT DISTINCT LTRIM(RTRIM(m.MASTER_TABLE)), LTRIM(RTRIM(m.DETAIL_TABLE))
FROM dbo.MODULES m
INNER JOIN dbo.V_MODULE_NODE n ON n.M_IDX = m.M_IDX
WHERE n.NODE_KIND = N'WORKBENCH'
  AND NULLIF(LTRIM(RTRIM(m.MASTER_TABLE)), N'') IS NOT NULL
  AND NULLIF(LTRIM(RTRIM(m.DETAIL_TABLE)), N'') IS NOT NULL;

CREATE TABLE #target (M varchar(50) NOT NULL, R varchar(50) NOT NULL, seq int NOT NULL, fid nvarchar(50) NOT NULL, rn int NOT NULL, PRIMARY KEY (M, R, seq));
INSERT INTO #target (M, R, seq, fid, rn)
SELECT a.M, a.R, a.seq, a.fid, ROW_NUMBER() OVER (PARTITION BY a.M, a.R ORDER BY a.seq)
FROM (
    SELECT p.M, p.R, s.seq, s.fid
    FROM #pairs p
    CROSS JOIN #spec s
    WHERE EXISTS (
            SELECT 1
            FROM dbo.FIELDS f
            WHERE LTRIM(RTRIM(f.T_ID)) = p.R
              AND LTRIM(RTRIM(f.F_ID)) = s.fid
              AND COALESCE(f.IS_VISIBLE, 1) = 1
              AND COALESCE(f.IS_VIRTUAL, 0) = 0
              AND EXISTS (SELECT 1 FROM #labels l WHERE l.seq = s.seq AND l.label = LTRIM(RTRIM(f.F_DESC)))
        )
      AND EXISTS (
            SELECT 1
            FROM sys.columns c
            INNER JOIN sys.objects o ON o.object_id = c.object_id AND o.type IN ('U', 'V')
            INNER JOIN sys.schemas sc ON sc.schema_id = o.schema_id
            WHERE sc.name = N'dbo' AND o.name = p.R AND c.name = s.fid
        )
) a;

-- ③ 基线序号：每表对「非目标列」的最大 F_IDX（目标列整体排到它之后） ------------
CREATE TABLE #base (M varchar(50) NOT NULL, R varchar(50) NOT NULL, base_idx int NOT NULL, PRIMARY KEY (M, R));
INSERT INTO #base (M, R, base_idx)
SELECT t.M, t.R,
       ISNULL((
           SELECT MAX(d.F_IDX)
           FROM dbo.SYSQL_DEFAULT d
           WHERE LTRIM(RTRIM(d.T_ID)) = t.M
             AND LTRIM(RTRIM(d.T_ID_R)) = t.R
             AND NOT EXISTS (
                 SELECT 1 FROM #target x
                 WHERE x.M = t.M AND x.R = t.R AND x.fid = LTRIM(RTRIM(d.F_ID)))
       ), 0)
FROM (SELECT DISTINCT M, R FROM #target) t;

-- ④ 执行前的账目 --------------------------------------------------------------
DECLARE @pairs int = (SELECT COUNT(*) FROM (SELECT DISTINCT M, R FROM #target) t);
DECLARE @target_rows int = (SELECT COUNT(*) FROM #target);
DECLARE @existing_rows int = (
    SELECT COUNT(*) FROM #target t
    WHERE EXISTS (SELECT 1 FROM dbo.SYSQL_DEFAULT d
                  WHERE LTRIM(RTRIM(d.T_ID)) = t.M AND LTRIM(RTRIM(d.T_ID_R)) = t.R AND LTRIM(RTRIM(d.F_ID)) = t.fid));
DECLARE @pairs_without_defaults int = (
    SELECT COUNT(*) FROM (SELECT DISTINCT M, R FROM #target) t
    WHERE NOT EXISTS (SELECT 1 FROM dbo.SYSQL_DEFAULT d
                      WHERE LTRIM(RTRIM(d.T_ID)) = t.M AND LTRIM(RTRIM(d.T_ID_R)) = t.R));

BEGIN TRANSACTION;

-- ⑤ 先移除这些字段的旧行，再按序追加到末尾 ------------------------------------
DELETE d
FROM dbo.SYSQL_DEFAULT d
INNER JOIN #target t
    ON t.M = LTRIM(RTRIM(d.T_ID))
   AND t.R = LTRIM(RTRIM(d.T_ID_R))
   AND t.fid = LTRIM(RTRIM(d.F_ID));

INSERT INTO dbo.SYSQL_DEFAULT (T_ID, T_ID_R, F_IDX, F_ID)
SELECT t.M, t.R, b.base_idx + t.rn, t.fid
FROM #target t
INNER JOIN #base b ON b.M = t.M AND b.R = t.R;

-- ⑥ 事务内自证 ----------------------------------------------------------------
IF EXISTS (
        SELECT 1 FROM #target t
        WHERE NOT EXISTS (
            SELECT 1 FROM dbo.SYSQL_DEFAULT d
            WHERE LTRIM(RTRIM(d.T_ID)) = t.M AND LTRIM(RTRIM(d.T_ID_R)) = t.R AND LTRIM(RTRIM(d.F_ID)) = t.fid))
    THROW 50001, N'自证失败：追加后仍有目标字段缺失。', 1;

IF EXISTS (
        SELECT 1
        FROM #target t
        INNER JOIN dbo.SYSQL_DEFAULT dt
            ON LTRIM(RTRIM(dt.T_ID)) = t.M AND LTRIM(RTRIM(dt.T_ID_R)) = t.R AND LTRIM(RTRIM(dt.F_ID)) = t.fid
        WHERE EXISTS (
            SELECT 1 FROM dbo.SYSQL_DEFAULT o
            WHERE LTRIM(RTRIM(o.T_ID)) = t.M
              AND LTRIM(RTRIM(o.T_ID_R)) = t.R
              AND NOT EXISTS (SELECT 1 FROM #target x WHERE x.M = t.M AND x.R = t.R AND x.fid = LTRIM(RTRIM(o.F_ID)))
              AND o.F_IDX > dt.F_IDX))
    THROW 50002, N'自证失败：目标字段没有排在默认列末尾。', 1;

IF EXISTS (
        SELECT 1
        FROM #target a
        INNER JOIN #target b ON b.M = a.M AND b.R = a.R AND b.seq > a.seq
        INNER JOIN dbo.SYSQL_DEFAULT da
            ON LTRIM(RTRIM(da.T_ID)) = a.M AND LTRIM(RTRIM(da.T_ID_R)) = a.R AND LTRIM(RTRIM(da.F_ID)) = a.fid
        INNER JOIN dbo.SYSQL_DEFAULT db
            ON LTRIM(RTRIM(db.T_ID)) = b.M AND LTRIM(RTRIM(db.T_ID_R)) = b.R AND LTRIM(RTRIM(db.F_ID)) = b.fid
        WHERE da.F_IDX >= db.F_IDX)
    THROW 50003, N'自证失败：目标字段组内顺序不正确。', 1;

-- 只查本次写入的字段：库内其他字段存在历史遗留的重复行（读取侧按 MIN(F_IDX) 去重），不在本次范围
IF EXISTS (
        SELECT 1
        FROM dbo.SYSQL_DEFAULT d
        INNER JOIN #target t
            ON LTRIM(RTRIM(d.T_ID)) = t.M AND LTRIM(RTRIM(d.T_ID_R)) = t.R AND LTRIM(RTRIM(d.F_ID)) = t.fid
        GROUP BY t.M, t.R, t.fid
        HAVING COUNT(*) > 1)
    THROW 50004, N'自证失败：本次写入的字段出现多行。', 1;

COMMIT TRANSACTION;

-- ⑦ 结果 ----------------------------------------------------------------------
PRINT N'== 默认列生命周期字段统一追加完成 =='
    + CHAR(10) + N'    涉及表对：' + CONVERT(nvarchar(10), @pairs)
    + N'（其中此前没有任何默认列：' + CONVERT(nvarchar(10), @pairs_without_defaults) + N'）'
    + CHAR(10) + N'    目标字段行：' + CONVERT(nvarchar(10), @target_rows)
    + N'（此前已在默认列中、本次移至末尾：' + CONVERT(nvarchar(10), @existing_rows)
    + N'；本次新增：' + CONVERT(nvarchar(10), @target_rows - @existing_rows) + N'）';
