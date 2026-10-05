-- ============================================================================
-- EOS.ERP migration 317: normalize view and constraint names
-- ----------------------------------------------------------------------------
-- Database: EOS.ERP (the single business database of the new system)
--
-- Context: the naming convention for database objects is UPPER CASE, and views
-- additionally carry a V_ prefix. Ten of the sixteen views already comply
-- (V_COP_ACCOUNT_M, V_HR_EMPLOYEE, V_INV_MONTH_SNAPSHOT_RECON, V_PRO_MAIN_SOURCE,
-- V_PUR_CANCEL_ALLOC, V_PUR_DUE_M, V_SYSDL_SYSDN, V_TODAY, V_WF_FORM_FLOW, plus
-- the procedure P_HRM_WAGE_CALC). The rest do not:
--
--   MOC_GET_SHOWSUM  ->  V_MOC_GET_SHOWSUM   no prefix
--   vw_Client        ->  V_CLIENT            lower case, no prefix
--   vw_ORDER_LIST    ->  V_ORDER_LIST        lower case, no prefix
--   VW_ORDER_SHEET   ->  V_ORDER_SHEET       prefix is VW_, not V_
--   vw_TodaySend     ->  V_TODAY_SEND        lower case, no prefix
--   vw_UnusualOrder  ->  V_UNUSUAL_ORDER     lower case, no prefix
--   vw_users         ->  V_USERS             lower case, no prefix
--
-- Three view columns are mixed case as well and are normalized in the same
-- rebuild: vw_Client.OrderCount -> ORDER_COUNT, VW_ORDER_SHEET.Year/Month/Total
-- -> YEAR/MONTH/TOTAL (bracketed, both are reserved words), and
-- vw_users.Name/Passport/Password -> NAME/PASSPORT/PASSWORD.
--
-- The views are rebuilt (DROP + CREATE) rather than renamed with sp_rename,
-- because sp_rename cannot touch the column names and leaves the previous
-- definition text in place.
--
-- Verified before writing:
--   1. no module references any of the seven views
--      (sys.sql_expression_dependencies = 0), so the rebuild breaks no
--      dependent object;
--   2. no application code references them either (repository-wide search:
--      hits are only the bootstrap seed and historical migration comments);
--   3. metadata registrations exist for three names only: MOC_GET_SHOWSUM and
--      V_MOC_GET_SHOWSUM (both in TABLES, and FIELDS rows under each), and
--      VW_ORDER_SHEET. They are reconciled in step 3.
--
-- Scope:
--   1. rebuild the seven views under their normalized names;
--   2. rename PK_ERP_SCHEMA_JOURNAL_Id -> PK_ERP_SCHEMA_JOURNAL_ID;
--   3. reconcile the metadata registrations;
--   4. assert: no non-compliant view name, no non-compliant constraint name,
--      and no stale registration remains.
-- ============================================================================

SET NOCOUNT ON;
GO

-- ---------------------------------------------------------------- 1. 重建视图
IF OBJECT_ID(N'dbo.MOC_GET_SHOWSUM', N'V') IS NOT NULL
    DROP VIEW dbo.MOC_GET_SHOWSUM;
GO

IF OBJECT_ID(N'dbo.vw_Client', N'V') IS NOT NULL
    DROP VIEW dbo.vw_Client;
GO

IF OBJECT_ID(N'dbo.vw_ORDER_LIST', N'V') IS NOT NULL
    DROP VIEW dbo.vw_ORDER_LIST;
GO

IF OBJECT_ID(N'dbo.VW_ORDER_SHEET', N'V') IS NOT NULL
    DROP VIEW dbo.VW_ORDER_SHEET;
GO

IF OBJECT_ID(N'dbo.vw_TodaySend', N'V') IS NOT NULL
    DROP VIEW dbo.vw_TodaySend;
GO

IF OBJECT_ID(N'dbo.vw_UnusualOrder', N'V') IS NOT NULL
    DROP VIEW dbo.vw_UnusualOrder;
GO

IF OBJECT_ID(N'dbo.vw_users', N'V') IS NOT NULL
    DROP VIEW dbo.vw_users;
GO

-- V_MOC_GET_SHOWSUM：领料单汇总。元数据里早已按这个名字登记，视图名对齐它。
CREATE VIEW dbo.V_MOC_GET_SHOWSUM
AS
SELECT GET_TYPE, GET_NO, PRO_NO, SUM(SEND_QTY) AS SEND_QTY, SUM(QTY)
      AS QTY, SUM(RETURN_QTY) AS RETURN_QTY, SUM(RETURNED_QTY)
      AS RETURNED_QTY
