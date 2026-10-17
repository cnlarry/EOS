-- 386：修正 PRODUCT.PRICE 的越界测试数据，并把精度收回单价族标准
-- 背景：该列曾因一行录错的值（522 万亿，全表次大值仅 172,500）而改用 decimal(28,6)。
-- 那是让模型迁就脏数据：单价族的标准类型是 decimal(18,6)（整数位 12，上限约 1 万亿），
-- 正常量级远低于此。库内为测试数据，直接把越界值归零后收回标准精度。
-- 幂等：阈值 1e12 高于任何合理单价；列已是 decimal(18,6) 时 ALTER 为空操作。

UPDATE [dbo].[PRODUCT]
SET [PRICE] = 0
WHERE ABS(ISNULL([PRICE], 0)) >= 1000000000000;
GO

ALTER TABLE [dbo].[PRODUCT] ALTER COLUMN [PRICE] decimal(18,6) NULL;
GO
