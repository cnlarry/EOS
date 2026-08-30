-- ============================================================================
-- EOS.ERP 迁移 027：最小语义层——REPORT_METRIC 度量定义表（ADR-009 §10.2/P7）
-- ----------------------------------------------------------------------------
-- 背景（2026-08-30）：报表系统中的度量口径散落在至少五处（REPORT.MASTER_TABLE、
-- P_RPT_* 存储过程、LISTREPORT.T_SQL、SYSQL.T_SQL、SYSQR_DEFAULT.F_EXPR），
-- 没有任何一处定义"销售额 = 什么"。P7 在库内建最小度量定义表，使度量口径
-- 「定义一次、机器可读、多处消费」，供报表与 Agent 共同消费。
--
-- 设计原则（§10.2 决策）：
--   1. 不自建查询引擎、不引入 Cube/dbt 等外部语义层产品；
--   2. 度量定义本身属开发态（§1），进 DbUp 版本化迁移；
--   3. 准入条件：先有高频争议口径清单（来自 T_SQL 审计），再建表；
--   4. 口径定义 = 口径名 + SQL 表达式 + 可用维度 + 业务域 + 版本。
--
-- 全大写命名（AGENTS.md 强制）；幂等：IF NOT EXISTS 守卫。
-- T_SQL 审计报告落 logs/report-metric-audit/。
-- ============================================================================

SET NOCOUNT ON;

