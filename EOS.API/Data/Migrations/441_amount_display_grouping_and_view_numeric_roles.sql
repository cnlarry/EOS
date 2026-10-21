-- ============================================================================
-- EOS.ERP migration 441: 金额显示口径统一（千分位 + 固定两位）并补回视图/虚拟列缺失的数值语义族
-- ----------------------------------------------------------------------------
-- 背景一（显示口径不一致）：金额的显示格式有三处来源（ADR-037 决定六的回落链）——
-- 字段 DISPLAY_FORMAT 显式配置 → 语义族默认 → 原始值。库内当下的分布并不一致：
--   · NUMERIC_ROLE='AMOUNT' 且格式留空：走语义族默认，800 显示 800.00，带千分位；
--   · 同族但格式写成 `0.00`：显式格式优先，800.00 不带千分位（`1,234.50` 显示成 1234.50）；
--   · 非金额语义族、格式留空：按"其余数值"回落，800 显示 800、800.1 显示 800.1（去尾零）。
-- 第三类正是字段掉语义族之后的表现（见背景二），前两类则是 `0.00` 与 `#,##0.00` 两种
-- 写法长期并存。同一个列表里并排的金额列因此会出现"一个有千分位去尾零、一个两位小数不
-- 分组"的两种口径。迁移 425 已经把去尾零的金额格式收敛为 `#,##0.00`，只是当时的判据
-- 没包含 `0.00`（固定两位但不分组）。
--
-- 背景二（字段丢语义族）：FIELDS 是**按 T_ID 独立登记**的，语义族不会跟着数据走。
-- 两种建法都会漏掉 NUMERIC_ROLE：
--   · 视图：迁移 339 把 COP_ORDER_D 的字段元数据整表复制成 V_COP_ORDER_D_UNDELIVERED
--     的字段行，复制列表里没有 NUMERIC_ROLE（迁移 318 手工登记的三张对账视图同样没登记，
--     它们只有数量列，显示口径恰好与"其余数值"一致，所以一直没暴露）；
--   · 虚拟列：`NOT_RECEIVE_AMOUNT` / `NOT_PAYOUT_AMOUNT` / `NOT_FINISHED_AMOUNT` /
--     `END_AMOUNT` 这类由表达式算出来的金额列（VIRTUAL_EXP），历来只登记了格式没登记语义。
-- 两者共同的结果：金额掉进"其余数值"回落，`1.000` 显示成 `1`、`1234.5` 显示成 `1234.5`。
--
-- 做法（三步都是元数据，不碰任何业务数据）：
--   1) 虚拟金额列逐个登记 NUMERIC_ROLE='AMOUNT'（它们没有源表可复制，只能具名登记）；
--   2) 复制型视图按显式「视图 ← 源表」映射补回语义族，只补当前为空的，不覆盖人工值；
--   3) 金额（NUMERIC_ROLE='AMOUNT'）的显示格式统一为 `#,##0.00`，沿用迁移 425 的口径：
--      一次收敛 `0.00` / `0.#` / `0.##` / `#.#` / `#.##`，另把两个差额列的 `0.###`
--      （未预收金额 / 未预付款额）显式收成两位——它们是"金额 − 已付"，与其余金额同口径。
-- 显示格式只影响屏幕显示：排序、筛选、分组、合计、导出一律用原始值（ADR-037 决定七），
-- 因此本迁移不改变任何计算结果与落库值。
--
-- 刻意不动 4 个工资列：HR_WAGE_D.WAGE_ITEMA5/A7、HRM_WAGE_D.WAGE_ITEMA7 写的是 `0`
-- （无小数），HRM_WAGE_D.WAGE_ITEM02「小时工资」写的是 `0.###`（保留三位、去尾零）——
-- 它们是金额语义族之外的显式选择，收敛与否另属业务口径，不在本迁移内改。
--
-- 幂等：第二次执行三步都改 0 行，不重复标脏。
-- 回滚：把 DISPLAY_FORMAT 改回原值、视图与虚拟列的 NUMERIC_ROLE 置空（不建议：那正是
-- 本迁移要消除的不一致）。
-- ============================================================================