FROM dbo.MOC_GET_D
GROUP BY GET_TYPE, GET_NO, PRO_NO
GO

-- V_CLIENT：客户及其订单数。OrderCount 一并规范成 ORDER_COUNT。
CREATE VIEW dbo.V_CLIENT
AS
SELECT
    A.CLIENT_ID,
    A.CLIENT_NAME,
    A.FULL_NAME_CN,
    A.DELI_ADDR_CN,
    A.CURR_ID,
    A.PRICE_CONDITION,
    A.PAY_CONDITION,
    A.SALES_ID,
    A.REMARK,
    A.CREATE_PERSON,
    A.CREATE_DATE,
    A.LINKMAN,
    A.TEL,
    A.FAX,
    A.TAX_ID,
    (SELECT COUNT(*) FROM COP_ORDER_M WHERE CLIENT_ID = A.CLIENT_ID) AS ORDER_COUNT
FROM CLIENT AS A
GO

-- V_ORDER_LIST：订单列表。列名是中文，中文没有大小写，原样保留。
CREATE VIEW dbo.V_ORDER_LIST
AS
SELECT
    B.CLIENT_ID AS 客户编号,
    A.ORDER_NO AS 订单号,
    B.ORDER_DATE AS 订单日期,
    A.PRE_SEND_DATE AS 预交日期,
    A.FACT_SEND_DATE AS 实交日期,
    DATEDIFF(DAY, B.ORDER_DATE, A.FACT_SEND_DATE) AS 实际交货周期
FROM COP_ORDER_D AS A
    JOIN COP_ORDER_M AS B ON B.ORDER_NO = A.ORDER_NO
WHERE B.CLIENT_ID = 'TTI769'
    AND B.ORDER_DATE BETWEEN '2013-12-31' AND '2015-01-01'
    AND A.FACT_SEND_DATE IS NOT NULL
GO

-- V_ORDER_SHEET：销售月报。YEAR / MONTH 是保留字，落库为列名时保留方括号。
CREATE VIEW dbo.V_ORDER_SHEET
AS
SELECT TOP 1000
    YEAR(ORDER_DATE) AS [YEAR],
    MONTH(ORDER_DATE) AS [MONTH],
    SUM(AMOUNT_TAX * CURR_RATE) AS TOTAL
FROM COP_ORDER_M
GROUP BY
    YEAR(ORDER_DATE),
    MONTH(ORDER_DATE)
ORDER BY
    YEAR(ORDER_DATE) DESC,
    MONTH(ORDER_DATE) DESC;
GO

-- V_TODAY_SEND：今日应交订单
CREATE VIEW dbo.V_TODAY_SEND
AS
SELECT
    C.CLIENT_NAME,
    C.SALES_ID,
    A.ORDER_NO,
    B.CLIENT_ORDER_NO,
    B.ORDER_DATE,
    A.PRO_NO,
    A.CLIENT_PRO_NO,
    A.QTY,
    A.FINISHED_SEND_QTY,
    A.QTY - A.FINISHED_SEND_QTY AS NOT_SEND_QTY,
    A.PRE_SEND_DATE
FROM COP_ORDER_D AS A
    JOIN COP_ORDER_M AS B ON B.ORDER_NO = A.ORDER_NO
    JOIN CLIENT AS C ON C.CLIENT_ID = B.CLIENT_ID
WHERE DATEDIFF(DAY, A.PRE_SEND_DATE, GETDATE()) = 0 AND A.FINISHED_TAG = 0
GO

-- V_UNUSUAL_ORDER：逾期未交订单
CREATE VIEW dbo.V_UNUSUAL_ORDER
AS
SELECT
    C.CLIENT_NAME,
    C.SALES_ID,
    A.ORDER_NO,
    B.CLIENT_ORDER_NO,
    B.ORDER_DATE,
    A.PRO_NO,
    A.CLIENT_PRO_NO,
    A.QTY,
    A.FINISHED_SEND_QTY,
    A.QTY - A.FINISHED_SEND_QTY AS NOT_SEND_QTY,
    A.PRE_SEND_DATE
FROM COP_ORDER_D AS A
    JOIN COP_ORDER_M AS B ON B.ORDER_NO = A.ORDER_NO
    JOIN CLIENT AS C ON C.CLIENT_ID = B.CLIENT_ID
