-- 059: qty-not-exceed 校验参数补 targetTable（fail-closed）。
-- 校验执行器按 fail-closed 语义要求检查项显式声明目标表，
-- 禁止从 usage/limit 的 TARGET 域隐式推导；存量样例规则随本迁移收口。
IF OBJECT_ID(N'dbo.MODULE_VALIDATION_RULE', N'U') IS NULL
BEGIN
    PRINT N'== SKIP: MODULE_VALIDATION_RULE 不存在 ==';
    RETURN;
END;

UPDATE dbo.MODULE_VALIDATION_RULE
SET PARAM_STRUCT = N'{"mode":"usage-not-exceed","checks":[{"targetTable":"PUR_PURCHASE_D","match":[{"target":"PURCHASE_TYPE","source":{"scope":"DETAIL","field":"PURCHASE_TYPE"}},{"target":"PURCHASE_NO","source":{"scope":"DETAIL","field":"PURCHASE_NO"}},{"target":"SERIAL_NO","source":{"scope":"DETAIL","field":"PURCHASE_SERIAL_NO"}}],"thisQty":{"scope":"DETAIL","terms":[{"field":"QTY","coef":1}]},"usage":{"scope":"TARGET","fields":["RECEIVE_QTY"]},"limit":{"scope":"TARGET","fields":["QTY"]}},{"targetTable":"PUR_PURCHASE_D","match":[{"target":"PURCHASE_TYPE","source":{"scope":"DETAIL","field":"PURCHASE_TYPE"}},{"target":"PURCHASE_NO","source":{"scope":"DETAIL","field":"PURCHASE_NO"}},{"target":"SERIAL_NO","source":{"scope":"DETAIL","field":"PURCHASE_SERIAL_NO"}}],"thisQty":{"scope":"DETAIL","terms":[{"field":"SPARE_QTY","coef":1}]},"usage":{"scope":"TARGET","fields":["RECEIVE_SPARE_QTY"]},"limit":{"scope":"TARGET","fields":["SPARE_QTY"]}}]}'
WHERE MODULE_ID = 1607
  AND STAGE = N'APPROVE'
  AND VALIDATION_KEY = N'qty-not-exceed'
  AND PARAM_STRUCT NOT LIKE N'%"targetTable"%';

PRINT N'== 059 qty-not-exceed targetTable 收口完成 ==';