SET NOCOUNT ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET ARITHABORT ON;
SET XACT_ABORT ON;

DECLARE @GUARD_MESSAGE NVARCHAR(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 52840, @GUARD_MESSAGE, 1;

BEGIN TRANSACTION;

/* ---------------------------------------------------------------------------
   1. 虚拟金额列：没有源表可复制，逐个具名登记语义族
   --------------------------------------------------------------------------- */
DECLARE @VirtualAmount TABLE (T_ID NVARCHAR(100) NOT NULL, F_ID NVARCHAR(100) NOT NULL,
                              PRIMARY KEY (T_ID, F_ID));

INSERT @VirtualAmount (T_ID, F_ID)
VALUES (N'CAR',                     N'LAST_AMOUNT'),                    -- 金额（CAR_OILCARD.LAST_AMOUNT）
       (N'COP_ACCOUNT_DD_M',        N'NOT_RECEIVE_AMOUNT'),             -- 未收款金额
       (N'COP_ACCOUNT_M',           N'NOT_RECEIVE_AMOUNT'),             -- 未收款金额
       (N'COP_MONTH_D',             N'END_AMOUNT'),                     -- 期末金额
       (N'COP_MONTH_D',             N'END_AMOUNT_RMB'),                 -- 期末金额(RMB)
       (N'COP_ORDER_D',             N'NOT_FINISHED_AMOUNT'),            -- 未预收金额
       (N'COP_ORDER_D',             N'UNFINISHED_RECEIPT_AMOUNT'),      -- 未收金额
       (N'COP_PREPAY_M',            N'NOT_AMOUNT'),                     -- 未付款
       (N'COP_PREPAY_M',            N'NOT_PREPAY_AMOUNT'),              -- 可冲抵
       (N'CUS_PROCESS_INVOICE_D',   N'NOT_FINISHED_PROCESS_AMOUNT'),     -- 可开发票金额
       (N'INV_PRO_MONTH_D',         N'AMOUNT'),                         -- 金额
       (N'PUR_DUE_M',               N'NOT_PAYOUT_AMOUNT'),              -- 未付总计
       (N'PUR_MONTH_D',             N'END_AMOUNT'),                     -- 期末金额
       (N'PUR_MONTH_D',             N'END_AMOUNT_RMB'),                 -- 期末金额RMB
       (N'PUR_PREPAY_M',            N'NOT_AMOUNT'),                     -- 未付款
       (N'PUR_PURCHASE_D',          N'NOT_FINISHED_AMOUNT');            -- 未预付款额

UPDATE f
   SET f.NUMERIC_ROLE = N'AMOUNT',
       f.LAST_UPDATE_BY = N'EOS-MIG',
       f.LAST_UPDATE_DATE = SYSDATETIME()
  FROM dbo.FIELDS AS f
  JOIN @VirtualAmount AS v
    ON LTRIM(RTRIM(f.T_ID)) = v.T_ID
   AND LTRIM(RTRIM(f.F_ID)) = v.F_ID
 WHERE f.NUMERIC_ROLE IS NULL;

PRINT N'[441] 虚拟金额列登记语义族：' + CONVERT(NVARCHAR(10), @@ROWCOUNT) + N' 行';

/* ---------------------------------------------------------------------------
   2. 复制型视图补回语义族
   映射是显式的：「视图 ← 它的源表」。不按名字前缀推断——V_COP_ORDER_D_UNDELIVERED
   的源表是 COP_ORDER_D，去掉 V_ 前缀推不出来。
   --------------------------------------------------------------------------- */
DECLARE @ViewSource TABLE (VIEW_T NVARCHAR(100) PRIMARY KEY, SRC_T NVARCHAR(100) NOT NULL);

INSERT @ViewSource (VIEW_T, SRC_T)
VALUES (N'V_COP_ORDER_D_UNDELIVERED',     N'COP_ORDER_D'),
       (N'V_COP_ORDER_D_UNPRODUCED',      N'COP_ORDER_D'),
       (N'V_COP_SEND_D_UNRECONCILED',     N'COP_SEND_D'),
       (N'V_PUR_RECEIVE_D_UNRECONCILED',  N'PUR_RECEIVE_D'),
       (N'V_COP_ACCOUNT_M',               N'COP_ACCOUNT_M'),
       (N'V_PUR_DUE_M',                   N'PUR_DUE_M');

UPDATE f
   SET f.NUMERIC_ROLE = s.NUMERIC_ROLE,
       f.LAST_UPDATE_BY = N'EOS-MIG',
       f.LAST_UPDATE_DATE = SYSDATETIME()
  FROM dbo.FIELDS AS f
  JOIN @ViewSource AS m
    ON m.VIEW_T = LTRIM(RTRIM(f.T_ID))
  JOIN dbo.FIELDS AS s
    ON LTRIM(RTRIM(s.T_ID)) = m.SRC_T
   AND LTRIM(RTRIM(s.F_ID)) = LTRIM(RTRIM(f.F_ID))
 WHERE f.NUMERIC_ROLE IS NULL
   AND s.NUMERIC_ROLE IS NOT NULL;

PRINT N'[441] 视图字段补回语义族：' + CONVERT(NVARCHAR(10), @@ROWCOUNT) + N' 行';

/* ---------------------------------------------------------------------------
   3. 金额显示格式统一为 #,##0.00
   3a 判据一：格式写成"固定/可变小数位但不带千分位"的几种历史写法之一
   --------------------------------------------------------------------------- */
SELECT DISTINCT LTRIM(RTRIM(f.T_ID)) AS T_ID
  INTO #AMOUNT_TABLES
  FROM dbo.FIELDS AS f
 WHERE f.NUMERIC_ROLE = N'AMOUNT'
   AND LTRIM(RTRIM(ISNULL(f.DISPLAY_FORMAT, N''))) IN (N'0.00', N'0.#', N'0.##', N'#.#', N'#.##');

UPDATE f
   SET f.DISPLAY_FORMAT = N'#,##0.00',
       f.LAST_UPDATE_BY = N'EOS-MIG',
       f.LAST_UPDATE_DATE = SYSDATETIME()
  FROM dbo.FIELDS AS f
  JOIN #AMOUNT_TABLES AS t ON t.T_ID = LTRIM(RTRIM(f.T_ID))
 WHERE f.NUMERIC_ROLE = N'AMOUNT'
   AND LTRIM(RTRIM(ISNULL(f.DISPLAY_FORMAT, N''))) IN (N'0.00', N'0.#', N'0.##', N'#.#', N'#.##');

PRINT N'[441] 金额显示格式收敛：' + CONVERT(NVARCHAR(10), @@ROWCOUNT) + N' 行';

/* ---------------------------------------------------------------------------
   3b 判据二：两个差额列的 `0.###` 显式收成两位
   它们由"金额 − 已收/已付"算出，与其余金额同口径；工资类的 `0.###`（小时工资）不动。
   --------------------------------------------------------------------------- */
UPDATE f
   SET f.DISPLAY_FORMAT = N'#,##0.00',
       f.LAST_UPDATE_BY = N'EOS-MIG',
       f.LAST_UPDATE_DATE = SYSDATETIME()
  FROM dbo.FIELDS AS f
 WHERE f.NUMERIC_ROLE = N'AMOUNT'
   AND LTRIM(RTRIM(ISNULL(f.DISPLAY_FORMAT, N''))) = N'0.###'
   AND ((LTRIM(RTRIM(f.T_ID)) = N'COP_ORDER_D'    AND LTRIM(RTRIM(f.F_ID)) = N'NOT_FINISHED_AMOUNT')
     OR (LTRIM(RTRIM(f.T_ID)) = N'PUR_PURCHASE_D' AND LTRIM(RTRIM(f.F_ID)) = N'NOT_FINISHED_AMOUNT')
     OR (LTRIM(RTRIM(f.T_ID)) = N'V_COP_ORDER_D_UNDELIVERED' AND LTRIM(RTRIM(f.F_ID)) = N'NOT_FINISHED_AMOUNT'));

PRINT N'[441] 差额列 0.### 收敛：' + CONVERT(NVARCHAR(10), @@ROWCOUNT) + N' 行';

/* ---------------------------------------------------------------------------
   4. 标脏：只针对已发布过快照、且主表或明细表落在受影响的表上的模块。
   字段元数据在运行期实时重建，这一步不是为了"让显示生效"，而是让快照状态与元数据
   的变更事实保持可追溯（与迁移 425 / 440 的处理一致）。
   --------------------------------------------------------------------------- */
SELECT DISTINCT v.T_ID INTO #TOUCHED FROM (
    SELECT T_ID FROM #AMOUNT_TABLES
    UNION SELECT T_ID FROM @VirtualAmount
    UNION SELECT VIEW_T FROM @ViewSource
) AS v;

