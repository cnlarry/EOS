-- REPORT.M_IDX：报表归属的业务模块（**不是菜单项**）。
--
-- 背景：报表此前靠 R_M_IDX 指向"报表承载页"（M_URL='/reports' 的影子模块）才能被打开——
-- 156 个承载页里 149 个的主表与某个业务模块完全相同、"只被承载页使用"的表为 0；
-- 权限行与筛选条件也都登记在影子上。于是同一件事（这张报表属于谁）被写在两处，
-- 且会随宿主 URL 的写法漂移。本迁移把这层归属落成独立列。
--
-- 本迁移只做三件事：加列、按裁定回填、加约束与自证。读路径切换与条件随行搬迁在后续迁移。
--
-- 回填分档（合计 469 张）：
--   ① 宿主即业务模块（M_URL='/workbench'）                                  217 张：M_IDX = R_M_IDX
--   ②b 报表级裁定：汇总型报表的取数来自注册表、不从宿主主表派生，归属按语义指认   1 张：INV_Batch_Expiry_1
--   ② 宿主是承载页、且其 MASTER_TABLE 在全库只对应一个业务模块                153 张：归该模块
--   ③ 其余（承载页对应多个业务模块 / 承载页无主表 / 工具与管理页宿主 / 宿主悬空） 98 张：按逐条裁定表
--
-- 幂等：列已存在且已回填时全部 UPDATE 命中 0 行；档位行数只在首次执行时断言。
-- fail-closed：M_IDX 收紧为 NOT NULL——"没有归属的报表"不是一种可接受的状态。
--
-- 模块号变更：外键取 ON UPDATE CASCADE。既有级联语句（MenuAdminRepository.ChangeModuleIndexSql）
-- 先改 MODULES.M_IDX 再改各子表，若本外键不带级联，第一条语句就会直接失败；带上级联后
-- REPORT.M_IDX 随之自动改指，无需在该语句表里再加一行。

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @GUARD_MESSAGE NVARCHAR(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, @GUARD_MESSAGE, 1;

BEGIN TRANSACTION;

-- ---------------------------------------------------------------- ① 加列
-- 先可空加列：存量 469 行此刻无值，直接 NOT NULL 无法通过。回填完成后再收紧。
IF COL_LENGTH(N'dbo.REPORT', N'M_IDX') IS NULL
    ALTER TABLE dbo.REPORT ADD M_IDX INT NULL;
GO
-- 上一批与本批之间必须有批分隔：整批是一次性解析的，同一批里引用刚 ADD 的列会在解析期报
-- "列名无效"（与取值打印、检查约束同一成因）。GO 只是分批，会话与事务都沿用，不新开事务。

DECLARE @FirstRun BIT = CASE WHEN EXISTS (SELECT 1 FROM dbo.REPORT WITH (NOLOCK) WHERE M_IDX IS NULL) THEN 1 ELSE 0 END;

-- ---------------------------------------------------------------- ② 档①：宿主即业务模块
UPDATE r SET M_IDX = r.R_M_IDX
FROM dbo.REPORT r
JOIN dbo.MODULES h WITH (NOLOCK) ON h.M_IDX = r.R_M_IDX
WHERE r.M_IDX IS NULL AND LTRIM(RTRIM(ISNULL(h.M_URL, N''))) = N'/workbench';
DECLARE @Rule1 INT = @@ROWCOUNT;

-- ---------------------------------------------------------------- ②b 报表级裁定（必须先于"同表业务模块"）
-- 汇总型报表的取数是服务端注册表里的聚合 SQL，不从宿主主表派生 ⇒ 它的归属只能按语义指认，
-- "它挂在哪个承载页、承载页配了哪张主表"对它不成立。INV_Batch_Expiry_1（批次效期与临期清单）
-- 读的是批次账与余额表，归属其原本的权限模块 1303 料件库存资料；同一承载页上的 INV_Batch_List
-- 是批次主档的普通表报表，仍按同表规则归 1302——两者只能在报表粒度上分开，不能按宿主一刀切。
CREATE TABLE #REPORT_ATTRIBUTION (REPORT_ID NVARCHAR(100) NOT NULL PRIMARY KEY, TARGET INT NOT NULL);
INSERT INTO #REPORT_ATTRIBUTION (REPORT_ID, TARGET) VALUES (N'INV_Batch_Expiry_1', 1303);

UPDATE r SET M_IDX = a.TARGET
FROM dbo.REPORT r
JOIN #REPORT_ATTRIBUTION a ON a.REPORT_ID = LTRIM(RTRIM(r.REPORT_ID))
WHERE r.M_IDX IS NULL;
DECLARE @RuleReport INT = @@ROWCOUNT;

DROP TABLE #REPORT_ATTRIBUTION;

-- ---------------------------------------------------------------- ③ 档②：承载页的同表业务模块唯一
-- "同表业务模块"= 主表与承载页相同、且自己不是承载页的模块；候选多于一个即为歧义，交档③裁定。
UPDATE r SET M_IDX = c.Target
FROM dbo.REPORT r
JOIN dbo.MODULES h WITH (NOLOCK) ON h.M_IDX = r.R_M_IDX
JOIN (
    SELECT LTRIM(RTRIM(m.MASTER_TABLE)) AS MASTER_TABLE, MIN(m.M_IDX) AS Target
    FROM dbo.MODULES m WITH (NOLOCK)
    WHERE LTRIM(RTRIM(ISNULL(m.MASTER_TABLE, N''))) <> N''
      AND LTRIM(RTRIM(ISNULL(m.M_URL, N''))) <> N'/reports'
    GROUP BY LTRIM(RTRIM(m.MASTER_TABLE))
    HAVING COUNT(*) = 1
) c ON c.MASTER_TABLE = LTRIM(RTRIM(ISNULL(h.MASTER_TABLE, N'')))
WHERE r.M_IDX IS NULL AND LTRIM(RTRIM(ISNULL(h.M_URL, N''))) = N'/reports';
DECLARE @Rule2 INT = @@ROWCOUNT;

-- ---------------------------------------------------------------- ④ 档③：逐条裁定（宿主 → 归属）
-- 判定依据见同批归属裁定清单：歧义表按承载页中文名与候选模块业务名的对应关系判定；
-- 承载页无主表者归到它筛选的那张主档；工具与管理页宿主归到持有该表的模块或宿主自身。
CREATE TABLE #ATTRIBUTION (HOST INT NOT NULL PRIMARY KEY, TARGET INT NOT NULL);
INSERT INTO #ATTRIBUTION (HOST, TARGET) VALUES
    (28, 28), (235, 2101), (236, 2101), (238, 2201), (1206, 1201), (1310, 1502),
    (1508, 1502), (1605, 1615), (1804, 1804), (2101, 2101), (2201, 2201), (2202, 2202),
    (2203, 2203), (2204, 2204), (2305, 2305), (2306, 2306), (2504, 2504), (3003, 3003),
    (129801, 1201), (129802, 129802), (129804, 1204), (129806, 1201), (129807, 1201),
    (129808, 1201), (129809, 1201), (129810, 1201), (139803, 130103), (139804, 130104),
    (149810, 1407), (159802, 1502), (159803, 1503), (159804, 1518), (159805, 1505),
    (159806, 1405), (169805, 1615), (169808, 1608), (17019801, 170101), (17019805, 170105),
    (17029801, 170201), (17029803, 170203), (18019801, 180102), (18019803, 180105),
    (180213, 180213), (180251, 180651), (18019807, 180102),
    (18029810, 1803091), (18029811, 180652),
    (18039808, 180308), (18039809, 180309), (18039810, 180310), (18039812, 1803091),
    (180398081, 1908081), (180398091, 1803091), (180398101, 1803101),
    (18069801, 180102), (18069802, 180105), (18069803, 180652), (18069805, 180504),
    (199901, 199901), (230901, 230901), (230902, 230902);

UPDATE r SET M_IDX = a.TARGET
FROM dbo.REPORT r
JOIN #ATTRIBUTION a ON a.HOST = r.R_M_IDX
WHERE r.M_IDX IS NULL;
DECLARE @Rule3 INT = @@ROWCOUNT;

-- 裁定表里的宿主分两类：库内存在的模块，以及"宿主悬空"（报表的 R_M_IDX 在 MODULES 里没有
-- 对应行，故只能人工指定归属）。悬空宿主的个数是本案的一条已知事实，多一个或少一个都说明
-- 清单与库已经脱节——只校验"总数仍是 469"看不出来（会被后面的非空断言兜住，但不点明是哪条）。
DECLARE @DanglingHosts INT = (
    SELECT COUNT(*) FROM #ATTRIBUTION a
    WHERE NOT EXISTS (SELECT 1 FROM dbo.MODULES m WITH (NOLOCK) WHERE m.M_IDX = a.HOST));
IF @DanglingHosts <> 8
    THROW 55104, N'裁定表里的悬空宿主数与预期不符（应为 8 个），清单与库已脱节，中止。', 1;

DROP TABLE #ATTRIBUTION;

-- 四档必须逐档对上：只校验总数会掩盖"某一档多算、另一档少算"的错配
IF @FirstRun = 1
BEGIN
    IF @Rule1 <> 217
        THROW 55100, N'档①（宿主即业务模块）应回填 217 张，实际与预期不符，中止。', 1;
    IF @RuleReport <> 1
        THROW 55105, N'报表级裁定应回填 1 张，实际与预期不符，中止。', 1;
    IF @Rule2 <> 153
        THROW 55101, N'档②（同表业务模块唯一）应回填 153 张，实际与预期不符，中止。', 1;
    IF @Rule3 <> 98
        THROW 55102, N'档③（逐条裁定）应回填 98 张，实际与预期不符，中止。', 1;
END
IF (@Rule1 + @RuleReport + @Rule2 + @Rule3) <> 0 AND (@Rule1 + @RuleReport + @Rule2 + @Rule3) <> 469
    THROW 55103, N'各档回填合计不是 469（重复执行时应为 0），中止。', 1;

-- ---------------------------------------------------------------- ⑤ 后置自证
IF EXISTS (SELECT 1 FROM dbo.REPORT WITH (NOLOCK) WHERE M_IDX IS NULL)
    THROW 55110, N'仍有报表没有归属模块（M_IDX 为空）。', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.REPORT WITH (NOLOCK))
    THROW 55111, N'REPORT 表为空，与预期不符。', 1;

