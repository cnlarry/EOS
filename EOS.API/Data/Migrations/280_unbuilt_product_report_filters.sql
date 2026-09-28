-- 未建… 系列报表的筛选口径：产品主档里"还没有对应子档"的那些行。
--
-- 依据：这 5 张报表原先挂在报表承载页上，而承载页的 MASTER_TABLE 是空的
-- （`未建模具成品料件` 等 5 张），即**口径从未落地**——改造前它们恒 404，
-- 归位到产品主档（1201）后变成"显示全部 2273 个产品"，报表名与内容正好相反。
-- 本迁移把口径补上。
--
-- 口径（逐张）：
--   Product_List_nomoju      未建模具成品料件   PRO_NO 不在 MOU_PRO_M（产品模具对照表）
--   Product_List_nobom       未建BOM成品资料    PRO_NO 不在 BOM_STRU_M（BOM 结构主表）
--   Product_List_nokehujijia 未建客户计价成品资料 PRO_NO 不在 CLIENT_PRICE_D（客户计价明细）
--   Product_List_nochsjijia  未建厂商计价材料辅料 PRO_NO 不在 SUPPLIER_PRICE_D（厂商计价明细）
--   Product_List_nosample    未建样品的成品资料  PRO_NO 不在 SAMPLE_PRO（样品产品主档）
--
-- 落点：`REPORT.REPORT_FILTER`——**每张报表自带的过滤列**，运行时由
-- `ReportRepository.ApplyControlledFilter` 交给 `DataFilterParser` 受控解析（失败即拒，
-- 不静默忽略）。子查询的表/列必须命中 `DataFilterParser.SubqueryTableColumns` 白名单；
-- 其中 `SAMPLE_PRO` 由本批一并登记（它是 PRODUCT 的整表副本，按 PRO_NO 关联，
-- 受控子查询只读该列）。
--
-- 未纳入本迁移的一个口径问题（留给业务定夺，不是技术债）：
-- 报表名里的类别后缀（"成品资料" / "材料辅料"）是否要收窄到 PRO_TYPE？
-- 本迁移**只做"未建"这一件事**，不发明类别限制——理由是那 5 个承载页与旧系统源码里
-- 都没有留下类别判据（`ERP/` 全库搜这 5 个报表名 0 命中）。
-- 若要收窄，实测影响：客户计价 1425→843（PRO_TYPE=1）、厂商计价 1072→267（PRO_TYPE 3/4）、
-- 样品 2262→1679（PRO_TYPE=1）。

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @GUARD_MESSAGE NVARCHAR(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, @GUARD_MESSAGE, 1;

BEGIN TRANSACTION;

IF OBJECT_ID('tempdb..#UNBUILT') IS NOT NULL DROP TABLE #UNBUILT;
CREATE TABLE #UNBUILT (REPORT_ID NVARCHAR(100) NOT NULL PRIMARY KEY,
                       MASTER_TABLE NVARCHAR(100) NOT NULL,
                       FILTER_TEXT NVARCHAR(400) NOT NULL);

INSERT INTO #UNBUILT (REPORT_ID, MASTER_TABLE, FILTER_TEXT) VALUES
    (N'Product_List_nomoju',      N'MOU_PRO_M',        N'{PRODUCT.PRO_NO} NOT IN (SELECT PRO_NO FROM MOU_PRO_M)'),
    (N'Product_List_nobom',       N'BOM_STRU_M',       N'{PRODUCT.PRO_NO} NOT IN (SELECT PRO_NO FROM BOM_STRU_M)'),
    (N'Product_List_nokehujijia', N'CLIENT_PRICE_D',   N'{PRODUCT.PRO_NO} NOT IN (SELECT PRO_NO FROM CLIENT_PRICE_D)'),
    (N'Product_List_nochsjijia',  N'SUPPLIER_PRICE_D', N'{PRODUCT.PRO_NO} NOT IN (SELECT PRO_NO FROM SUPPLIER_PRICE_D)'),
    (N'Product_List_nosample',    N'SAMPLE_PRO',       N'{PRODUCT.PRO_NO} NOT IN (SELECT PRO_NO FROM SAMPLE_PRO)');

-- ---------------------------------------------------------------- ① 前置
-- 5 张报表都必须存在**且归属产品主档模块 1201**：口径写的是 PRODUCT 的列，
-- 归属模块的主表若不是 PRODUCT，这条过滤会挂到别的表上，语义直接错位。
IF EXISTS (SELECT 1 FROM #UNBUILT u
           WHERE NOT EXISTS (SELECT 1 FROM dbo.REPORT r WITH (NOLOCK)
                             JOIN dbo.MODULES m WITH (NOLOCK) ON m.M_IDX = r.M_IDX
                             WHERE LTRIM(RTRIM(r.REPORT_ID)) = u.REPORT_ID
                               AND r.M_IDX = 1201
                               AND LTRIM(RTRIM(ISNULL(m.MASTER_TABLE, N''))) = N'PRODUCT'))
    THROW 55910, N'待设口径的报表不存在、或归属模块不是产品主档（1201/PRODUCT），中止。', 1;

-- 子查询用到的表与列都必须物理存在——脚本改动的是"能不能跑"，不是"跑出几个错"。
IF EXISTS (SELECT 1 FROM #UNBUILT u
           WHERE NOT EXISTS (SELECT 1 FROM sys.tables t WITH (NOLOCK)
                             WHERE t.name = u.MASTER_TABLE)
              OR NOT EXISTS (SELECT 1 FROM sys.columns c WITH (NOLOCK)
                             WHERE c.object_id = OBJECT_ID(N'dbo.' + u.MASTER_TABLE)
                               AND c.name = N'PRO_NO'))
    THROW 55911, N'子查询表缺表或缺 PRO_NO 列，中止。', 1;

-- ---------------------------------------------------------------- ② 落库
UPDATE r
SET r.REPORT_FILTER = u.FILTER_TEXT
FROM dbo.REPORT r
JOIN #UNBUILT u ON LTRIM(RTRIM(r.REPORT_ID)) = u.REPORT_ID;

IF @@ROWCOUNT <> 5
    THROW 55912, N'未按预期更新 5 行 REPORT_FILTER，中止。', 1;

-- ---------------------------------------------------------------- ③ 后置自证
IF EXISTS (SELECT 1 FROM #UNBUILT u
           WHERE NOT EXISTS (SELECT 1 FROM dbo.REPORT r WITH (NOLOCK)
                             WHERE LTRIM(RTRIM(r.REPORT_ID)) = u.REPORT_ID
                               AND LTRIM(RTRIM(ISNULL(r.REPORT_FILTER, N''))) = u.FILTER_TEXT))
    THROW 55913, N'落库后回读不一致，中止。', 1;

COMMIT TRANSACTION;
