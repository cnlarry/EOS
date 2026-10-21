-- ============================================================================
-- EOS.ERP migration 442: 所有数值列的显示格式带千分位
-- ----------------------------------------------------------------------------
-- 背景：显示格式的回落链是「字段 DISPLAY_FORMAT → 语义族默认 → 原始值」（ADR-037 决定六），
-- 而语义族默认里只有金额被特殊处理（固定两位），"其余数值"的默认本来就带千分位。
-- 但显式格式优先：只要字段上写了 `0.00` / `0.##` / `#` 这类**不含千分位占位符**的写法，
-- 分组就被盖掉，数量于是显示成 600000.00、1128200，而不是 600,000.00、1,128,200。
--
-- 迁移 441 只按 `NUMERIC_ROLE='AMOUNT'` 扫了一遍，数量、单价、汇率、百分比没有纳入，
-- 结果同一张单据里金额有千分位、数量没有。本迁移把判据扩到**所有数值列**：
-- 显式格式非空且不含 `,` 的，一律改写为它的"分组等价写法"——
--   · 以 `0` 开头：前置 `#,##`（`0.00` → `#,##0.00`、`0.##` → `#,##0.##`、`0` → `#,##0`）
--   · 以 `#` 开头：把首个 `#` 换成 `#,##`（`#` → `#,##`、`#.##` → `#,##.##`）
-- 这是**纯插入分隔符**的变换：小数位数、舍入行为、以及"全可选位格式在 0 时显示空白"
-- 的语义都保持不变，屏幕上除逗号外没有任何差别。因此连 441 里保留的 4 个工资列
-- （`0` / `0.###`）也一并纳入，不再有例外。
--
-- 只改 FIELDS 的显示格式：排序、筛选、分组、合计、导出一律用原始值（ADR-037 决定七）。
-- 本迁移不动 NUMERIC_ROLE——给可写的数量列登记 QTY 会按单位精度拒绝超出小数的输入
-- （`RecordPayloadValidator.CheckNumericScale` 返回 SCALE_EXCEEDED），那是校验口径变更，
-- 与显示无关，不在本迁移内。
--
-- 幂等：第二次执行改 0 行，不重复标脏。
-- 回滚：把 DISPLAY_FORMAT 里的 `#,##` 前缀还原（不建议：那正是本迁移要消除的不一致）。
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
    THROW 52850, @GUARD_MESSAGE, 1;

BEGIN TRANSACTION;

/* ---------------------------------------------------------------------------
   0. 前置：待改格式必须以 `0` 或 `#` 开头（.NET 自定义数字格式）。
   标准格式串（N2 / F2 …）与分号分段（正;负;零）不在本迁移的判据内——出现即终止，
   由人来决定怎么改写，避免自动变换把语义改掉。
   --------------------------------------------------------------------------- */
IF EXISTS (
    SELECT 1
      FROM dbo.FIELDS AS f
     WHERE LOWER(ISNULL(f.F_TYPE, N'')) IN (N'decimal', N'numeric', N'float', N'money', N'smallmoney', N'real')
       AND LTRIM(RTRIM(ISNULL(f.DISPLAY_FORMAT, N''))) <> N''
       AND CHARINDEX(N',', f.DISPLAY_FORMAT) = 0
       AND (LEFT(LTRIM(f.DISPLAY_FORMAT), 1) NOT IN (N'0', N'#') OR CHARINDEX(N';', f.DISPLAY_FORMAT) > 0))
    THROW 52851, N'存在既不含千分位、又不是 0/# 开头的数值格式，需要人工确定改写方式，迁移终止。', 1;

/* ---------------------------------------------------------------------------
   1. 数值列显示格式统一带千分位
   --------------------------------------------------------------------------- */
UPDATE f
   SET f.DISPLAY_FORMAT = CASE
           WHEN LEFT(LTRIM(f.DISPLAY_FORMAT), 1) = N'0' THEN N'#,##' + LTRIM(RTRIM(f.DISPLAY_FORMAT))
           ELSE N'#,##' + SUBSTRING(LTRIM(RTRIM(f.DISPLAY_FORMAT)), 2, 4000)
       END,
       f.LAST_UPDATE_BY = N'EOS-MIG',
       f.LAST_UPDATE_DATE = SYSDATETIME()
  FROM dbo.FIELDS AS f
 WHERE LOWER(ISNULL(f.F_TYPE, N'')) IN (N'decimal', N'numeric', N'float', N'money', N'smallmoney', N'real')
   AND LTRIM(RTRIM(ISNULL(f.DISPLAY_FORMAT, N''))) <> N''
   AND CHARINDEX(N',', f.DISPLAY_FORMAT) = 0;

PRINT N'[442] 数值显示格式加千分位：' + CONVERT(NVARCHAR(10), @@ROWCOUNT) + N' 行';

/* ---------------------------------------------------------------------------
   2. 标脏：只针对已发布过快照、且主表或明细表落在受影响的表上的模块
   --------------------------------------------------------------------------- */
SELECT DISTINCT LTRIM(RTRIM(f.T_ID)) AS T_ID
  INTO #TOUCHED
  FROM dbo.FIELDS AS f
 WHERE LOWER(ISNULL(f.F_TYPE, N'')) IN (N'decimal', N'numeric', N'float', N'money', N'smallmoney', N'real')
   AND LTRIM(RTRIM(ISNULL(f.DISPLAY_FORMAT, N''))) LIKE N'#,##%';

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

PRINT N'[442] 已标记待重新发布的模块：' + CONVERT(NVARCHAR(10), @@ROWCOUNT) + N' 个';

DROP TABLE #TOUCHED;

/* ---------------------------------------------------------------------------
   3. 收口断言：数值列不再有"非空且不含千分位"的显式格式
   --------------------------------------------------------------------------- */
IF EXISTS (
    SELECT 1
      FROM dbo.FIELDS AS f
     WHERE LOWER(ISNULL(f.F_TYPE, N'')) IN (N'decimal', N'numeric', N'float', N'money', N'smallmoney', N'real')
       AND LTRIM(RTRIM(ISNULL(f.DISPLAY_FORMAT, N''))) <> N''
       AND CHARINDEX(N',', f.DISPLAY_FORMAT) = 0)
    THROW 52852, N'仍有数值列的显式格式不带千分位，迁移终止。', 1;

COMMIT;

PRINT N'[442] 完成：所有数值列的显式显示格式都带千分位。';