IF EXISTS (SELECT 1 FROM dbo.REPORT r WITH (NOLOCK)
           WHERE NOT EXISTS (SELECT 1 FROM dbo.MODULES m WITH (NOLOCK) WHERE m.M_IDX = r.M_IDX))
    THROW 55112, N'有归属模块在 MODULES 中不存在，中止。', 1;

-- 归属不得落在待退役的报表承载子树上：那是"把报表挂回影子模块"的老路。
-- 目录口径 = M_URL 为空且编号尾数 98；子节点 = 以它们为父的全部模块。
-- 129802（BOM展开表）在该子树内、但按逐条裁决保留，故单独排除。
DECLARE @Retire TABLE (M_IDX INT NOT NULL PRIMARY KEY);
INSERT INTO @Retire (M_IDX)
SELECT d.M_IDX FROM dbo.MODULES d WITH (NOLOCK)
WHERE LTRIM(RTRIM(ISNULL(d.M_URL, N''))) = N''
  AND RIGHT(LTRIM(RTRIM(CAST(d.M_IDX AS NVARCHAR(20)))), 2) = N'98';

IF (SELECT COUNT(*) FROM @Retire) <> 23
    THROW 55113, N'待退役的报表查询目录不是 23 个，与本迁移的删除面口径不符，中止。', 1;

