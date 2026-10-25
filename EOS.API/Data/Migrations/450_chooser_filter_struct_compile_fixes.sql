-- 修 5 条**编译不过**的选择器过滤结构（`FIELD_DATASOURCE.FILTER_STRUCT`）。
--
-- 为什么必须先修：运行期是 fail-closed——结构编译不过时选择器退化成"没有过滤"，
-- 页面上只是候选变多、不报错也不进错误码，坏结构可以长期无人发现。
-- 判据来自 `EOS.API.Tests/ChooserFilterStructCompileLiveTests.cs`（存量逐条试编译），
-- 修复前实测 5 条失败，失败原因逐条写在下面。
--
-- 每条 UPDATE 都带 ID + 旧文本片段双重守卫（重跑不重复改），末尾用最终状态断言兜底：
-- 断言写"改完还剩没剩坏结构"，而不是"影响行数= N"——后者在已修好的库上会误报。

SET NOCOUNT ON;

-- ① ID 270（CUS_PURCHASE_D.MANUAL_NO ← CUS_MANUAL_MAT）：列对列比较写进了 value。
--    value 只接受字面量/运行时模板，列对列必须走 left/right 的 column 表达式。
--    注意**必须整条 item 替换**：只换前缀会在同一对象里留下 `"left":null,"right":null`，
--    System.Text.Json 以最后一个同名键为准，等于又把刚写进去的表达式清成 null（实测过）。
UPDATE dbo.FIELD_DATASOURCE
SET FILTER_STRUCT = REPLACE(FILTER_STRUCT,
        '{"field":"CUS_MANUAL_MAT.QTY","operator":"GT","value":"CUS_MANUAL_MAT.IN_QTY","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}',
        '{"operator":"GT","left":{"kind":"column","table":"CUS_MANUAL_MAT","column":"QTY"},"right":{"kind":"column","table":"CUS_MANUAL_MAT","column":"IN_QTY"}}')
WHERE ID = 270
  AND FILTER_STRUCT LIKE '%"value":"CUS_MANUAL_MAT.IN_QTY"%';

-- ② ID 1460（COP_SHIPMENT_D.PRO_NO ← MOC_PRODUCE_M）：同上（已完工数量 < 数量）
UPDATE dbo.FIELD_DATASOURCE
SET FILTER_STRUCT = REPLACE(FILTER_STRUCT,
        '{"field":"MOC_PRODUCE_M.FINISHED_QTY","operator":"LT","value":"MOC_PRODUCE_M.QTY","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}',
        '{"operator":"LT","left":{"kind":"column","table":"MOC_PRODUCE_M","column":"FINISHED_QTY"},"right":{"kind":"column","table":"MOC_PRODUCE_M","column":"QTY"}}')
WHERE ID = 1460
  AND FILTER_STRUCT LIKE '%"value":"MOC_PRODUCE_M.QTY"%';

-- ③ ID 1074（REPORT_USER_STATE.REPORT_ID ← REPORT）：`REPORT` 没有 `R_M_IDX` 列，现行列名是 `M_IDX`
--    （意图仍是"本模块的报表"，值 `{d.M_IDX}` 不变）。
UPDATE dbo.FIELD_DATASOURCE
SET FILTER_STRUCT = REPLACE(FILTER_STRUCT, '"field":"REPORT.R_M_IDX"', '"field":"REPORT.M_IDX"')
WHERE ID = 1074
  AND FILTER_STRUCT LIKE '%"REPORT.R_M_IDX"%';

-- ④⑤ ID 1078 / 1079（用户组成员选择器）：子查询引用了历史表名 `SYS_USER_GROUP_USER`，
--    现行表名是 `SYS_USER_GROUP_MEMBER`（迁移 436 的 `SYSDG_USER` → `SYS_USER_GROUP_MEMBER`）。
UPDATE dbo.FIELD_DATASOURCE
SET FILTER_STRUCT = REPLACE(FILTER_STRUCT, 'SYS_USER_GROUP_USER', 'SYS_USER_GROUP_MEMBER')
WHERE ID IN (1078, 1079)
  AND FILTER_STRUCT LIKE '%SYS_USER_GROUP_USER%';

-- 最终状态断言：这五条不该再带着上面那些编不过的写法
IF EXISTS (
    SELECT 1 FROM dbo.FIELD_DATASOURCE
     WHERE (ID = 270 AND FILTER_STRUCT LIKE '%"value":"CUS_MANUAL_MAT.IN_QTY"%')
        OR (ID = 1460 AND FILTER_STRUCT LIKE '%"value":"MOC_PRODUCE_M.QTY"%')
        OR (ID = 1074 AND FILTER_STRUCT LIKE '%"REPORT.R_M_IDX"%')
        OR (ID IN (1078, 1079) AND FILTER_STRUCT LIKE '%SYS_USER_GROUP_USER%'))
    THROW 51002, '迁移 450：仍有编译不过的选择器过滤结构（运行期会静默退化为无过滤）。', 1;