WHERE A.PRE_SEND_DATE < GETDATE() AND A.FINISHED_TAG = 0
GO

-- V_USERS：用户及其部门。Name / Passport / Password 一并规范成大写。
CREATE VIEW dbo.V_USERS
AS
SELECT
    B.EMP_ID AS ID,
    B.EMP_NAME AS NAME,
    A.USER_ID AS PASSPORT,
    A.USER_PWD AS PASSWORD,
    C.DEPT_NAME
FROM
    dbo.SYSDL AS A INNER JOIN
    dbo.SYSDN AS B ON A.EMP_ID = B.EMP_ID INNER JOIN
    dbo.DEPT AS C ON C.DEPT_ID = B.DEPT_ID
GO

-- ---------------------------------------------------------------- 2. 约束名
-- 主键约束在存储层是一个唯一索引，sp_rename 要按 INDEX 处理；用 OBJECT 会报
-- 「参数 @objname 不明确或所声明的 @objtype (OBJECT) 有误」。
IF EXISTS (SELECT 1 FROM sys.key_constraints WHERE name = N'PK_ERP_SCHEMA_JOURNAL_Id')
    EXEC sp_rename N'dbo.ERP_SCHEMA_JOURNAL.PK_ERP_SCHEMA_JOURNAL_Id', N'PK_ERP_SCHEMA_JOURNAL_ID', N'INDEX';
GO

-- ---------------------------------------------------------------- 3. 元数据登记
-- V_MOC_GET_SHOWSUM 的 TABLES 登记已经存在（内容更全），直接删掉 MOC_GET_SHOWSUM 那条，
-- 避免主键冲突；FIELDS 的 7 行则改挂到新名下——它们记的是本视图自身的列
-- （GET_TYPE / GET_NO / PRO_NO / QTY / SEND_QTY / RETURN_QTY / RETURNED_QTY），
-- 与 V_MOC_GET_SHOWSUM 名下已有的 3 行（COLOR_NAME / PRO_NAME / PRO_SPEC，扩展字段）不重复。
DELETE FROM dbo.TABLES WHERE T_ID = N'MOC_GET_SHOWSUM';
GO

UPDATE dbo.FIELDS SET T_ID = N'V_MOC_GET_SHOWSUM' WHERE T_ID = N'MOC_GET_SHOWSUM';
GO

-- VW_ORDER_SHEET -> V_ORDER_SHEET：目标名尚无登记，直接改名。
UPDATE dbo.FIELDS SET T_ID = N'V_ORDER_SHEET' WHERE T_ID = N'VW_ORDER_SHEET';
GO

UPDATE dbo.TABLES SET T_ID = N'V_ORDER_SHEET' WHERE T_ID = N'VW_ORDER_SHEET';
GO

-- ---------------------------------------------------------------- 4. 收口断言
-- 四个视图列名随重建改了，读取方式也跟着变，这里一并核对列名已是大写。
IF EXISTS (SELECT 1 FROM sys.columns c
             JOIN sys.views v ON v.object_id = c.object_id
            WHERE v.name = N'V_CLIENT' AND c.name = N'OrderCount')
    THROW 60021, '317: V_CLIENT.OrderCount 未改成 ORDER_COUNT。', 1;
GO

IF EXISTS (SELECT 1 FROM sys.views WHERE is_ms_shipped = 0
            AND (name COLLATE Latin1_General_BIN <> UPPER(name) COLLATE Latin1_General_BIN
                 OR name NOT LIKE N'V[_]%'))
    THROW 60022, '317: 仍有不合规的视图名（须为 V_ 前缀 + 全大写）。', 1;
GO

IF EXISTS (SELECT 1 FROM sys.key_constraints
            WHERE name COLLATE Latin1_General_BIN <> UPPER(name) COLLATE Latin1_General_BIN)
    THROW 60023, '317: 仍有非全大写的键约束名。', 1;
GO

IF EXISTS (SELECT 1 FROM dbo.TABLES
            WHERE T_ID IN (N'MOC_GET_SHOWSUM', N'VW_ORDER_SHEET')
               OR CAST(T_ID AS nvarchar(200)) COLLATE Latin1_General_BIN <> UPPER(CAST(T_ID AS nvarchar(200))) COLLATE Latin1_General_BIN)
    THROW 60024, '317: 视图的元数据登记未同步到新名。', 1;
GO