DECLARE @RetireChildren INT = (
    SELECT COUNT(*) FROM dbo.MODULES c WITH (NOLOCK)
    WHERE c.M_P_IDX IN (SELECT M_IDX FROM @Retire));
IF @RetireChildren <> 156
    THROW 55114, N'待退役目录的直属子节点不是 156 个，与本迁移的删除面口径不符，中止。', 1;

IF EXISTS (SELECT 1 FROM dbo.REPORT r WITH (NOLOCK)
           WHERE r.M_IDX IN (SELECT M_IDX FROM @Retire WHERE M_IDX <> 129802))
    THROW 55115, N'有报表的归属落在待退役的承载子树上（129802 除外），中止。', 1;

-- ---------------------------------------------------------------- ⑥ 收紧为 NOT NULL 并加约束
IF EXISTS (SELECT 1 FROM sys.columns
           WHERE object_id = OBJECT_ID(N'dbo.REPORT') AND name = N'M_IDX' AND is_nullable = 1)
    ALTER TABLE dbo.REPORT ALTER COLUMN M_IDX INT NOT NULL;

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE object_id = OBJECT_ID(N'dbo.REPORT') AND name = N'IX_REPORT_M_IDX')
    CREATE NONCLUSTERED INDEX IX_REPORT_M_IDX ON dbo.REPORT (M_IDX);

IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_REPORT_MODULE')
    ALTER TABLE dbo.REPORT ADD CONSTRAINT FK_REPORT_MODULE
        FOREIGN KEY (M_IDX) REFERENCES dbo.MODULES (M_IDX)
        ON UPDATE CASCADE ON DELETE NO ACTION;

IF NOT EXISTS (SELECT 1 FROM sys.extended_properties
               WHERE major_id = OBJECT_ID(N'dbo.REPORT') AND minor_id = COLUMNPROPERTY(OBJECT_ID(N'dbo.REPORT'), N'M_IDX', 'ColumnId')
                 AND name = N'MS_Description')
    EXEC sys.sp_addextendedproperty @name = N'MS_Description',
        @value = N'归属模块（MODULES.M_IDX），指业务模块而非菜单节点：报表的数据集（主表/子表/过滤）、查询条件、字段级与行级权限、模块功能权限都按它判定。',
        @level0type = N'SCHEMA', @level0name = N'dbo',
        @level1type = N'TABLE', @level1name = N'REPORT',
        @level2type = N'COLUMN', @level2name = N'M_IDX';

COMMIT TRANSACTION;