DECLARE @GUARD_MESSAGE NVARCHAR(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, @GUARD_MESSAGE, 1;

BEGIN TRANSACTION;

-- 1. REPORT_METRIC
IF OBJECT_ID('dbo.REPORT_METRIC') IS NULL
BEGIN
    CREATE TABLE dbo.REPORT_METRIC (
        [METRIC_ID] NVARCHAR(50) NOT NULL CONSTRAINT [PK_REPORT_METRIC] PRIMARY KEY,   -- 口径标识（如 sales_amount）
        [METRIC_NAME] NVARCHAR(100) NOT NULL,                                           -- 口径名称（如 "销售额"）
        [DEFINITION] NVARCHAR(MAX) NOT NULL,                                            -- 口径定义（SQL 表达式，如 SUM(AMOUNT_TAX)）
        [SOURCE_TABLE] NVARCHAR(200) NULL,                                              -- 来源表（如 COP_ORDER_D）
        [DIMENSION_KEYS] NVARCHAR(500) NULL,                                            -- 可用维度（逗号分隔，如 ORDER_DATE,CLIENT_ID）
        [DOMAIN] NVARCHAR(50) NULL,                                                     -- 业务域（如 销售/采购/库存/生产/财务）
        [VERSION] INT NOT NULL CONSTRAINT [DF_REPORT_METRIC_VERSION] DEFAULT (1),        -- 版本号
        [DESCRIPTION] NVARCHAR(500) NULL,                                               -- 描述
        [CREATE_PERSON] NCHAR(40) NULL,
        [CREATE_DATE] DATETIME2 NOT NULL CONSTRAINT [DF_REPORT_METRIC_CREATED] DEFAULT (SYSDATETIME()),
        [LAST_UPDATE_BY] NCHAR(40) NULL,
        [LAST_UPDATE_DATE] DATETIME2 NULL
    );
END

-- 2. 种子口径（高频、可从字段名直接确认的通用口径）
-- 注：P7 风险登记——"业务共识"是真正成本，以下仅为从表结构推断的候选口径，
-- 待业务确认后修正/补充（不阻塞 P7 落地，但除种子口额外不保证口径准确性）。
IF NOT EXISTS (SELECT 1 FROM dbo.REPORT_METRIC)
BEGIN
    INSERT INTO dbo.REPORT_METRIC (METRIC_ID, METRIC_NAME, DEFINITION, SOURCE_TABLE, DIMENSION_KEYS, DOMAIN, VERSION, DESCRIPTION) VALUES
        (N'sales_amount',      N'销售额',          N'SUM(AMOUNT_TAX)',       N'COP_ORDER_D', N'ORDER_DATE,CLIENT_ID,PRO_NO',            N'销售', 1, N'订单明细含税金额合计'),
        (N'sales_qty',         N'销售数量',         N'SUM(QTY)',              N'COP_ORDER_D', N'ORDER_DATE,CLIENT_ID,PRO_NO',            N'销售', 1, N'订单明细数量合计'),
        (N'sales_discount',    N'销售折扣',         N'SUM(REBATE)',           N'COP_ORDER_D', N'ORDER_DATE,CLIENT_ID,PRO_NO',            N'销售', 1, N'订单明细折扣合计'),
        (N'sales_amount_ex',   N'销售额（未税）',   N'SUM(AMOUNT)',           N'COP_ORDER_D', N'ORDER_DATE,CLIENT_ID,PRO_NO',            N'销售', 1, N'订单明细未税金额合计'),
        (N'purchase_amount',   N'采购金额',         N'SUM(AMOUNT_TAX)',       N'PUR_PURCHASE_D', N'PURCHASE_DATE,SUPPLIER_ID,PRO_NO',     N'采购', 1, N'采购明细含税金额合计'),
        (N'purchase_qty',      N'采购数量',         N'SUM(QTY)',              N'PUR_PURCHASE_D', N'PURCHASE_DATE,SUPPLIER_ID,PRO_NO',     N'采购', 1, N'采购明细数量合计'),
        (N'receive_qty',       N'收料数量',         N'SUM(RECEIVE_QTY)',      N'PUR_RECEIVE_D', N'RECEIVE_DATE,SUPPLIER_ID,PRO_NO',       N'采购', 1, N'收料明细数量合计'),
        (N'inventory_qty',     N'库存数量',         N'SUM(QTY)',              N'INV_PRO_DEPOT', N'DEPOT_ID,PRO_NO',                      N'库存', 1, N'产品库存数量合计'),
        (N'produce_qty',       N'生产数量',         N'SUM(QTY)',              N'MOC_PRODUCE_D', N'PRODUCE_DATE,PRO_NO',                   N'生产', 1, N'制令单生产数量合计'),
        (N'produce_finished_qty', N'完工数量',      N'SUM(FINISHED_QTY)',     N'MOC_PRODUCE_D', N'PRODUCE_DATE,PRO_NO',                   N'生产', 1, N'制令单完工数量合计'),
        (N'account_receivable', N'应收账款',        N'SUM(AMOUNT_TAX)',       N'COP_ACCOUNT_D', N'ACCOUNT_DATE,CLIENT_ID',                N'财务', 1, N'应收账款明细含税金额合计'),
        (N'account_payable',   N'应付账款',         N'SUM(AMOUNT_TAX)',       N'PUR_DUE_D', N'DUE_DATE,SUPPLIER_ID',                   N'财务', 1, N'应付账款明细含税金额合计'),
        (N'receipt_amount',    N'收款金额',         N'SUM(AMOUNT)',           N'COP_RECEIPT_D', N'RECEIPT_DATE,CLIENT_ID',                N'财务', 1, N'收款明细金额合计'),
        (N'payment_amount',    N'付款金额',         N'SUM(AMOUNT)',           N'PUR_PAY_D', N'PAY_DATE,SUPPLIER_ID',                   N'财务', 1, N'付款明细金额合计'),
        (N'sales_gross_margin', N'销售毛利',        N'SUM(AMOUNT_TAX - COST_AMOUNT)', N'COP_ORDER_D', N'ORDER_DATE,CLIENT_ID', N'销售', 1, N'销售金额 - 成本金额'),
        (N'inventory_turnover', N'库存周转率',      N'SUM(QTY) / NULLIF(SUM(IN_QTY),0)', N'INV_PRO_DEPOT', N'DEPOT_ID,PRO_NO', N'库存', 1, N'库存数量/入库数量（周转率指标）'),
        (N'employee_count',    N'员工人数',         N'COUNT(DISTINCT EMP_ID)', N'HR_EMPLOYEE', N'DEPT_ID',                             N'人事', 1, N'在册员工人数'),
        (N'order_count',       N'订单数',           N'COUNT(DISTINCT ORDER_NO)', N'COP_ORDER_D', N'ORDER_DATE,CLIENT_ID',              N'销售', 1, N'客户订单数量'),
        (N'purchase_order_count', N'采购单数',      N'COUNT(DISTINCT PURCHASE_NO)', N'PUR_PURCHASE_D', N'PURCHASE_DATE,SUPPLIER_ID', N'采购', 1, N'采购订单数量');
END

-- 3. 审计计数
SELECT 'REPORT_METRIC' AS KIND, COUNT(*) AS CNT FROM dbo.REPORT_METRIC;

COMMIT TRANSACTION;