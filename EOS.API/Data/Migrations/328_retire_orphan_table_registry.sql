-- ============================================================================
-- EOS.ERP migration 328: 清掉"描述一张不存在的表"的元数据登记（悬空登记）
-- ----------------------------------------------------------------------------
-- 背景：`TABLES` / `FIELDS` / `FIELD_DATASOURCE` / `SYSQL_DEFAULT` / `SYSQL_FIELDS` 这些登记表
-- 是界面（字段维护、选列、条件配置）的驱动源。历次删表（2203/2204 报表版式、旧查询中心、
-- 迁移 322 的 TASK/SYSTEMP 等）把它们的数据删了，**描述它们的登记行却留了下来**——本库实测：
--
--   · TABLES 里 17 条登记，库内没有同名对象（表/视图/过程/函数都没有）：COP_PRODUCE_M、
--     HR_EMPLOYEE_CADE、LISTREPORT、LISTREPORT_CONDITION、MOC_PLAN_D2、MOC_PRODUCE_IN_M、
--     PPPPPPP、PRO_LINE、REPORT_FOOTER、REPORT_HEADER、REPORT_IMAGE、REPORT_INFO、REPORT_TAIL、
--     SYSQD、SYSQL、SYSTEMP、TASK；
--   · 连带 111 行 FIELDS、18 行 FIELD_DATASOURCE 同样指着不存在的表；
--   · 其中 3 条（REPORT_HEADER / REPORT_FOOTER / REPORT_TAIL）**被 2202 页头设置 / 2203 页尾设置 /
--     2204 表尾设置**这三个 ADR-009 §11 已下线的管理页当主表——那属于"模块指向不存在的表"，
--     要人先定夺那些模块，本脚本**刻意不删**、只显著报告（干跑时正是这条分流救下了它们）。
--
-- 用户 2026-10-06 拍板（决策清单 #145 的第 4 项）：**一并清掉**。这类行的危害是"坏条目"：
-- 字段维护页会列出一张点不开的表、选列会给出不存在的列、条件配置能配出必然报错的表达式。
--
-- 与 324~327 不同，本脚本是**幂等清扫**（按谓词删，第二次跑删 0 行也是正常结果）：
-- 判据是"库内没有同名对象且不被任何模块引用"，不写死名单，将来再出现悬空登记也能收平。
--
-- 安全底线（两条硬断言，任一不成立即整批回滚并报错）：
--   ① 清完之后"被模块引用的登记行"必须一行不少（前后计数比对）；
--   ② 清完之后不留任何指向不存在对象的登记行（被模块引用、留给人工定夺的那几条除外，
--      它们会打印成人可读的清单）。
-- ============================================================================

SET NOCOUNT ON;
SET XACT_ABORT ON;
-- 第二步用 XML 数据类型方法（FOR XML PATH(...).value()）拼模块清单：XML 方法要求
-- QUOTED_IDENTIFIER ON（否则 Msg 1934）。DbUp 走 ADO.NET 默认 ON，sqlcmd 默认 OFF——
-- 显式打开，好让本脚本在 scripts/test-migration-dryrun.ps1 下也能干跑。
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;

DECLARE @GUARD_MESSAGE NVARCHAR(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, @GUARD_MESSAGE, 1;

BEGIN TRANSACTION;

-- ① 待清集合：在登记表里出现过、但库内没有同名对象（任何类型）的 T_ID
CREATE TABLE #dead (
    T_ID      SYSNAME NOT NULL PRIMARY KEY,
    Tables_   INT NOT NULL DEFAULT 0,
    Fields_   INT NOT NULL DEFAULT 0,
    Ds_       INT NOT NULL DEFAULT 0,
    QryDef_   INT NOT NULL DEFAULT 0,
    QryFld_   INT NOT NULL DEFAULT 0,
    InTables  BIT NOT NULL DEFAULT 0
);

