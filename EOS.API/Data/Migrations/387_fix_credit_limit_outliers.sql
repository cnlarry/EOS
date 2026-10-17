-- 387：修正信用额度的越界测试数据
-- 批 D 预扫描发现两列的极值远超业务合理量级：
--   CLIENT.CREDIT_LIMIT_NUM   = 1E+72（10 的 72 次方，任何金额字段都装不下）
--   SUPPLIER.CREDIT_LIMIT_QTY = 1E+15（1000 万亿）
-- 按 ADR-038 决定八"数据服从模型"，先把明显录错的值归零，再按标准类型转换，
-- 不为个别脏值放宽整列精度。
-- 阈值按各自语义族的存储上限设定（AMOUNT 为 decimal(18,4)，整数位 14；QTY 为 decimal(18,6)，整数位 12），
-- 只清掉装不下的值，正常数据不受影响。

UPDATE [dbo].[CLIENT]
SET [CREDIT_LIMIT_NUM] = 0
WHERE ABS(ISNULL([CREDIT_LIMIT_NUM], 0)) >= 1E+14;
GO

UPDATE [dbo].[SUPPLIER]
SET [CREDIT_LIMIT_QTY] = 0
WHERE ABS(ISNULL([CREDIT_LIMIT_QTY], 0)) >= 1E+12;
GO