MERGE dbo.WORKBENCH_MODULE_DIRTY AS D
USING (SELECT DISTINCT s.M_IDX
         FROM dbo.WORKBENCH_DEFINITION_SNAPSHOT s
         JOIN dbo.MODULES mm ON mm.M_IDX = s.M_IDX
        WHERE s.IS_CURRENT = 1
          AND (LTRIM(RTRIM(ISNULL(mm.MASTER_TABLE, N''))) IN (SELECT T_ID FROM #TOUCHED)
            OR LTRIM(RTRIM(ISNULL(mm.DETAIL_TABLE, N''))) IN (SELECT T_ID FROM #TOUCHED))) AS S
   ON D.M_IDX = S.M_IDX
WHEN MATCHED THEN UPDATE SET D.DIRTY_TAG = 1, D.LAST_MODIFIED_BY = N'EOS-MIG', D.LAST_MODIFIED_AT = SYSDATETIME()
WHEN NOT MATCHED THEN INSERT (M_IDX, DIRTY_TAG, LAST_MODIFIED_BY, LAST_MODIFIED_AT)
    VALUES (S.M_IDX, 1, N'EOS-MIG', SYSDATETIME());

PRINT N'[441] 已标记待重新发布的模块：' + CONVERT(NVARCHAR(10), @@ROWCOUNT) + N' 个';

DROP TABLE #AMOUNT_TABLES;
DROP TABLE #TOUCHED;

/* ---------------------------------------------------------------------------
   5. 收口断言
   --------------------------------------------------------------------------- */
IF EXISTS (
    SELECT 1
      FROM dbo.FIELDS AS f
      JOIN @ViewSource AS m ON m.VIEW_T = LTRIM(RTRIM(f.T_ID))
      JOIN dbo.FIELDS AS s ON LTRIM(RTRIM(s.T_ID)) = m.SRC_T AND LTRIM(RTRIM(s.F_ID)) = LTRIM(RTRIM(f.F_ID))
     WHERE f.NUMERIC_ROLE IS NULL AND s.NUMERIC_ROLE IS NOT NULL)
    THROW 52841, N'复制型视图仍有字段缺 NUMERIC_ROLE（源表已登记），迁移终止。', 1;

IF EXISTS (
    SELECT 1
      FROM dbo.FIELDS AS f
     WHERE f.NUMERIC_ROLE = N'AMOUNT'
       AND LTRIM(RTRIM(ISNULL(f.DISPLAY_FORMAT, N''))) <> N''
       AND CHARINDEX(N',', f.DISPLAY_FORMAT) = 0
       AND NOT ((LTRIM(RTRIM(f.T_ID)) = N'HR_WAGE_D'  AND LTRIM(RTRIM(f.F_ID)) IN (N'WAGE_ITEMA5', N'WAGE_ITEMA7'))
             OR (LTRIM(RTRIM(f.T_ID)) = N'HRM_WAGE_D' AND LTRIM(RTRIM(f.F_ID)) IN (N'WAGE_ITEMA7', N'WAGE_ITEM02'))))
    THROW 52842, N'仍有金额字段的显式格式不带千分位（4 个工资列除外），迁移终止。', 1;

COMMIT;

PRINT N'[441] 完成：视图与虚拟金额列的语义族已补回，金额显示统一为 #,##0.00。';