-- 五张登记表的 T_ID 取并集（同一个 T_ID 可能在多张表里出现；#dead 有主键，必须去重）
INSERT INTO #dead (T_ID) SELECT DISTINCT t.T_ID FROM dbo.TABLES t WHERE t.T_ID IS NOT NULL;
INSERT INTO #dead (T_ID) SELECT DISTINCT LTRIM(RTRIM(f.T_ID)) FROM dbo.FIELDS f
    WHERE NULLIF(LTRIM(RTRIM(f.T_ID)), N'') IS NOT NULL
      AND NOT EXISTS (SELECT 1 FROM #dead d WHERE d.T_ID = LTRIM(RTRIM(f.T_ID)));
INSERT INTO #dead (T_ID) SELECT DISTINCT LTRIM(RTRIM(x.T_ID)) FROM dbo.FIELD_DATASOURCE x
    WHERE NULLIF(LTRIM(RTRIM(x.T_ID)), N'') IS NOT NULL
      AND NOT EXISTS (SELECT 1 FROM #dead d WHERE d.T_ID = LTRIM(RTRIM(x.T_ID)));
INSERT INTO #dead (T_ID) SELECT DISTINCT LTRIM(RTRIM(q.T_ID)) FROM dbo.SYSQL_DEFAULT q
    WHERE NULLIF(LTRIM(RTRIM(q.T_ID)), N'') IS NOT NULL
      AND NOT EXISTS (SELECT 1 FROM #dead d WHERE d.T_ID = LTRIM(RTRIM(q.T_ID)));
INSERT INTO #dead (T_ID) SELECT DISTINCT LTRIM(RTRIM(s.T_ID)) FROM dbo.SYSQL_FIELDS s
    WHERE NULLIF(LTRIM(RTRIM(s.T_ID)), N'') IS NOT NULL
      AND NOT EXISTS (SELECT 1 FROM #dead d WHERE d.T_ID = LTRIM(RTRIM(s.T_ID)));

DELETE FROM #dead WHERE OBJECT_ID(N'dbo.' + T_ID) IS NOT NULL;   -- 对象还在：不是悬空

UPDATE d SET Tables_ = ISNULL(x.n, 0) FROM #dead d
LEFT JOIN (SELECT T_ID, COUNT(*) n FROM dbo.TABLES GROUP BY T_ID) x ON x.T_ID = d.T_ID;
UPDATE d SET Fields_ = ISNULL(x.n, 0) FROM #dead d
LEFT JOIN (SELECT LTRIM(RTRIM(T_ID)) AS T_ID, COUNT(*) n FROM dbo.FIELDS GROUP BY LTRIM(RTRIM(T_ID))) x ON x.T_ID = d.T_ID;
UPDATE d SET Ds_ = ISNULL(x.n, 0) FROM #dead d
LEFT JOIN (SELECT LTRIM(RTRIM(T_ID)) AS T_ID, COUNT(*) n FROM dbo.FIELD_DATASOURCE GROUP BY LTRIM(RTRIM(T_ID))) x ON x.T_ID = d.T_ID;
UPDATE d SET QryDef_ = ISNULL(x.n, 0) FROM #dead d
LEFT JOIN (SELECT LTRIM(RTRIM(T_ID)) AS T_ID, COUNT(*) n FROM dbo.SYSQL_DEFAULT GROUP BY LTRIM(RTRIM(T_ID))) x ON x.T_ID = d.T_ID;
UPDATE d SET QryFld_ = ISNULL(x.n, 0) FROM #dead d
LEFT JOIN (SELECT LTRIM(RTRIM(T_ID)) AS T_ID, COUNT(*) n FROM dbo.SYSQL_FIELDS GROUP BY LTRIM(RTRIM(T_ID))) x ON x.T_ID = d.T_ID;

-- ② 分流：被模块当主表/副表引用的悬空 T_ID **不属本次清理**，它们是"模块指向不存在的表"，
--    要人去定夺（本库实测是 2202/2203/2204 三个 ADR-009 §11 已下线的版式管理页，主表
--    REPORT_HEADER / REPORT_FOOTER / REPORT_TAIL 早随那次下线删除）。这里把它们摘出待清集合，
--    只**显著报告**、不擅自删：删掉登记反而会把"模块引用了不存在的表"这条线索一起抹掉。
CREATE TABLE #blocked (T_ID SYSNAME NOT NULL PRIMARY KEY, Modules_ NVARCHAR(MAX) NULL);

INSERT INTO #blocked (T_ID, Modules_)
SELECT d.T_ID, STUFF((SELECT N'、' + CONVERT(nvarchar(20), m2.M_IDX) + N' ' + ISNULL(m2.M_DESC, N'')
                      FROM dbo.MODULES m2 WITH (NOLOCK)
                      WHERE LTRIM(RTRIM(ISNULL(m2.MASTER_TABLE, N''))) = d.T_ID
                         OR LTRIM(RTRIM(ISNULL(m2.DETAIL_TABLE, N''))) = d.T_ID
                      FOR XML PATH(N''), TYPE).value(N'.', N'nvarchar(max)'), 1, 1, N'')
FROM #dead d
WHERE EXISTS (SELECT 1 FROM dbo.MODULES m WITH (NOLOCK)
              WHERE LTRIM(RTRIM(ISNULL(m.MASTER_TABLE, N''))) = d.T_ID
                 OR LTRIM(RTRIM(ISNULL(m.DETAIL_TABLE, N''))) = d.T_ID);

DECLARE @blockedTrail NVARCHAR(MAX) = N'';
SELECT @blockedTrail = @blockedTrail + N'    ' + T_ID + N' ← 模块 ' + ISNULL(Modules_, N'(?)') + CHAR(10)
FROM #blocked ORDER BY T_ID;

IF (SELECT COUNT(*) FROM #blocked) > 0
    PRINT N'== 跳过（不是垃圾行：有模块引用它，得先定夺那些模块）=='
        + CHAR(10) + @blockedTrail
        + N'    ↑ 这类"模块指向不存在的表"是真故障线索，本脚本刻意不删，留给人工决策（如整体退役该模块）';

DELETE d FROM #dead d WHERE EXISTS (SELECT 1 FROM #blocked b WHERE b.T_ID = d.T_ID);

-- ③ 留痕：清的是什么（逐条打印行数）
DECLARE @trail NVARCHAR(MAX) = N'';
SELECT @trail = @trail + N'    ' + T_ID + N'：TABLES ' + CONVERT(nvarchar(10), Tables_)
              + N' / FIELDS ' + CONVERT(nvarchar(10), Fields_)
              + N' / 数据源 ' + CONVERT(nvarchar(10), Ds_)
              + N' / 默认查询列 ' + CONVERT(nvarchar(10), QryDef_)
              + N' / 查询字段 ' + CONVERT(nvarchar(10), QryFld_) + CHAR(10)
FROM #dead ORDER BY T_ID;

DECLARE @deadCount INT = (SELECT COUNT(*) FROM #dead);
DECLARE @fieldSum INT = (SELECT ISNULL(SUM(Fields_), 0) FROM #dead);
DECLARE @dsSum INT = (SELECT ISNULL(SUM(Ds_), 0) FROM #dead);
DECLARE @qryDefSum INT = (SELECT ISNULL(SUM(QryDef_), 0) FROM #dead);

PRINT N'== 清掉"描述不存在的表"的元数据登记（决策清单 #145）=='
    + CHAR(10) + N'    待清 T_ID ' + CONVERT(nvarchar(10), @deadCount) + N' 个；'
    + N'FIELDS ' + CONVERT(nvarchar(10), @fieldSum) + N' 行；'
    + N'数据源 ' + CONVERT(nvarchar(10), @dsSum) + N' 行；'
    + N'默认查询列 ' + CONVERT(nvarchar(10), @qryDefSum) + N' 行'
    + CHAR(10) + ISNULL(NULLIF(@trail, N''), N'    （本次无待清项）');

-- ④ 安全断言②的基线：清之前，被模块引用的登记行有多少
DECLARE @linkedBefore INT = (SELECT COUNT(*)
                             FROM dbo.TABLES t
                             JOIN dbo.MODULES m WITH (NOLOCK)
                               ON LTRIM(RTRIM(ISNULL(m.MASTER_TABLE, N''))) = t.T_ID
                               OR LTRIM(RTRIM(ISNULL(m.DETAIL_TABLE, N''))) = t.T_ID);

-- ⑤ 清（幂等：集合为空时这几条 DELETE 命中 0 行）
DELETE f FROM dbo.FIELDS f JOIN #dead d ON LTRIM(RTRIM(f.T_ID)) = d.T_ID;
DELETE x FROM dbo.FIELD_DATASOURCE x JOIN #dead d ON LTRIM(RTRIM(x.T_ID)) = d.T_ID;
DELETE q FROM dbo.SYSQL_DEFAULT q JOIN #dead d ON LTRIM(RTRIM(q.T_ID)) = d.T_ID;
DELETE s FROM dbo.SYSQL_FIELDS s JOIN #dead d ON LTRIM(RTRIM(s.T_ID)) = d.T_ID;
DELETE t FROM dbo.TABLES t JOIN #dead d ON t.T_ID = d.T_ID;

-- ⑥ 安全断言②：被模块引用的登记行必须一行不少
DECLARE @linkedAfter INT = (SELECT COUNT(*)
                            FROM dbo.TABLES t
                            JOIN dbo.MODULES m WITH (NOLOCK)
                              ON LTRIM(RTRIM(ISNULL(m.MASTER_TABLE, N''))) = t.T_ID
                              OR LTRIM(RTRIM(ISNULL(m.DETAIL_TABLE, N''))) = t.T_ID);

IF @linkedAfter <> @linkedBefore
    THROW 53810, N'被模块引用的登记行被误删了（清理前 N 行 ≠ 清理后）：本脚本只该动"库内不存在"的那些。', 1;

-- ⑦ 安全断言③：清完之后，五张登记表里都不该再有指向不存在对象的行（第二步分流掉的除外）
IF EXISTS (SELECT 1 FROM dbo.TABLES t WHERE OBJECT_ID(N'dbo.' + t.T_ID) IS NULL
             AND NOT EXISTS (SELECT 1 FROM #blocked b WHERE b.T_ID = t.T_ID))
    THROW 53811, N'TABLES 里仍有描述不存在表的登记行（且不属于"被模块引用、留给人工定夺"那一类）。', 1;

IF EXISTS (SELECT 1 FROM dbo.FIELDS f
           WHERE OBJECT_ID(N'dbo.' + LTRIM(RTRIM(f.T_ID))) IS NULL
             AND NOT EXISTS (SELECT 1 FROM #blocked b WHERE b.T_ID = LTRIM(RTRIM(f.T_ID)))
             AND NOT EXISTS (SELECT 1 FROM dbo.MODULES m WITH (NOLOCK)
                             WHERE LTRIM(RTRIM(ISNULL(m.MASTER_TABLE, N''))) = LTRIM(RTRIM(f.T_ID))
                                OR LTRIM(RTRIM(ISNULL(m.DETAIL_TABLE, N''))) = LTRIM(RTRIM(f.T_ID))))
    THROW 53812, N'FIELDS 里仍有指向不存在表的行。', 1;

IF EXISTS (SELECT 1 FROM dbo.FIELD_DATASOURCE x WHERE OBJECT_ID(N'dbo.' + LTRIM(RTRIM(x.T_ID))) IS NULL
             AND NOT EXISTS (SELECT 1 FROM #blocked b WHERE b.T_ID = LTRIM(RTRIM(x.T_ID))))
    THROW 53813, N'FIELD_DATASOURCE 里仍有指向不存在表的行。', 1;

IF EXISTS (SELECT 1 FROM dbo.SYSQL_DEFAULT q WHERE OBJECT_ID(N'dbo.' + LTRIM(RTRIM(q.T_ID))) IS NULL
             AND NOT EXISTS (SELECT 1 FROM #blocked b WHERE b.T_ID = LTRIM(RTRIM(q.T_ID))))
    THROW 53814, N'SYSQL_DEFAULT 里仍有指向不存在表的行。', 1;

IF EXISTS (SELECT 1 FROM dbo.SYSQL_FIELDS s WHERE OBJECT_ID(N'dbo.' + LTRIM(RTRIM(s.T_ID))) IS NULL
             AND NOT EXISTS (SELECT 1 FROM #blocked b WHERE b.T_ID = LTRIM(RTRIM(s.T_ID))))
    THROW 53815, N'SYSQL_FIELDS 里仍有指向不存在表的行。', 1;

DROP TABLE #blocked;
DROP TABLE #dead;

COMMIT TRANSACTION;

PRINT N'== 收口完成：悬空登记已清（被模块引用的登记行一行未动）；将来再出现同类悬空，本脚本可重复执行 ==';
