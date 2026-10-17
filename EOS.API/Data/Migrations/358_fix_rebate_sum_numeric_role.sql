-- 358：修正 REBATE_SUM 的数值语义登记
-- 这四列在收款/付款单里存的是"现金折扣"金额（实测最大 21350），不是比率；
-- 迁移 357 依据字段说明里的"折扣"误登记为 PERCENT，此处改为 AMOUNT。
-- 幂等：仅当当前值为 PERCENT 时才更新。

UPDATE [dbo].[FIELDS]
SET [NUMERIC_ROLE] = N'AMOUNT'
WHERE [F_ID] = N'REBATE_SUM'
  AND [T_ID] IN (N'COP_RECEIPT_M', N'COP_RECEIPT_OTHER', N'PUR_PAY_M', N'PUR_PAY_OTHER')
  AND [NUMERIC_ROLE] = N'PERCENT';
GO
