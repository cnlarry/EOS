-- ============================================================================
-- EOS.ERP migration 337: 报表筛选条件去重（带用户填值的那一批）
-- ----------------------------------------------------------------------------
-- 背景：迁移 281 只去了"被删那份在 SYSQR_USER 里零填值"的安全批次；剩下 3 个模块
--       （1405 销售订单 / 1502 制令单 / 180214 人事日记）**两份都带用户填值**，
--       当时按"业务语义问题"具名挂起——同一字段两份填了不同值时两条谓词同时生效，
--       结果比用户以为的更窄。
--
-- 决策（用户 2026-10-08 拍板）：**保留序号较小的那一份**，把序号较大那份的用户填值
--       **按账号合并**过来；同一账号同一字段两套都有值时**以序号较大的那套为准**
--       （它是后形成的，代表较近的意图）。合并后删掉较大序号那份。
--
-- 范围：3 个模块共 **15 组**重复，**每组恰好 2 行**（前置断言；出现 3 行以上的组即中止，
--       因为此时"以较大为准"要按序号排序逐级覆盖，语义需重新确认）。
--
-- 手段：对每组 (M_IDX, F_ID, FILTER_TEMPLATE) 取 KEEP=MIN(SERIAL_NO)、DROP=MAX(SERIAL_NO)：
--       ① 先删 SYSQR_USER 里 KEEP 上"会被 DROP 覆盖"的账号行（冲突以 DROP 为准）；
--       ② 把 DROP 的填值行 SERIAL_NO 改成 KEEP（值原样保留）；
--       ③ 删 SYSQR_DEFAULT 里 DROP 的条件行。
--       全程一个事务、前后自证、可整库回滚。
-- ============================================================================

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @GUARD_MESSAGE NVARCHAR(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, @GUARD_MESSAGE, 1;

DECLARE @Work TABLE
(
    SEQ        INT IDENTITY(1,1) PRIMARY KEY,
    M_IDX      INT            NOT NULL,
    KEEP_NO    INT            NOT NULL,
    DROP_NO    INT            NOT NULL,
    F_ID       NVARCHAR(200)  NULL,
    TPL        NVARCHAR(MAX)  NULL
);

INSERT INTO @Work (M_IDX, KEEP_NO, DROP_NO, F_ID, TPL)
SELECT M_IDX, MIN(SERIAL_NO), MAX(SERIAL_NO), F_ID, FILTER_TEMPLATE
FROM dbo.SYSQR_DEFAULT WITH (NOLOCK)
WHERE M_IDX IN (1405, 1502, 180214)
GROUP BY M_IDX, F_ID, FILTER_TEMPLATE
HAVING COUNT(*) = 2;

-- ① 前置自证：组数与规模都必须与决策时的实况一致
IF (SELECT COUNT(*) FROM @Work) <> 15
    THROW 53701, N'前置不成立：待合并的重复组不是 15 组（库已变，先复核再跑）。', 1;
IF EXISTS (
        SELECT 1 FROM dbo.SYSQR_DEFAULT WITH (NOLOCK)
        WHERE M_IDX IN (1405, 1502, 180214)
        GROUP BY M_IDX, F_ID, FILTER_TEMPLATE
        HAVING COUNT(*) > 2)
    THROW 53702, N'前置不成立：存在 3 行以上的重复组，"以较大序号为准"需按序逐级覆盖，先确认语义。', 1;

DECLARE @UserRowsBefore INT = (SELECT COUNT(*) FROM dbo.SYSQR_USER WITH (NOLOCK) WHERE M_IDX IN (1405, 1502, 180214));
DECLARE @DefaultRowsBefore INT = (SELECT COUNT(*) FROM dbo.SYSQR_DEFAULT WITH (NOLOCK) WHERE M_IDX IN (1405, 1502, 180214));

BEGIN TRANSACTION;

-- ② 冲突以 DROP 为准：先删掉 KEEP 上"DROP 同账号也有填值"的行
DELETE u
FROM dbo.SYSQR_USER u
JOIN @Work w ON w.M_IDX = u.M_IDX AND w.KEEP_NO = u.SERIAL_NO
WHERE EXISTS (SELECT 1
              FROM dbo.SYSQR_USER d WITH (NOLOCK)
              WHERE d.M_IDX = u.M_IDX AND d.USER_ID = u.USER_ID AND d.SERIAL_NO = w.DROP_NO);

-- ③ 把 DROP 的填值行改挂到 KEEP（值原样保留；上一步已清掉会撞键的那些）
UPDATE u
   SET u.SERIAL_NO = w.KEEP_NO
FROM dbo.SYSQR_USER u
JOIN @Work w ON w.M_IDX = u.M_IDX AND w.DROP_NO = u.SERIAL_NO;

-- ④ 删掉 SYSQR_DEFAULT 里 DROP 的条件行（按三列定位，避免误删同模块的其它序号）
DELETE d
FROM dbo.SYSQR_DEFAULT d
JOIN @Work w ON w.M_IDX = d.M_IDX
            AND w.DROP_NO = d.SERIAL_NO
            AND ISNULL(LTRIM(RTRIM(d.F_ID)), N'') = ISNULL(LTRIM(RTRIM(w.F_ID)), N'')
            AND ISNULL(d.FILTER_TEMPLATE, N'') = ISNULL(w.TPL, N'');

COMMIT TRANSACTION;

-- ⑤ 后置自证
IF EXISTS (
        SELECT 1 FROM dbo.SYSQR_DEFAULT WITH (NOLOCK)
        WHERE M_IDX IN (1405, 1502, 180214)
        GROUP BY M_IDX, F_ID, FILTER_TEMPLATE
        HAVING COUNT(*) > 1)
    THROW 53710, N'后置失败：这 3 个模块仍有重复条件组。', 1;

IF (SELECT COUNT(*) FROM dbo.SYSQR_DEFAULT WITH (NOLOCK) WHERE M_IDX IN (1405, 1502, 180214))
   <> @DefaultRowsBefore - (SELECT COUNT(*) FROM @Work)
    THROW 53711, N'后置失败：SYSQR_DEFAULT 删除的行数与待删组数不符。', 1;

-- 用户填值：总数只能因"删除冲突行"而减少，绝不能因为改挂而丢行
IF (SELECT COUNT(*) FROM dbo.SYSQR_USER WITH (NOLOCK) WHERE M_IDX IN (1405, 1502, 180214)) > @UserRowsBefore
    THROW 53712, N'后置失败：用户填值行数反而变多了。', 1;

-- 不许留孤儿：填值行必须能对上一条现存的条件行
IF EXISTS (
        SELECT 1
        FROM dbo.SYSQR_USER u WITH (NOLOCK)
        WHERE u.M_IDX IN (1405, 1502, 180214)
          AND NOT EXISTS (SELECT 1 FROM dbo.SYSQR_DEFAULT d WITH (NOLOCK)
                          WHERE d.M_IDX = u.M_IDX AND d.SERIAL_NO = u.SERIAL_NO))
    THROW 53713, N'后置失败：存在对不上条件行的孤儿填值行。', 1;

-- PRINT 里不能嵌子查询（只允许标量表达式），先落到变量再拼串——迁移 334 的注释里记过这一条。
DECLARE @GroupsDone INT = (SELECT COUNT(*) FROM @Work);
DECLARE @DefaultRowsAfter INT = (SELECT COUNT(*) FROM dbo.SYSQR_DEFAULT WITH (NOLOCK) WHERE M_IDX IN (1405, 1502, 180214));
DECLARE @UserRowsAfter INT = (SELECT COUNT(*) FROM dbo.SYSQR_USER WITH (NOLOCK) WHERE M_IDX IN (1405, 1502, 180214));

PRINT N'[] 报表筛选条件去重完成：3 个模块（1405/1502/180214）合并 '
    + CONVERT(NVARCHAR(10), @GroupsDone) + N' 组重复；'
    + N'SYSQR_DEFAULT ' + CONVERT(NVARCHAR(10), @DefaultRowsBefore) + N' → '
    + CONVERT(NVARCHAR(10), @DefaultRowsAfter) + N' 行；SYSQR_USER '
    + CONVERT(NVARCHAR(10), @UserRowsBefore) + N' → ' + CONVERT(NVARCHAR(10), @UserRowsAfter)
    + N' 行（减少的即"以较大序号为准"覆盖掉的冲突行）。';
