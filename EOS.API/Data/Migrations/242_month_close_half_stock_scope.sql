-- ============================================================================
-- EOS.ERP migration 242: 月结范围是否含半成品（按制程）账（D3 / WS-18）
-- ----------------------------------------------------------------------------
-- 参数 `MONTH_CLOSE_SCOPE_HALF_STOCK`：**部署级**参数（与 MONTH_CLOSE_BY_* 同款，只取 DEPOT_ID = '*' 那行），
-- 语义 = "月结范围是否含半成品（按制程）账"，**默认 0（关）**。
--
-- 为什么默认关，以及"关"到底是什么意思（照代码实况写，不照文档想象）：
--   · 半成品账 `HALF_PRO_DEPOT`（键 PRO_NO + PROCEDURE_TYPE_ID + DEPOT_ID）**不写库存流水**
--     （`HalfStockMoveHandler` 只动余额表那一行），所以它的历史无法回溯——这一点决定了
--     "让它进月结快照"必须另想办法（无流水可加：快照只能直取余额，且期末必须落在生成当天）。
--   · 该工作（快照侧按制程展开）是 WS-18b；**在其落地前本参数只允许关**（保存路径会拒"开"，
--     见 DepotStockPolicyService.ValidateMonthCloseScope），免得出现"拦了但快照没有"的不对称。
--   · 参数为关时：半成品账**既不拦、也不快照**——与"这个参数没打开"是同一句话，
--     也就是今天代码的真实行为（注意： 原文写"今天是无条件拦 2603/2604 但不快照"，
--     那是**定性描述与代码不符**：拦截代码只挂在主账移动引擎上，半成品路径今天根本没被拦）。
--
-- 幂等：列存在即跳过；默认行不存在则报错（策略求值与月结口径都依赖它）。
-- ============================================================================

IF COL_LENGTH('dbo.DEPOT_STOCK_POLICY', 'MONTH_CLOSE_SCOPE_HALF_STOCK') IS NULL
BEGIN
    ALTER TABLE dbo.DEPOT_STOCK_POLICY
        ADD MONTH_CLOSE_SCOPE_HALF_STOCK BIT NOT NULL
            CONSTRAINT DF_DEPOT_STOCK_POLICY_MONTH_HALF DEFAULT (0);
    PRINT N'== 新增列 DEPOT_STOCK_POLICY.MONTH_CLOSE_SCOPE_HALF_STOCK（BIT，默认 0）==';
END
ELSE
    PRINT N'== 列 MONTH_CLOSE_SCOPE_HALF_STOCK 已存在，跳过 ==';

-- 扩展属性：列语义写在库里，看结构的人不用去翻 ADR
IF NOT EXISTS (SELECT 1 FROM sys.extended_properties
                WHERE major_id = OBJECT_ID(N'dbo.DEPOT_STOCK_POLICY')
                  AND minor_id = COLUMNPROPERTY(OBJECT_ID(N'dbo.DEPOT_STOCK_POLICY'), N'MONTH_CLOSE_SCOPE_HALF_STOCK', 'ColumnId')
                  AND name = N'MS_Description')
    EXEC sys.sp_addextendedproperty
        @name = N'MS_Description',
        @value = N'月结范围是否含半成品（按制程）账：1=含（拦与快照一起生效，快照侧见 WS-18b）；0=不含（既不拦也不快照）。仅部署级行有效。',
        @level0type = N'SCHEMA', @level0name = N'dbo',
        @level1type = N'TABLE', @level1name = N'DEPOT_STOCK_POLICY',
        @level2type = N'COLUMN', @level2name = N'MONTH_CLOSE_SCOPE_HALF_STOCK';

-- 核对：部署级默认行必须在（策略求值与月结口径都从它取值）
IF NOT EXISTS (SELECT 1 FROM dbo.DEPOT_STOCK_POLICY WHERE DEPOT_ID = N'*')
    THROW 52130, N'缺少部署级默认行 DEPOT_STOCK_POLICY(''*'')，月结范围参数无处可放，迁移中止。', 1;

-- 取值打印走**动态 SQL**：整批是一次性解析的，同一批次里直接引用刚 ADD 的列会在解析期报
-- "列名无效"（迁移 185 用的是分批执行，这里不依赖那个约定）。
EXEC (N'DECLARE @half BIT = (SELECT MONTH_CLOSE_SCOPE_HALF_STOCK FROM dbo.DEPOT_STOCK_POLICY WHERE DEPOT_ID = N''*'');
PRINT CONCAT(N''== 就位：部署级 MONTH_CLOSE_SCOPE_HALF_STOCK = '', @half,
             N''（0=不含半成品账：既不拦也不快照；参数只允许关，直到快照侧落地）=='');');
