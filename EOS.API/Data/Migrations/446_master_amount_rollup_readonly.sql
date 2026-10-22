-- 主表金额汇总列改为只读。
--
-- 汇总规则（运行期由 MasterAmountRollup 发现，见 EOS.API/Data/Workbench/MasterAmountRollup.cs）：
-- 主表列 C 与明细同名列 C 之间，名称相同、数据类型相同、且两侧在 FIELDS 都登记为金额角色
-- （NUMERIC_ROLE='AMOUNT'），即认定「主表 C = SUM(明细 C)」。
--
-- 这些列由框架在保存、单据动作、批核/解批的收尾统一回写。允许用户手填是"假可写"：
-- 填进去的值会被随后的汇总覆盖，用户看到的却是"我填了有效"。故一律置为只读。
--
-- 与旧实现的关系：原先代码里写死 AMOUNT/AMOUNT_TAX/TAX_SUM 三个列名，别名金额列
-- （如 CUS_MANUAL_M.PROCESS_AMOUNT）从未被汇总；改为按元数据发现后它们一并纳入。
-- 未纳入的（明细侧没有同名列，如 CUS_EXPORT_M.DRAWBACK_AMOUNT_TAX）保持可写、不汇总。
--
-- 幂等：只把仍未只读的置 1，重复执行不改变结果。

SET NOCOUNT ON;

IF OBJECT_ID('tempdb..#ROLLUP_PAIRS') IS NOT NULL DROP TABLE #ROLLUP_PAIRS;
SELECT DISTINCT MASTER_TABLE AS M, DETAIL_TABLE AS D
INTO #ROLLUP_PAIRS
FROM dbo.MODULES
WHERE ISNULL(MASTER_TABLE, '') <> ''
  AND ISNULL(DETAIL_TABLE, '') <> '';

UPDATE f
SET IS_READONLY = 1
FROM dbo.FIELDS f
JOIN #ROLLUP_PAIRS p ON p.M = f.T_ID
JOIN dbo.FIELDS d ON d.T_ID = p.D AND d.F_ID = f.F_ID AND d.NUMERIC_ROLE = N'AMOUNT'
WHERE f.NUMERIC_ROLE = N'AMOUNT'
  AND ISNULL(f.IS_READONLY, 0) = 0
  -- 运行期按主表前两个主键列定位单据；主键不足两列的表根本不会被回写，
  -- 把它置成只读会造出"永远不能改、也不会自动算"的死列，故排除。
  AND (SELECT COUNT(*) FROM sys.indexes i
        JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
        WHERE i.object_id = OBJECT_ID('dbo.' + QUOTENAME(p.M)) AND i.is_primary_key = 1) >= 2
  AND COL_LENGTH('dbo.' + QUOTENAME(p.M), f.F_ID) IS NOT NULL
  AND COL_LENGTH('dbo.' + QUOTENAME(p.D), f.F_ID) IS NOT NULL
  AND (SELECT TYPE_NAME(c.user_type_id) FROM sys.columns c
        WHERE c.object_id = OBJECT_ID('dbo.' + QUOTENAME(p.M)) AND c.name = f.F_ID)
    = (SELECT TYPE_NAME(c.user_type_id) FROM sys.columns c
        WHERE c.object_id = OBJECT_ID('dbo.' + QUOTENAME(p.D)) AND c.name = f.F_ID);

DROP TABLE #ROLLUP_PAIRS;
