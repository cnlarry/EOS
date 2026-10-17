-- 401：回填币别与计量单位的计算精度（ADR-037 批 1）
-- 单位按 UNIT_TYPE 回填：1=数量（个/包/箱…）0 位；2=重量、3=长度 3 位；毫克单独 6 位。
-- 币别按 ISO 4217：JPY/VND/KRW 为 0 位，其余 2 位（库内尚无 3 位/4 位币种）。
-- 幂等：结果与执行次数无关。

UPDATE [dbo].[UNIT] SET [DECIMAL_PLACES] = 0 WHERE [UNIT_TYPE] = '1';
GO

UPDATE [dbo].[UNIT] SET [DECIMAL_PLACES] = 3 WHERE [UNIT_TYPE] IN ('2', '3') AND [UNIT_ID] <> N'MG';
GO

UPDATE [dbo].[UNIT] SET [DECIMAL_PLACES] = 6 WHERE [UNIT_ID] = N'MG';
GO

UPDATE [dbo].[CURR] SET [DECIMAL_PLACES] = 0 WHERE [CURR_ID] IN (N'JPY', N'VND', N'KRW');
GO

UPDATE [dbo].[CURR] SET [DECIMAL_PLACES] = 2 WHERE [CURR_ID] NOT IN (N'JPY', N'VND', N'KRW');
GO
