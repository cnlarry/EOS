-- ADR-008 P3：存量 CHOOSE_FILTER 二档全自动转换回填（2026-08-28）
-- 全自动转换 291 行（待重建 167 + 兼容重转 124）/ 需人工 500 行；
-- 报告：logs/fields-chooser-migration/（不入库）。

SET NOCOUNT ON;

DECLARE @GUARD_MESSAGE NVARCHAR(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, @GUARD_MESSAGE, 1;

-- 1) FILTER_STRUCT 回填（漂移守卫：日志原文一致才落）
UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"MODULES.MODI_URL","operator":"NE","value":"","nullSafe":"EMPTY","left":null,"right":null,"negate":null,"group":null,"subquery":null},{"field":"MODULES.m_tag","operator":"EQ","value":"1","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND l.STATUS <> N'CONVERTED'
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'ISNULL(MODULES.MODI_URL,'''')<>'''' AND m_tag=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":null,"operator":"EQ","value":null,"nullSafe":null,"left":{"kind":"column","table":"PRODUCT","column":"BUSINESS_TAG","value":null,"op":null,"left":null,"right":null},"right":{"kind":"literal","table":null,"column":null,"value":"0","op":null,"left":null,"right":null},"negate":null,"group":null,"subquery":null},{"field":null,"operator":"EQ","value":null,"nullSafe":null,"left":{"kind":"column","table":"PRODUCT","column":"CONFIRM_TAG","value":null,"op":null,"left":null,"right":null},"right":{"kind":"literal","table":null,"column":null,"value":"1","op":null,"left":null,"right":null},"negate":null,"group":null,"subquery":null},{"field":"PRODUCT.PRO_TYPE","operator":"EQ","value":"1","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null},{"field":null,"operator":"NOT_IN","value":null,"nullSafe":null,"left":{"kind":"column","table":"PRODUCT","column":"PRO_NO","value":null,"op":null,"left":null,"right":null},"right":null,"negate":null,"group":null,"subquery":{"table":null,"from":[{"table":"BOM_COST_M","alias":null}],"column":"PRO_NO","function":null,"args":null,"filter":null}}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND l.STATUS <> N'CONVERTED'
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'PRODUCT.BUSINESS_TAG=0 AND PRODUCT.CONFIRM_TAG = 1 AND PRO_TYPE=''1'' AND PRODUCT.PRO_NO NOT IN (SELECT PRO_NO FROM BOM_COST_M)';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":null,"operator":"EQ","value":null,"nullSafe":null,"left":{"kind":"isnull","table":null,"column":null,"value":"0","op":null,"left":{"kind":"column","table":"PRODUCT","column":"BUSINESS_TAG","value":null,"op":null,"left":null,"right":null},"right":null},"right":{"kind":"literal","table":null,"column":null,"value":"0","op":null,"left":null,"right":null},"negate":null,"group":null,"subquery":null},{"field":null,"operator":"EQ","value":null,"nullSafe":null,"left":{"kind":"isnull","table":null,"column":null,"value":"0","op":null,"left":{"kind":"column","table":"PRODUCT","column":"CONFIRM_TAG","value":null,"op":null,"left":null,"right":null},"right":null},"right":{"kind":"literal","table":null,"column":null,"value":"1","op":null,"left":null,"right":null},"negate":null,"group":null,"subquery":null},{"field":null,"operator":"LT","value":null,"nullSafe":null,"left":{"kind":"isnull","table":null,"column":null,"value":"\u0027\u0027","op":null,"left":{"kind":"column","table":"PRODUCT","column":"PRO_TYPE","value":null,"op":null,"left":null,"right":null},"right":null},"right":{"kind":"literal","table":null,"column":null,"value":"3","op":null,"left":null,"right":null},"negate":null,"group":null,"subquery":null},{"field":null,"operator":"NOT_IN","value":null,"nullSafe":null,"left":{"kind":"column","table":"PRODUCT","column":"PRO_NO","value":null,"op":null,"left":null,"right":null},"right":null,"negate":null,"group":null,"subquery":{"table":null,"from":[{"table":"BOM_INSTRUCT_M","alias":null}],"column":"PRO_NO","function":null,"args":null,"filter":null}}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND l.STATUS <> N'CONVERTED'
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'ISNULL(PRODUCT.BUSINESS_TAG,0)=0 AND ISNULL(PRODUCT.CONFIRM_TAG,0) = 1 AND ISNULL(PRODUCT.PRO_TYPE,'''') <''3'' AND PRODUCT.PRO_NO NOT IN (SELECT BOM_INSTRUCT_M.PRO_NO FROM BOM_INSTRUCT_M)';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":null,"operator":"EQ","value":null,"nullSafe":null,"left":{"kind":"column","table":"PRODUCT","column":"BUSINESS_TAG","value":null,"op":null,"left":null,"right":null},"right":{"kind":"literal","table":null,"column":null,"value":"0","op":null,"left":null,"right":null},"negate":null,"group":null,"subquery":null},{"field":null,"operator":"EQ","value":null,"nullSafe":null,"left":{"kind":"column","table":"PRODUCT","column":"CONFIRM_TAG","value":null,"op":null,"left":null,"right":null},"right":{"kind":"literal","table":null,"column":null,"value":"1","op":null,"left":null,"right":null},"negate":null,"group":null,"subquery":null},{"field":null,"operator":"EQ","value":null,"nullSafe":null,"left":{"kind":"column","table":"PRODUCT","column":"CLIENT_ID","value":null,"op":null,"left":null,"right":null},"right":{"kind":"template","table":null,"column":null,"value":"{m.CLIENT_ID}","op":null,"left":null,"right":null},"negate":null,"group":null,"subquery":null},{"field":null,"operator":"NOT_IN","value":null,"nullSafe":null,"left":{"kind":"column","table":null,"column":"BOM_REDEPLOY_D.PRO_NO","value":null,"op":null,"left":null,"right":null},"right":null,"negate":null,"group":null,"subquery":{"table":null,"from":[{"table":"CLIENT_PRICE_D","alias":null}],"column":"PRO_NO","function":null,"args":null,"filter":null}}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 4
  AND c.FILTER_STRUCT IS NULL
  AND l.STATUS <> N'CONVERTED'
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'PRODUCT.BUSINESS_TAG=0 AND PRODUCT.CONFIRM_TAG = 1 AND PRODUCT.CLIENT_ID=''{m.CLIENT_ID}'' AND PRO_NO NOT IN (SELECT PRO_NO FROM CLIENT_PRICE_D)';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":null,"operator":"EQ","value":null,"nullSafe":null,"left":{"kind":"isnull","table":null,"column":null,"value":"0","op":null,"left":{"kind":"column","table":"PRODUCT","column":"BUSINESS_TAG","value":null,"op":null,"left":null,"right":null},"right":null},"right":{"kind":"literal","table":null,"column":null,"value":"0","op":null,"left":null,"right":null},"negate":null,"group":null,"subquery":null},{"field":null,"operator":"EQ","value":null,"nullSafe":null,"left":{"kind":"isnull","table":null,"column":null,"value":"0","op":null,"left":{"kind":"column","table":"PRODUCT","column":"CONFIRM_TAG","value":null,"op":null,"left":null,"right":null},"right":null},"right":{"kind":"literal","table":null,"column":null,"value":"1","op":null,"left":null,"right":null},"negate":null,"group":null,"subquery":null},{"field":null,"operator":"LT","value":null,"nullSafe":null,"left":{"kind":"isnull","table":null,"column":null,"value":"\u0027\u0027","op":null,"left":{"kind":"column","table":"PRODUCT","column":"PRO_TYPE","value":null,"op":null,"left":null,"right":null},"right":null},"right":{"kind":"literal","table":null,"column":null,"value":"3","op":null,"left":null,"right":null},"negate":null,"group":null,"subquery":null},{"field":null,"operator":"NOT_IN","value":null,"nullSafe":null,"left":{"kind":"column","table":"PRODUCT","column":"PRO_NO","value":null,"op":null,"left":null,"right":null},"right":null,"negate":null,"group":null,"subquery":{"table":null,"from":[{"table":"BOM_STRU_M","alias":null}],"column":"PRO_NO","function":null,"args":null,"filter":null}}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND l.STATUS <> N'CONVERTED'
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'ISNULL(PRODUCT.BUSINESS_TAG,0)=0 AND ISNULL(PRODUCT.CONFIRM_TAG,0) = 1 AND ISNULL(PRODUCT.PRO_TYPE,'''') <''3'' AND PRODUCT.PRO_NO NOT IN (SELECT BOM_STRU_M.PRO_NO FROM BOM_STRU_M)';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"HR_EMPLOYEE.STATE","operator":"LT","value":"4","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND l.STATUS <> N'CONVERTED'
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'HR_EMPLOYEE.STATE < 4';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"HR_EMPLOYEE.STATE","operator":"LT","value":"4","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND l.STATUS <> N'CONVERTED'
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'HR_EMPLOYEE.STATE < 4';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"HR_EMPLOYEE.STATE","operator":"LT","value":"4","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND l.STATUS <> N'CONVERTED'
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'HR_EMPLOYEE.STATE < 4';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"HR_EMPLOYEE.STATE","operator":"LT","value":"4","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND l.STATUS <> N'CONVERTED'
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'HR_EMPLOYEE.STATE < 4';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"HR_EMPLOYEE.STATE","operator":"LT","value":"4","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND l.STATUS <> N'CONVERTED'
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'HR_EMPLOYEE.STATE < 4';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":null,"operator":"EQ","value":null,"nullSafe":null,"left":{"kind":"isnull","table":null,"column":null,"value":"0","op":null,"left":{"kind":"column","table":"PRODUCT","column":"BUSINESS_TAG","value":null,"op":null,"left":null,"right":null},"right":null},"right":{"kind":"literal","table":null,"column":null,"value":"0","op":null,"left":null,"right":null},"negate":null,"group":null,"subquery":null},{"field":null,"operator":"EQ","value":null,"nullSafe":null,"left":{"kind":"isnull","table":null,"column":null,"value":"0","op":null,"left":{"kind":"column","table":"PRODUCT","column":"CONFIRM_TAG","value":null,"op":null,"left":null,"right":null},"right":null},"right":{"kind":"literal","table":null,"column":null,"value":"1","op":null,"left":null,"right":null},"negate":null,"group":null,"subquery":null},{"field":null,"operator":"NOT_IN","value":null,"nullSafe":null,"left":{"kind":"column","table":"PRODUCT","column":"PRO_NO","value":null,"op":null,"left":null,"right":null},"right":null,"negate":null,"group":null,"subquery":{"table":null,"from":[{"table":"CLIENT_PRICE_D","alias":null}],"column":"PRO_NO","function":null,"args":null,"filter":[{"field":null,"operator":"EQ","value":null,"nullSafe":null,"left":{"kind":"column","table":"CLIENT_PRICE_D","column":"CLIENT_ID","value":null,"op":null,"left":null,"right":null},"right":{"kind":"template","table":null,"column":null,"value":"{m.CLIENT_ID}","op":null,"left":null,"right":null},"negate":null,"group":null,"subquery":null}]}}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND l.STATUS <> N'CONVERTED'
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'ISNULL(PRODUCT.BUSINESS_TAG,0)=0 AND ISNULL(PRODUCT.CONFIRM_TAG,0) = 1 AND PRODUCT.PRO_NO NOT IN (SELECT PRO_NO FROM CLIENT_PRICE_D WHERE CLIENT_PRICE_D.CLIENT_ID=''{M.CLIENT_ID}'')';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"DEPOT.MRP","operator":"EQ","value":"1","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND l.STATUS <> N'CONVERTED'
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'DEPOT.MRP=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"DEPOT.MRP","operator":"EQ","value":"1","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND l.STATUS <> N'CONVERTED'
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'DEPOT.MRP=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"DEPOT.MRP","operator":"EQ","value":"1","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND l.STATUS <> N'CONVERTED'
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'DEPOT.MRP=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"DEPOT.MRP","operator":"EQ","value":"1","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND l.STATUS <> N'CONVERTED'
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'DEPOT.MRP=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"BANK.CURR_ID","operator":"EQ","value":"{m.CURR_ID}","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND l.STATUS <> N'CONVERTED'
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'BANK.CURR_ID=''{m.CURR_ID}''';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"DEPOT.MRP","operator":"EQ","value":"1","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND l.STATUS <> N'CONVERTED'
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'DEPOT.MRP=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"COP_PREPAY_M.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null},{"field":"COP_PREPAY_M.AMOUNT","operator":"GT","value":"COP_PREPAY_M.PREPAY_AMOUNT","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND l.STATUS <> N'CONVERTED'
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'COP_PREPAY_M.CONFIRM_TAG=1 AND COP_PREPAY_M.AMOUNT>COP_PREPAY_M.PREPAY_AMOUNT';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"DEPOT.MRP","operator":"EQ","value":"0","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND l.STATUS <> N'CONVERTED'
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'DEPOT.MRP=0';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"DEPOT.MRP","operator":"EQ","value":"1","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND l.STATUS <> N'CONVERTED'
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'DEPOT.MRP=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"DEPOT.MRP","operator":"EQ","value":"1","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null},{"field":"COP_SEND_D.DEPOT_ID","operator":"NE","value":"TW","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND l.STATUS <> N'CONVERTED'
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'DEPOT.MRP=1 AND DEPOT_ID<>''TW''';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"CUS_PRODUCT.PRO_TYPE","operator":"EQ","value":"1","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null},{"field":"CUS_PRODUCT.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND l.STATUS <> N'CONVERTED'
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'CUS_PRODUCT.PRO_TYPE =''1'' AND CUS_PRODUCT.CONFIRM_TAG=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"CUS_PRODUCT.PRO_SORT","operator":"NE","value":"1","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null},{"field":"CUS_PRODUCT.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND l.STATUS <> N'CONVERTED'
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'CUS_PRODUCT.PRO_SORT<>''1'' AND CUS_PRODUCT.CONFIRM_TAG=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"CUS_PRODUCT.PRO_SORT","operator":"NE","value":"1","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null},{"field":"CUS_PRODUCT.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND l.STATUS <> N'CONVERTED'
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'CUS_PRODUCT.PRO_SORT<>''1'' AND CUS_PRODUCT.CONFIRM_TAG=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"CUS_PRODUCT.PRO_TYPE","operator":"EQ","value":"2","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null},{"field":"CUS_PRODUCT.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND l.STATUS <> N'CONVERTED'
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'CUS_PRODUCT.PRO_TYPE=''2'' AND CUS_PRODUCT.CONFIRM_TAG=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"CUS_PRODUCT.PRO_TYPE","operator":"EQ","value":"2","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null},{"field":"CUS_PRODUCT.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND l.STATUS <> N'CONVERTED'
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'CUS_PRODUCT.PRO_TYPE=''2'' AND CUS_PRODUCT.CONFIRM_TAG=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"CUS_PRODUCT.PRO_SORT","operator":"NE","value":"2","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null},{"field":"CUS_PRODUCT.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND l.STATUS <> N'CONVERTED'
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'CUS_PRODUCT.PRO_SORT<>''2'' AND CUS_PRODUCT.CONFIRM_TAG=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"CUS_PRODUCT.PRO_SORT","operator":"NE","value":"2","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null},{"field":"CUS_PRODUCT.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND l.STATUS <> N'CONVERTED'
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'CUS_PRODUCT.PRO_SORT<>''2'' AND CUS_PRODUCT.CONFIRM_TAG=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"CUS_PRODUCT.PRO_TYPE","operator":"EQ","value":"1","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null},{"field":"CUS_PRODUCT.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND l.STATUS <> N'CONVERTED'
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'CUS_PRODUCT.PRO_TYPE=''1'' AND CUS_PRODUCT.CONFIRM_TAG=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"CUS_PRODUCT.PRO_TYPE","operator":"EQ","value":"1","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null},{"field":"CUS_PRODUCT.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND l.STATUS <> N'CONVERTED'
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'CUS_PRODUCT.PRO_TYPE=''1'' AND CUS_PRODUCT.CONFIRM_TAG=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"DEPOT.MRP","operator":"EQ","value":"1","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND l.STATUS <> N'CONVERTED'
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'DEPOT.MRP=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"DEPOT.MRP","operator":"EQ","value":"1","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND l.STATUS <> N'CONVERTED'
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'DEPOT.MRP=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"DEPOT.MRP","operator":"EQ","value":"1","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND l.STATUS <> N'CONVERTED'
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'DEPOT.MRP=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"DEPOT.MRP","operator":"EQ","value":"1","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND l.STATUS <> N'CONVERTED'
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'DEPOT.MRP=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"DEPOT.MRP","operator":"EQ","value":"1","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND l.STATUS <> N'CONVERTED'
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'DEPOT.MRP=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"HR_EMPLOYEE.STATE","operator":"LT","value":"4","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND l.STATUS <> N'CONVERTED'
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'HR_EMPLOYEE.STATE < 4';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"HR_EMPLOYEE.STATE","operator":"LT","value":"4","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND l.STATUS <> N'CONVERTED'
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'HR_EMPLOYEE.STATE < 4';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"HR_EMPLOYEE.STATE","operator":"LT","value":"4","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND l.STATUS <> N'CONVERTED'
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'HR_EMPLOYEE.STATE < 4';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"HR_EMPLOYEE.STATE","operator":"LT","value":"4","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND l.STATUS <> N'CONVERTED'
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'HR_EMPLOYEE.STATE < 4';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"HR_EMPLOYEE.STATE","operator":"LT","value":"4","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND l.STATUS <> N'CONVERTED'
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'HR_EMPLOYEE.STATE < 4';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"HR_EMPLOYEE.STATE","operator":"LT","value":"4","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND l.STATUS <> N'CONVERTED'
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'HR_EMPLOYEE.STATE < 4';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"HR_EMPLOYEE.STATE","operator":"LT","value":"4","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND l.STATUS <> N'CONVERTED'
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'HR_EMPLOYEE.STATE < 4';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"HR_EMPLOYEE.STATE","operator":"LT","value":"4","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND l.STATUS <> N'CONVERTED'
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'HR_EMPLOYEE.STATE < 4';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":null,"operator":"LT","value":null,"nullSafe":null,"left":{"kind":"column","table":"HR_EMPLOYEE","column":"STATE","value":null,"op":null,"left":null,"right":null},"right":{"kind":"literal","table":null,"column":null,"value":"4","op":null,"left":null,"right":null},"negate":null,"group":null,"subquery":null},{"field":null,"operator":"NOT_IN","value":null,"nullSafe":null,"left":{"kind":"column","table":"HR_EMPLOYEE","column":"EMP_ID","value":null,"op":null,"left":null,"right":null},"right":null,"negate":null,"group":null,"subquery":{"table":null,"from":[{"table":"HR_APPLY_D","alias":null}],"column":"EMP_ID","function":null,"args":null,"filter":[{"field":null,"operator":"EQ","value":null,"nullSafe":null,"left":{"kind":"column","table":"HR_APPLY_D","column":"APPLY_TYPE","value":null,"op":null,"left":null,"right":null},"right":{"kind":"template","table":null,"column":null,"value":"{m.APPLY_TYPE}","op":null,"left":null,"right":null},"negate":null,"group":null,"subquery":null},{"field":null,"operator":"EQ","value":null,"nullSafe":null,"left":{"kind":"column","table":"HR_APPLY_D","column":"APPLY_NO","value":null,"op":null,"left":null,"right":null},"right":{"kind":"template","table":null,"column":null,"value":"{m.APPLY_NO}","op":null,"left":null,"right":null},"negate":null,"group":null,"subquery":null}]}}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND l.STATUS <> N'CONVERTED'
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'HR_EMPLOYEE.STATE < 4 AND HR_EMPLOYEE.EMP_ID NOT IN (SELECT EMP_ID FROM HR_APPLY_D WHERE HR_APPLY_D.APPLY_TYPE=''{m.APPLY_TYPE}'' AND HR_APPLY_D.APPLY_NO=''{m.APPLY_NO}'')';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"HR_EMPLOYEE.STATE","operator":"LT","value":"4","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND l.STATUS <> N'CONVERTED'
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'HR_EMPLOYEE.STATE < 4';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"HR_EMPLOYEE.STATE","operator":"LT","value":"4","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND l.STATUS <> N'CONVERTED'
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'HR_EMPLOYEE.STATE < 4';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"HR_EMPLOYEE.STATE","operator":"LT","value":"4","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND l.STATUS <> N'CONVERTED'
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'HR_EMPLOYEE.STATE < 4';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"HR_EMPLOYEE.STATE","operator":"LT","value":"4","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND l.STATUS <> N'CONVERTED'
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'HR_EMPLOYEE.STATE < 4';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"HR_EMPLOYEE.STATE","operator":"LT","value":"4","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND l.STATUS <> N'CONVERTED'
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'HR_EMPLOYEE.STATE < 4';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"HR_EMPLOYEE.STATE","operator":"LT","value":"4","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null},{"field":"HR_EMPLOYEE.IF_SECRECY","operator":"EQ","value":"{m.IF_SECRECY}","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND l.STATUS <> N'CONVERTED'
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'HR_EMPLOYEE.STATE < 4 AND HR_EMPLOYEE.IF_SECRECY={m.IF_SECRECY}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"HR_EMPLOYEE.STATE","operator":"LT","value":"4","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND l.STATUS <> N'CONVERTED'
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'HR_EMPLOYEE.STATE < 4';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"HR_EMPLOYEE.STATE","operator":"LT","value":"4","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND l.STATUS <> N'CONVERTED'
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'HR_EMPLOYEE.STATE < 4';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"HR_EMPLOYEE.STATE","operator":"LT","value":"4","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND l.STATUS <> N'CONVERTED'
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'HR_EMPLOYEE.STATE < 4';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":null,"operator":"LT","value":null,"nullSafe":null,"left":{"kind":"column","table":"HR_EMPLOYEE","column":"STATE","value":null,"op":null,"left":null,"right":null},"right":{"kind":"literal","table":null,"column":null,"value":"4","op":null,"left":null,"right":null},"negate":null,"group":null,"subquery":null},{"field":null,"operator":"NOT_IN","value":null,"nullSafe":null,"left":{"kind":"column","table":"HR_EMPLOYEE","column":"EMP_ID","value":null,"op":null,"left":null,"right":null},"right":null,"negate":null,"group":null,"subquery":{"table":null,"from":[{"table":"HR_EMPLOYEE_CARD","alias":null}],"column":"EMP_ID","function":null,"args":null,"filter":null}}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND l.STATUS <> N'CONVERTED'
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'HR_EMPLOYEE.STATE < 4 AND HR_EMPLOYEE.EMP_ID NOT IN (SELECT EMP_ID FROM HR_EMPLOYEE_CARD)';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":null,"operator":"LT","value":null,"nullSafe":null,"left":{"kind":"column","table":"HR_EMPLOYEE","column":"STATE","value":null,"op":null,"left":null,"right":null},"right":{"kind":"literal","table":null,"column":null,"value":"4","op":null,"left":null,"right":null},"negate":null,"group":null,"subquery":null},{"field":null,"operator":"EQ","value":null,"nullSafe":null,"left":{"kind":"column","table":"HR_EMPLOYEE","column":"IF_CARD","value":null,"op":null,"left":null,"right":null},"right":{"kind":"literal","table":null,"column":null,"value":"1","op":null,"left":null,"right":null},"negate":null,"group":null,"subquery":null},{"field":null,"operator":"NOT_IN","value":null,"nullSafe":null,"left":{"kind":"column","table":"HR_EMPLOYEE","column":"EMP_ID","value":null,"op":null,"left":null,"right":null},"right":null,"negate":null,"group":null,"subquery":{"table":null,"from":[{"table":"HR_EMPLOYEE_CARD","alias":null}],"column":"EMP_ID","function":null,"args":null,"filter":null}}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND l.STATUS <> N'CONVERTED'
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'HR_EMPLOYEE.STATE < 4 AND HR_EMPLOYEE.IF_CARD = 1 AND HR_EMPLOYEE.EMP_ID NOT IN (SELECT EMP_ID FROM HR_EMPLOYEE_CARD)';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":null,"operator":"LT","value":null,"nullSafe":null,"left":{"kind":"column","table":"HR_EMPLOYEE","column":"STATE","value":null,"op":null,"left":null,"right":null},"right":{"kind":"literal","table":null,"column":null,"value":"4","op":null,"left":null,"right":null},"negate":null,"group":null,"subquery":null},{"field":null,"operator":"NOT_IN","value":null,"nullSafe":null,"left":{"kind":"column","table":"HR_EMPLOYEE","column":"EMP_ID","value":null,"op":null,"left":null,"right":null},"right":null,"negate":null,"group":null,"subquery":{"table":null,"from":[{"table":"HR_ENACTMENT_D","alias":null}],"column":"EMP_ID","function":null,"args":null,"filter":[{"field":"HR_ENACTMENT_D.ENACTMENT_NO","operator":"EQ","value":"{m.ENACTMENT_NO}","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null},{"field":"HR_ENACTMENT_D.ENACTMENT_TYPE","operator":"EQ","value":"{m.ENACTMENT_TYPE}","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND l.STATUS <> N'CONVERTED'
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'HR_EMPLOYEE.STATE < 4 AND HR_EMPLOYEE.EMP_ID NOT IN (SELECT EMP_ID FROM HR_ENACTMENT_D WHERE ENACTMENT_NO=''{m.ENACTMENT_NO}'' AND ENACTMENT_TYPE=''{m.ENACTMENT_TYPE}'')';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"HR_EMPLOYEE.STATE","operator":"LT","value":"4","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND l.STATUS <> N'CONVERTED'
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'HR_EMPLOYEE.STATE < 4';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"HR_EMPLOYEE.STATE","operator":"LT","value":"4","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND l.STATUS <> N'CONVERTED'
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'HR_EMPLOYEE.STATE < 4';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"HR_EMPLOYEE.STATE","operator":"LT","value":"4","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND l.STATUS <> N'CONVERTED'
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'HR_EMPLOYEE.STATE < 4';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"HR_EMPLOYEE.STATE","operator":"LT","value":"4","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND l.STATUS <> N'CONVERTED'
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'HR_EMPLOYEE.STATE < 4';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"HR_EMPLOYEE.STATE","operator":"LT","value":"4","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND l.STATUS <> N'CONVERTED'
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'HR_EMPLOYEE.STATE < 4';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"HR_EMPLOYEE.STATE","operator":"LT","value":"4","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND l.STATUS <> N'CONVERTED'
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'HR_EMPLOYEE.STATE < 4';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"HR_EMPLOYEE.STATE","operator":"LT","value":"4","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND l.STATUS <> N'CONVERTED'
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'HR_EMPLOYEE.STATE < 4';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"HR_EMPLOYEE.STATE","operator":"LT","value":"4","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND l.STATUS <> N'CONVERTED'
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'HR_EMPLOYEE.STATE < 4';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":null,"operator":"LT","value":null,"nullSafe":null,"left":{"kind":"isnull","table":null,"column":null,"value":"0","op":null,"left":{"kind":"column","table":"HR_EMPLOYEE","column":"STATE","value":null,"op":null,"left":null,"right":null},"right":null},"right":{"kind":"literal","table":null,"column":null,"value":"4","op":null,"left":null,"right":null},"negate":null,"group":null,"subquery":null},{"field":null,"operator":"NOT_IN","value":null,"nullSafe":null,"left":{"kind":"column","table":"HR_EMPLOYEE","column":"EMP_ID","value":null,"op":null,"left":null,"right":null},"right":null,"negate":null,"group":null,"subquery":{"table":null,"from":[{"table":"HR_PLAN_M","alias":null},{"table":"HR_PLAN_D","alias":null}],"column":"EMP_ID","function":null,"args":null,"filter":[{"field":null,"operator":"EQ","value":null,"nullSafe":null,"left":{"kind":"column","table":"HR_PLAN_M","column":"PLAN_TYPE","value":null,"op":null,"left":null,"right":null},"right":{"kind":"column","table":"HR_PLAN_D","column":"PLAN_TYPE","value":null,"op":null,"left":null,"right":null},"negate":null,"group":null,"subquery":null},{"field":null,"operator":"EQ","value":null,"nullSafe":null,"left":{"kind":"column","table":"HR_PLAN_M","column":"PLAN_NO","value":null,"op":null,"left":null,"right":null},"right":{"kind":"column","table":"HR_PLAN_D","column":"PLAN_NO","value":null,"op":null,"left":null,"right":null},"negate":null,"group":null,"subquery":null},{"field":null,"operator":"EQ","value":null,"nullSafe":null,"left":{"kind":"column","table":"HR_PLAN_M","column":"COUNT_MONTH","value":null,"op":null,"left":null,"right":null},"right":{"kind":"template","table":null,"column":null,"value":"{m.COUNT_MONTH}","op":null,"left":null,"right":null},"negate":null,"group":null,"subquery":null}]}}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND l.STATUS <> N'CONVERTED'
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'ISNULL(HR_EMPLOYEE.STATE,0) < 4 AND HR_EMPLOYEE.EMP_ID NOT IN (SELECT HR_PLAN_D.EMP_ID FROM HR_PLAN_M,HR_PLAN_D WHERE HR_PLAN_M.PLAN_TYPE=HR_PLAN_D.PLAN_TYPE AND HR_PLAN_M.PLAN_NO=HR_PLAN_D.PLAN_NO AND HR_PLAN_M.COUNT_MONTH=''{m.COUNT_MONTH}'')';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"HR_EMPLOYEE.STATE","operator":"LT","value":"4","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND l.STATUS <> N'CONVERTED'
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'HR_EMPLOYEE.STATE < 4';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"HR_EMPLOYEE.STATE","operator":"LT","value":"4","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND l.STATUS <> N'CONVERTED'
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'HR_EMPLOYEE.STATE < 4';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"HR_EMPLOYEE.STATE","operator":"LT","value":"4","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND l.STATUS <> N'CONVERTED'
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'HR_EMPLOYEE.STATE < 4';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"HR_EMPLOYEE.STATE","operator":"LT","value":"4","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND l.STATUS <> N'CONVERTED'
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'HR_EMPLOYEE.STATE < 4';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"HR_EMPLOYEE.STATE","operator":"LT","value":"4","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND l.STATUS <> N'CONVERTED'
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'HR_EMPLOYEE.STATE < 4';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"HR_EMPLOYEE.STATE","operator":"LT","value":"4","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND l.STATUS <> N'CONVERTED'
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'HR_EMPLOYEE.STATE < 4';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"HR_EMPLOYEE.STATE","operator":"LT","value":"4","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND l.STATUS <> N'CONVERTED'
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'HR_EMPLOYEE.STATE < 4';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"HR_EMPLOYEE.STATE","operator":"LT","value":"4","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND l.STATUS <> N'CONVERTED'
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'HR_EMPLOYEE.STATE < 4';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"HR_EMPLOYEE.STATE","operator":"LT","value":"4","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND l.STATUS <> N'CONVERTED'
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'HR_EMPLOYEE.STATE < 4';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"HR_EMPLOYEE.STATE","operator":"LT","value":"4","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND l.STATUS <> N'CONVERTED'
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'HR_EMPLOYEE.STATE < 4';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"HR_EMPLOYEE.STATE","operator":"LT","value":"4","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND l.STATUS <> N'CONVERTED'
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'HR_EMPLOYEE.STATE < 4';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"HR_EMPLOYEE.STATE","operator":"LT","value":"4","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND l.STATUS <> N'CONVERTED'
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'HR_EMPLOYEE.STATE < 4';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"HR_EMPLOYEE.STATE","operator":"LT","value":"4","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null},{"field":"HR_EMPLOYEE.IF_SECRECY","operator":"EQ","value":"{m.IF_SECRECY}","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND l.STATUS <> N'CONVERTED'
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'HR_EMPLOYEE.STATE < 4 AND HR_EMPLOYEE.IF_SECRECY={m.IF_SECRECY}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"HR_EMPLOYEE.STATE","operator":"LT","value":"4","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND l.STATUS <> N'CONVERTED'
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'HR_EMPLOYEE.STATE < 4';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"HR_EMPLOYEE.STATE","operator":"LT","value":"4","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND l.STATUS <> N'CONVERTED'
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'HR_EMPLOYEE.STATE < 4';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"HR_EMPLOYEE.STATE","operator":"LT","value":"4","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND l.STATUS <> N'CONVERTED'
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'HR_EMPLOYEE.STATE < 4';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"HR_EMPLOYEE.STATE","operator":"LT","value":"4","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND l.STATUS <> N'CONVERTED'
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'HR_EMPLOYEE.STATE < 4';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"HR_EMPLOYEE.STATE","operator":"LT","value":"4","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND l.STATUS <> N'CONVERTED'
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'HR_EMPLOYEE.STATE < 4';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"HR_EMPLOYEE.STATE","operator":"LT","value":"4","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND l.STATUS <> N'CONVERTED'
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'HR_EMPLOYEE.STATE < 4';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"HR_EMPLOYEE.STATE","operator":"LT","value":"4","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND l.STATUS <> N'CONVERTED'
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'HR_EMPLOYEE.STATE < 4';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"HR_EMPLOYEE.STATE","operator":"LT","value":"4","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND l.STATUS <> N'CONVERTED'
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'HR_EMPLOYEE.STATE < 4';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"DEPOT.MRP","operator":"EQ","value":"1","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND l.STATUS <> N'CONVERTED'
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'DEPOT.MRP=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":null,"operator":"EQ","value":null,"nullSafe":null,"left":{"kind":"isnull","table":null,"column":null,"value":"0","op":null,"left":{"kind":"column","table":"PRODUCT","column":"BUSINESS_TAG","value":null,"op":null,"left":null,"right":null},"right":null},"right":{"kind":"literal","table":null,"column":null,"value":"0","op":null,"left":null,"right":null},"negate":null,"group":null,"subquery":null},{"field":null,"operator":"EQ","value":null,"nullSafe":null,"left":{"kind":"isnull","table":null,"column":null,"value":"0","op":null,"left":{"kind":"column","table":"PRODUCT","column":"CONFIRM_TAG","value":null,"op":null,"left":null,"right":null},"right":null},"right":{"kind":"literal","table":null,"column":null,"value":"1","op":null,"left":null,"right":null},"negate":null,"group":null,"subquery":null},{"field":null,"operator":"NOT_IN","value":null,"nullSafe":null,"left":{"kind":"column","table":"PRODUCT","column":"PRO_NO","value":null,"op":null,"left":null,"right":null},"right":null,"negate":null,"group":null,"subquery":{"table":null,"from":[{"table":"INV_CHECK_STOCK_D","alias":null}],"column":"PRO_NO","function":null,"args":null,"filter":[{"field":"INV_CHECK_STOCK_D.CHECK_STOCK_TYPE","operator":"EQ","value":"{m.CHECK_STOCK_TYPE}","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null},{"field":"INV_CHECK_STOCK_D.CHECK_STOCK_NO","operator":"EQ","value":"{m.CHECK_STOCK_NO}","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND l.STATUS <> N'CONVERTED'
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'ISNULL(PRODUCT.BUSINESS_TAG,0)=0 AND ISNULL(PRODUCT.CONFIRM_TAG,0) = 1  AND PRODUCT.PRO_NO NOT IN (SELECT PRO_NO FROM INV_CHECK_STOCK_D WHERE CHECK_STOCK_TYPE=''{m.CHECK_STOCK_TYPE}'' AND CHECK_STOCK_NO=''{m.CHECK_STOCK_NO}'')';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"DEPOT.MRP","operator":"EQ","value":"1","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND l.STATUS <> N'CONVERTED'
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'DEPOT.MRP=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"DEPOT.MRP","operator":"EQ","value":"1","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND l.STATUS <> N'CONVERTED'
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'DEPOT.MRP=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":null,"operator":"EQ","value":null,"nullSafe":null,"left":{"kind":"isnull","table":null,"column":null,"value":"0","op":null,"left":{"kind":"column","table":"PRODUCT","column":"BUSINESS_TAG","value":null,"op":null,"left":null,"right":null},"right":null},"right":{"kind":"literal","table":null,"column":null,"value":"0","op":null,"left":null,"right":null},"negate":null,"group":null,"subquery":null},{"field":null,"operator":"EQ","value":null,"nullSafe":null,"left":{"kind":"isnull","table":null,"column":null,"value":"0","op":null,"left":{"kind":"column","table":"PRODUCT","column":"CONFIRM_TAG","value":null,"op":null,"left":null,"right":null},"right":null},"right":{"kind":"literal","table":null,"column":null,"value":"1","op":null,"left":null,"right":null},"negate":null,"group":null,"subquery":null},{"field":null,"operator":"GE","value":null,"nullSafe":null,"left":{"kind":"column","table":"PRODUCT","column":"PRO_TYPE","value":null,"op":null,"left":null,"right":null},"right":{"kind":"literal","table":null,"column":null,"value":"1","op":null,"left":null,"right":null},"negate":null,"group":null,"subquery":null},{"field":null,"operator":"EQ","value":null,"nullSafe":null,"left":{"kind":"template","table":null,"column":null,"value":"{m.OCCUR_TYPE}","op":null,"left":null,"right":null},"right":{"kind":"literal","table":null,"column":null,"value":"QTC","op":null,"left":null,"right":null},"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND l.STATUS <> N'CONVERTED'
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'ISNULL(PRODUCT.BUSINESS_TAG,0)=0 AND ISNULL(PRODUCT.CONFIRM_TAG,0) = 1 AND PRODUCT.PRO_TYPE>=''1'' AND ''{m.OCCUR_TYPE}''=''QTC''';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":null,"operator":"GT","value":null,"nullSafe":null,"left":{"kind":"column","table":"PRODUCT","column":"QTY","value":null,"op":null,"left":null,"right":null},"right":{"kind":"literal","table":null,"column":null,"value":"0.1","op":null,"left":null,"right":null},"negate":null,"group":null,"subquery":null},{"field":null,"operator":"EQ","value":null,"nullSafe":null,"left":{"kind":"template","table":null,"column":null,"value":"{m.OCCUR_TYPE}","op":null,"left":null,"right":null},"right":{"kind":"literal","table":null,"column":null,"value":"QTC","op":null,"left":null,"right":null},"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 4
  AND c.FILTER_STRUCT IS NULL
  AND l.STATUS <> N'CONVERTED'
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'PRODUCT.QTY>0.1 AND ''{m.OCCUR_TYPE}''=''QTC''';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"DEPOT.MRP","operator":"EQ","value":"1","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND l.STATUS <> N'CONVERTED'
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'DEPOT.MRP=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"DEPOT.MRP","operator":"EQ","value":"1","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND l.STATUS <> N'CONVERTED'
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'DEPOT.MRP=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"DEPOT.MRP","operator":"EQ","value":"1","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND l.STATUS <> N'CONVERTED'
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'DEPOT.MRP=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"DEPOT.MRP","operator":"EQ","value":"1","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND l.STATUS <> N'CONVERTED'
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'DEPOT.MRP=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"DEPOT.MRP","operator":"EQ","value":"1","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND l.STATUS <> N'CONVERTED'
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'DEPOT.MRP=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"DEPOT.MRP","operator":"EQ","value":"1","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND l.STATUS <> N'CONVERTED'
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'DEPOT.MRP=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"DEPOT.MRP","operator":"EQ","value":"1","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND l.STATUS <> N'CONVERTED'
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'DEPOT.MRP=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":null,"operator":"EQ","value":null,"nullSafe":null,"left":{"kind":"column","table":"LINE","column":"LINE_ID","value":null,"op":null,"left":null,"right":null},"right":{"kind":"literal","table":null,"column":null,"value":"SWT-HJI-94","op":null,"left":null,"right":null},"negate":null,"group":null,"subquery":null},{"field":null,"operator":"EQ","value":null,"nullSafe":null,"left":{"kind":"template","table":null,"column":null,"value":"{m.PRODUCE_TYPE}","op":null,"left":null,"right":null},"right":{"kind":"literal","table":null,"column":null,"value":"ZCML","op":null,"left":null,"right":null},"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND l.STATUS <> N'CONVERTED'
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'LINE.LINE_ID=''SWT-HJI-94'' AND ''{m.PRODUCE_TYPE}''=''ZCML''';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":null,"operator":"NE","value":null,"nullSafe":null,"left":{"kind":"column","table":"LINE","column":"LINE_ID","value":null,"op":null,"left":null,"right":null},"right":{"kind":"literal","table":null,"column":null,"value":"SWT-HJI-94","op":null,"left":null,"right":null},"negate":null,"group":null,"subquery":null},{"field":null,"operator":"NE","value":null,"nullSafe":null,"left":{"kind":"template","table":null,"column":null,"value":"{m.PRODUCE_TYPE}","op":null,"left":null,"right":null},"right":{"kind":"literal","table":null,"column":null,"value":"ZCML","op":null,"left":null,"right":null},"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 3
  AND c.FILTER_STRUCT IS NULL
  AND l.STATUS <> N'CONVERTED'
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'LINE.LINE_ID<>''SWT-HJI-94'' AND ''{m.PRODUCE_TYPE}''<>''ZCML''';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":null,"operator":"EQ","value":null,"nullSafe":null,"left":{"kind":"column","table":"PRODUCT","column":"BUSINESS_TAG","value":null,"op":null,"left":null,"right":null},"right":{"kind":"literal","table":null,"column":null,"value":"0","op":null,"left":null,"right":null},"negate":null,"group":null,"subquery":null},{"field":null,"operator":"EQ","value":null,"nullSafe":null,"left":{"kind":"column","table":"PRODUCT","column":"CONFIRM_TAG","value":null,"op":null,"left":null,"right":null},"right":{"kind":"literal","table":null,"column":null,"value":"1","op":null,"left":null,"right":null},"negate":null,"group":null,"subquery":null},{"field":null,"operator":"EQ","value":null,"nullSafe":null,"left":{"kind":"column","table":"PRODUCT","column":"MAIN_SOURCE","value":null,"op":null,"left":null,"right":null},"right":{"kind":"literal","table":null,"column":null,"value":"2","op":null,"left":null,"right":null},"negate":null,"group":null,"subquery":null},{"field":null,"operator":"IN","value":null,"nullSafe":null,"left":{"kind":"column","table":"PRODUCT","column":"PRO_NO","value":null,"op":null,"left":null,"right":null},"right":null,"negate":null,"group":null,"subquery":{"table":null,"from":[{"table":"BOM_STRU_M","alias":null}],"column":"PRO_NO","function":null,"args":null,"filter":null}}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 4
  AND c.FILTER_STRUCT IS NULL
  AND l.STATUS <> N'CONVERTED'
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'PRODUCT.BUSINESS_TAG=0 AND PRODUCT.CONFIRM_TAG = 1 AND PRODUCT.MAIN_SOURCE=''2'' AND PRODUCT.PRO_NO IN (SELECT PRO_NO FROM BOM_STRU_M)';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"DEPOT.MRP","operator":"EQ","value":"1","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND l.STATUS <> N'CONVERTED'
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'DEPOT.MRP=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"SFC_PROCEDURE_TYPE.CONTROL","operator":"EQ","value":"1","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND l.STATUS <> N'CONVERTED'
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'SFC_PROCEDURE_TYPE.CONTROL=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"SFC_PROCEDURE_TYPE.CONTROL","operator":"EQ","value":"1","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND l.STATUS <> N'CONVERTED'
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'SFC_PROCEDURE_TYPE.CONTROL=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"DEPOT.MRP","operator":"EQ","value":"1","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND l.STATUS <> N'CONVERTED'
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'DEPOT.MRP=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"DEPOT.MRP","operator":"EQ","value":"1","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND l.STATUS <> N'CONVERTED'
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'DEPOT.MRP=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"MOC_PRODUCE_M.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null},{"field":"MOC_PRODUCE_M.FINISHED_QTY","operator":"GT","value":"0","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND l.STATUS <> N'CONVERTED'
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'MOC_PRODUCE_M.CONFIRM_TAG=1 AND MOC_PRODUCE_M.FINISHED_QTY>0';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"DEPOT.MRP","operator":"EQ","value":"1","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND l.STATUS <> N'CONVERTED'
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'DEPOT.MRP=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"SFC_PROCEDURE_TYPE.PROCEDURE_TYPE_ID","operator":"NE","value":"{d.PROCEDURE_TYPE_ID}","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND l.STATUS <> N'CONVERTED'
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'SFC_PROCEDURE_TYPE.PROCEDURE_TYPE_ID<>''{d.PROCEDURE_TYPE_ID}''';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"MOU_APPLY_M.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null},{"field":"MOU_APPLY_M.CLIENT_ID","operator":"EQ","value":"{m.CLIENT_ID}","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null},{"field":"MOU_APPLY_M.ACCEPT_STATE","operator":"EQ","value":"","nullSafe":"EMPTY","left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND l.STATUS <> N'CONVERTED'
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'MOU_APPLY_M.CONFIRM_TAG=1 AND MOU_APPLY_M.CLIENT_ID=''{m.CLIENT_ID}'' AND ISNULL(MOU_APPLY_M.ACCEPT_STATE,'''')=''''';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":null,"operator":"EQ","value":null,"nullSafe":null,"left":{"kind":"column","table":"MOU_APPLY_M","column":"CLIENT_ID","value":null,"op":null,"left":null,"right":null},"right":{"kind":"template","table":null,"column":null,"value":"{m.CLIENT_ID}","op":null,"left":null,"right":null},"negate":null,"group":null,"subquery":null},{"field":null,"operator":"EQ","value":null,"nullSafe":null,"left":{"kind":"column","table":"MOU_APPLY_M","column":"CONFIRM_TAG","value":null,"op":null,"left":null,"right":null},"right":{"kind":"literal","table":null,"column":null,"value":"1","op":null,"left":null,"right":null},"negate":null,"group":null,"subquery":null},{"field":null,"operator":"EQ","value":null,"nullSafe":null,"left":{"kind":"column","table":"MOU_APPLY_M","column":"SAM_TYPE","value":null,"op":null,"left":null,"right":null},"right":{"kind":"literal","table":null,"column":null,"value":"1","op":null,"left":null,"right":null},"negate":null,"group":null,"subquery":null},{"field":null,"operator":"EQ","value":null,"nullSafe":null,"left":{"kind":"column","table":"MOU_APPLY_M","column":"ACCEPT_STATE","value":null,"op":null,"left":null,"right":null},"right":{"kind":"literal","table":null,"column":null,"value":"","op":null,"left":null,"right":null},"negate":null,"group":null,"subquery":null},{"field":null,"operator":"NOT_IN","value":null,"nullSafe":null,"left":{"kind":"column","table":"MOU_APPLY_M","column":"APPLY_NO","value":null,"op":null,"left":null,"right":null},"right":null,"negate":null,"group":null,"subquery":{"table":null,"from":[{"table":"MOU_ACCEPT_M","alias":null}],"column":"APPLY_NO","function":null,"args":null,"filter":null}}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND l.STATUS <> N'CONVERTED'
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'MOU_APPLY_M.CLIENT_ID=''{m.CLIENT_ID}'' AND MOU_APPLY_M.CONFIRM_TAG=1 AND MOU_APPLY_M.SAM_TYPE=''1'' AND MOU_APPLY_M.ACCEPT_STATE='''' AND MOU_APPLY_M.APPLY_NO NOT IN (SELECT MOU_ACCEPT_M.APPLY_NO FROM MOU_ACCEPT_M)';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":null,"operator":"EQ","value":null,"nullSafe":null,"left":{"kind":"column","table":"MOU_APPLY_M","column":"CONFIRM_TAG","value":null,"op":null,"left":null,"right":null},"right":{"kind":"literal","table":null,"column":null,"value":"1","op":null,"left":null,"right":null},"negate":null,"group":null,"subquery":null},{"field":null,"operator":"EQ","value":null,"nullSafe":null,"left":{"kind":"column","table":"MOU_APPLY_M","column":"SAM_TYPE","value":null,"op":null,"left":null,"right":null},"right":{"kind":"literal","table":null,"column":null,"value":"1","op":null,"left":null,"right":null},"negate":null,"group":null,"subquery":null},{"field":null,"operator":"EQ","value":null,"nullSafe":null,"left":{"kind":"column","table":"MOU_APPLY_M","column":"ACCEPT_STATE","value":null,"op":null,"left":null,"right":null},"right":{"kind":"literal","table":null,"column":null,"value":"","op":null,"left":null,"right":null},"negate":null,"group":null,"subquery":null},{"field":null,"operator":"NOT_IN","value":null,"nullSafe":null,"left":{"kind":"column","table":"MOU_APPLY_M","column":"APPLY_NO","value":null,"op":null,"left":null,"right":null},"right":null,"negate":null,"group":null,"subquery":{"table":null,"from":[{"table":"MOU_ACCEPT_M","alias":null}],"column":"APPLY_NO","function":null,"args":null,"filter":null}}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 2
  AND c.FILTER_STRUCT IS NULL
  AND l.STATUS <> N'CONVERTED'
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'MOU_APPLY_M.CONFIRM_TAG=1 AND MOU_APPLY_M.SAM_TYPE=''1'' AND MOU_APPLY_M.ACCEPT_STATE='''' AND MOU_APPLY_M.APPLY_NO NOT IN (SELECT MOU_ACCEPT_M.APPLY_NO FROM MOU_ACCEPT_M)';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"MOU_ACCEPT_M.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null},{"field":"MOU_ACCEPT_M.CLIENT_ID","operator":"EQ","value":"{m.CLIENT_ID}","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null},{"field":"MOU_ACCEPT_M.ACCEPT_STATE","operator":"EQ","value":"\u62A5\u5E9F","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null},{"field":"MOU_ACCEPT_M.FINISHED_TAG","operator":"EQ","value":"0","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND l.STATUS <> N'CONVERTED'
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'MOU_ACCEPT_M.CONFIRM_TAG=1 AND MOU_ACCEPT_M.CLIENT_ID=''{m.CLIENT_ID}''  AND MOU_ACCEPT_M.ACCEPT_STATE=''报废'' AND MOU_ACCEPT_M.FINISHED_TAG=0';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"MOU_ACCEPT_M.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null},{"field":"MOU_ACCEPT_M.ACCEPT_STATE","operator":"EQ","value":"\u62A5\u5E9F","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null},{"field":"MOU_ACCEPT_M.FINISHED_TAG","operator":"EQ","value":"0","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 2
  AND c.FILTER_STRUCT IS NULL
  AND l.STATUS <> N'CONVERTED'
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'MOU_ACCEPT_M.CONFIRM_TAG=1 AND MOU_ACCEPT_M.ACCEPT_STATE=''报废'' AND MOU_ACCEPT_M.FINISHED_TAG=0';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"MOU_ACCEPT_M.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null},{"field":"MOU_ACCEPT_M.CLIENT_ID","operator":"EQ","value":"{m.CLIENT_ID}","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null},{"field":"MOU_ACCEPT_M.BATCH_STATE","operator":"LT","value":"{m.BATCH_SORT}","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null},{"field":"MOU_ACCEPT_M.ACCEPT_STATE","operator":"EQ","value":"\u627F\u8BA4","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 3
  AND c.FILTER_STRUCT IS NULL
  AND l.STATUS <> N'CONVERTED'
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'MOU_ACCEPT_M.CONFIRM_TAG=1 AND MOU_ACCEPT_M.CLIENT_ID=''{m.CLIENT_ID}'' AND MOU_ACCEPT_M.BATCH_STATE<''{m.BATCH_SORT}'' AND MOU_ACCEPT_M.ACCEPT_STATE=''承认''';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"MOU_APPLY_M.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null},{"field":"MOU_APPLY_M.CLIENT_ID","operator":"EQ","value":"{m.CLIENT_ID}","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null},{"field":"MOU_APPLY_M.ACCEPT_STATE","operator":"NE","value":"\u62A5\u5E9F","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND l.STATUS <> N'CONVERTED'
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'MOU_APPLY_M.CONFIRM_TAG=1 AND MOU_APPLY_M.CLIENT_ID=''{m.CLIENT_ID}'' AND MOU_APPLY_M.ACCEPT_STATE<>''报废''';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"MOU_ASSESS_M.CLIENT_ID","operator":"EQ","value":"{m.CLIENT_ID}","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null},{"field":"MOU_ASSESS_M.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND l.STATUS <> N'CONVERTED'
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'MOU_ASSESS_M.CLIENT_ID=''{M.CLIENT_ID}'' AND MOU_ASSESS_M.CONFIRM_TAG=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"MOU_ASSESS_M.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 2
  AND c.FILTER_STRUCT IS NULL
  AND l.STATUS <> N'CONVERTED'
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'MOU_ASSESS_M.CONFIRM_TAG=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"MOU_MOULD.MOU_SORT","operator":"EQ","value":"3","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND l.STATUS <> N'CONVERTED'
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'MOU_MOULD.MOU_SORT=3';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"MOU_ASSESS_M.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null},{"field":"MOU_ASSESS_M.PRO_NO","operator":"EQ","value":"{m.PRO_NO}","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND l.STATUS <> N'CONVERTED'
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'MOU_ASSESS_M.CONFIRM_TAG=1 and MOU_ASSESS_M.PRO_NO=''{m.PRO_NO}''';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"MOU_MOULD.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null},{"field":"MOU_MOULD.CLIENT_ID","operator":"EQ","value":"{m.CLIENT_ID}","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 2
  AND c.FILTER_STRUCT IS NULL
  AND l.STATUS <> N'CONVERTED'
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'MOU_MOULD.CONFIRM_TAG=1 AND MOU_MOULD.CLIENT_ID=''{m.CLIENT_ID}''';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"MOU_MOULD.MOU_SORT","operator":"EQ","value":"1","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null},{"field":"MOU_MOULD.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null},{"field":"MOU_MOULD.CLIENT_ID","operator":"EQ","value":"{d.CLIENT_ID}","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND l.STATUS <> N'CONVERTED'
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'MOU_MOULD.MOU_SORT=''1'' AND MOU_MOULD.CONFIRM_TAG=1 AND MOU_MOULD.CLIENT_ID=''{d.CLIENT_ID}''';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"MOU_MOULD.MOU_SORT","operator":"EQ","value":"2","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null},{"field":"MOU_MOULD.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 2
  AND c.FILTER_STRUCT IS NULL
  AND l.STATUS <> N'CONVERTED'
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'MOU_MOULD.MOU_SORT=''2'' AND MOU_MOULD.CONFIRM_TAG=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"MOU_MOULD.MOU_SORT","operator":"EQ","value":"3","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null},{"field":"MOU_MOULD.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 3
  AND c.FILTER_STRUCT IS NULL
  AND l.STATUS <> N'CONVERTED'
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'MOU_MOULD.MOU_SORT=''3'' AND MOU_MOULD.CONFIRM_TAG=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"MOU_MOULD.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null},{"field":"MOU_BATCHTOP_M.MOU_SORT","operator":"EQ","value":"{m.MOU_SORT}","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null},{"field":"MOU_MOULD.CLIENT_ID","operator":"EQ","value":"{m.CLIENT_ID}","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 2
  AND c.FILTER_STRUCT IS NULL
  AND l.STATUS <> N'CONVERTED'
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'MOU_MOULD.CONFIRM_TAG=1 AND MOU_SORT=''{m.MOU_SORT}'' AND MOU_MOULD.CLIENT_ID=''{m.CLIENT_ID}''';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"MOU_MOULD.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null},{"field":"MOU_MOULD.USE_TAG","operator":"EQ","value":"1","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null},{"field":"MOU_MOULD.QTY","operator":"GT","value":"0","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null},{"field":"MOU_MOULD.MOU_SORT","operator":"EQ","value":"1","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND l.STATUS <> N'CONVERTED'
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'MOU_MOULD.CONFIRM_TAG=1 AND MOU_MOULD.USE_TAG=1 AND MOU_MOULD.QTY>0 AND MOU_MOULD.MOU_SORT=''1''';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"MOU_MOULD.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null},{"field":"MOU_MOULD.USE_TAG","operator":"EQ","value":"1","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null},{"field":"MOU_MOULD.QTY","operator":"GT","value":"0","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null},{"field":"MOU_MOULD.MOU_SORT","operator":"EQ","value":"2","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 2
  AND c.FILTER_STRUCT IS NULL
  AND l.STATUS <> N'CONVERTED'
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'MOU_MOULD.CONFIRM_TAG=1 AND MOU_MOULD.USE_TAG=1 AND MOU_MOULD.QTY>0 AND MOU_MOULD.MOU_SORT=''2''';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"MOU_MOULD.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null},{"field":"MOU_MOULD.USE_TAG","operator":"EQ","value":"1","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null},{"field":"MOU_MOULD.QTY","operator":"GT","value":"0","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null},{"field":"MOU_MOULD.MOU_SORT","operator":"EQ","value":"3","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 3
  AND c.FILTER_STRUCT IS NULL
  AND l.STATUS <> N'CONVERTED'
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'MOU_MOULD.CONFIRM_TAG=1  AND MOU_MOULD.USE_TAG=1 AND MOU_MOULD.QTY>0 AND MOU_MOULD.MOU_SORT=''3''';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"MOU_MOULD.MOU_SORT","operator":"EQ","value":"1","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null},{"field":"MOU_MOULD.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null},{"field":"MOU_MOULD.CLIENT_ID","operator":"EQ","value":"{m.CLIENT_ID}","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null},{"field":"MOU_MOULD.CLIENT_PRO_NO","operator":"EQ","value":"{m.PRO_NO}","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND l.STATUS <> N'CONVERTED'
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'MOU_MOULD.MOU_SORT=''1'' AND MOU_MOULD.CONFIRM_TAG=1 AND MOU_MOULD.CLIENT_ID=''{m.CLIENT_ID}'' AND MOU_MOULD.CLIENT_PRO_NO=''{m.PRO_NO}''';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"MOU_MOULD.MOU_SORT","operator":"EQ","value":"2","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null},{"field":"MOU_MOULD.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null},{"field":"MOU_MOULD.CLIENT_ID","operator":"EQ","value":"{m.CLIENT_ID}","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 2
  AND c.FILTER_STRUCT IS NULL
  AND l.STATUS <> N'CONVERTED'
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'MOU_MOULD.MOU_SORT=''2'' AND MOU_MOULD.CONFIRM_TAG=1 AND MOU_MOULD.CLIENT_ID=''{m.CLIENT_ID}''';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"MOU_MOULD.MOU_SORT","operator":"EQ","value":"3","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null},{"field":"MOU_MOULD.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null},{"field":"MOU_MOULD.CLIENT_ID","operator":"EQ","value":"{m.CLIENT_ID}","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 3
  AND c.FILTER_STRUCT IS NULL
  AND l.STATUS <> N'CONVERTED'
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'MOU_MOULD.MOU_SORT=''3'' AND MOU_MOULD.CONFIRM_TAG=1 AND MOU_MOULD.CLIENT_ID=''{m.CLIENT_ID}''';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"MOU_MOULD.MOU_SORT","operator":"NE","value":"1","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null},{"field":"MOU_MOULD.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 4
  AND c.FILTER_STRUCT IS NULL
  AND l.STATUS <> N'CONVERTED'
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'MOU_MOULD.MOU_SORT<>''1'' AND MOU_MOULD.CONFIRM_TAG=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":null,"operator":"EQ","value":null,"nullSafe":null,"left":{"kind":"isnull","table":null,"column":null,"value":"0","op":null,"left":{"kind":"column","table":"PRODUCT","column":"BUSINESS_TAG","value":null,"op":null,"left":null,"right":null},"right":null},"right":{"kind":"literal","table":null,"column":null,"value":"0","op":null,"left":null,"right":null},"negate":null,"group":null,"subquery":null},{"field":null,"operator":"EQ","value":null,"nullSafe":null,"left":{"kind":"isnull","table":null,"column":null,"value":"0","op":null,"left":{"kind":"column","table":"PRODUCT","column":"CONFIRM_TAG","value":null,"op":null,"left":null,"right":null},"right":null},"right":{"kind":"literal","table":null,"column":null,"value":"1","op":null,"left":null,"right":null},"negate":null,"group":null,"subquery":null},{"field":null,"operator":"EQ","value":null,"nullSafe":null,"left":{"kind":"column","table":"PRODUCT","column":"PRO_TYPE","value":null,"op":null,"left":null,"right":null},"right":{"kind":"literal","table":null,"column":null,"value":"1","op":null,"left":null,"right":null},"negate":null,"group":null,"subquery":null},{"field":null,"operator":"NOT_IN","value":null,"nullSafe":null,"left":{"kind":"column","table":"PRODUCT","column":"PRO_NO","value":null,"op":null,"left":null,"right":null},"right":null,"negate":null,"group":null,"subquery":{"table":null,"from":[{"table":"MOU_PRO_M","alias":null}],"column":"PRO_NO","function":null,"args":null,"filter":[{"field":null,"operator":"NE","value":null,"nullSafe":null,"left":{"kind":"column","table":"MOU_PRO_M","column":"PRO_NO","value":null,"op":null,"left":null,"right":null},"right":{"kind":"template","table":null,"column":null,"value":"{m.PRO_NO}","op":null,"left":null,"right":null},"negate":null,"group":null,"subquery":null}]}}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND l.STATUS <> N'CONVERTED'
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'ISNULL(PRODUCT.BUSINESS_TAG,0)=0 AND ISNULL(PRODUCT.CONFIRM_TAG,0) = 1 AND PRODUCT.PRO_TYPE=''1'' AND PRODUCT.PRO_NO NOT IN (SELECT PRO_NO FROM MOU_PRO_M WHERE MOU_PRO_M.PRO_NO<>''{m.PRO_NO}'')';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":null,"operator":"EQ","value":null,"nullSafe":null,"left":{"kind":"column","table":"MOU_MOULD","column":"MOU_SORT","value":null,"op":null,"left":null,"right":null},"right":{"kind":"literal","table":null,"column":null,"value":"1","op":null,"left":null,"right":null},"negate":null,"group":null,"subquery":null},{"field":null,"operator":"EQ","value":null,"nullSafe":null,"left":{"kind":"column","table":"MOU_MOULD","column":"CONFIRM_TAG","value":null,"op":null,"left":null,"right":null},"right":{"kind":"literal","table":null,"column":null,"value":"1","op":null,"left":null,"right":null},"negate":null,"group":null,"subquery":null},{"field":null,"operator":"EQ","value":null,"nullSafe":null,"left":{"kind":"column","table":"MOU_MOULD","column":"USE_TAG","value":null,"op":null,"left":null,"right":null},"right":{"kind":"literal","table":null,"column":null,"value":"1","op":null,"left":null,"right":null},"negate":null,"group":null,"subquery":null},{"field":null,"operator":"GT","value":null,"nullSafe":null,"left":{"kind":"column","table":"MOU_MOULD","column":"QTY","value":null,"op":null,"left":null,"right":null},"right":{"kind":"literal","table":null,"column":null,"value":"0","op":null,"left":null,"right":null},"negate":null,"group":null,"subquery":null},{"field":null,"operator":"EQ","value":null,"nullSafe":null,"left":{"kind":"column","table":"MOU_MOULD","column":"CLIENT_ID","value":null,"op":null,"left":null,"right":null},"right":{"kind":"template","table":null,"column":null,"value":"{d.CLIENT_ID}","op":null,"left":null,"right":null},"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND l.STATUS <> N'CONVERTED'
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'MOU_MOULD.MOU_SORT=''1'' AND MOU_MOULD.CONFIRM_TAG=1 AND MOU_MOULD.USE_TAG=1 AND (MOU_MOULD.QTY>0) AND MOU_MOULD.CLIENT_ID=''{d.CLIENT_ID}''';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":null,"operator":"EQ","value":null,"nullSafe":null,"left":{"kind":"column","table":"MOU_MOULD","column":"MOU_SORT","value":null,"op":null,"left":null,"right":null},"right":{"kind":"literal","table":null,"column":null,"value":"2","op":null,"left":null,"right":null},"negate":null,"group":null,"subquery":null},{"field":null,"operator":"EQ","value":null,"nullSafe":null,"left":{"kind":"column","table":"MOU_MOULD","column":"CONFIRM_TAG","value":null,"op":null,"left":null,"right":null},"right":{"kind":"literal","table":null,"column":null,"value":"1","op":null,"left":null,"right":null},"negate":null,"group":null,"subquery":null},{"field":null,"operator":null,"value":null,"nullSafe":null,"left":null,"right":null,"negate":null,"group":{"logic":"OR","items":[{"field":null,"operator":null,"value":null,"nullSafe":null,"left":null,"right":null,"negate":null,"group":{"logic":"AND","items":[{"field":null,"operator":"GT","value":null,"nullSafe":null,"left":{"kind":"column","table":"MOU_MOULD","column":"QTY","value":null,"op":null,"left":null,"right":null},"right":{"kind":"literal","table":null,"column":null,"value":"0","op":null,"left":null,"right":null},"negate":null,"group":null,"subquery":null}]},"subquery":null},{"field":null,"operator":null,"value":null,"nullSafe":null,"left":null,"right":null,"negate":null,"group":{"logic":"AND","items":[{"field":null,"operator":"GT","value":null,"nullSafe":null,"left":{"kind":"column","table":"MOU_MOULD","column":"MOU_QTY","value":null,"op":null,"left":null,"right":null},"right":{"kind":"literal","table":null,"column":null,"value":"0","op":null,"left":null,"right":null},"negate":null,"group":null,"subquery":null}]},"subquery":null}]},"subquery":null},{"field":null,"operator":"EQ","value":null,"nullSafe":null,"left":{"kind":"column","table":"MOU_MOULD","column":"CLIENT_ID","value":null,"op":null,"left":null,"right":null},"right":{"kind":"template","table":null,"column":null,"value":"{d.CLIENT_ID}","op":null,"left":null,"right":null},"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 2
  AND c.FILTER_STRUCT IS NULL
  AND l.STATUS <> N'CONVERTED'
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'MOU_MOULD.MOU_SORT=''2'' AND MOU_MOULD.CONFIRM_TAG=1 AND (MOU_MOULD.QTY>0 OR MOU_MOULD.MOU_QTY>0) AND MOU_MOULD.CLIENT_ID=''{d.CLIENT_ID}''';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":null,"operator":"EQ","value":null,"nullSafe":null,"left":{"kind":"column","table":"MOU_MOULD","column":"MOU_SORT","value":null,"op":null,"left":null,"right":null},"right":{"kind":"literal","table":null,"column":null,"value":"3","op":null,"left":null,"right":null},"negate":null,"group":null,"subquery":null},{"field":null,"operator":"EQ","value":null,"nullSafe":null,"left":{"kind":"column","table":"MOU_MOULD","column":"CONFIRM_TAG","value":null,"op":null,"left":null,"right":null},"right":{"kind":"literal","table":null,"column":null,"value":"1","op":null,"left":null,"right":null},"negate":null,"group":null,"subquery":null},{"field":null,"operator":"GT","value":null,"nullSafe":null,"left":{"kind":"column","table":"MOU_MOULD","column":"QTY","value":null,"op":null,"left":null,"right":null},"right":{"kind":"literal","table":null,"column":null,"value":"0","op":null,"left":null,"right":null},"negate":null,"group":null,"subquery":null},{"field":null,"operator":"EQ","value":null,"nullSafe":null,"left":{"kind":"column","table":"MOU_MOULD","column":"CLIENT_ID","value":null,"op":null,"left":null,"right":null},"right":{"kind":"template","table":null,"column":null,"value":"{d.CLIENT_ID}","op":null,"left":null,"right":null},"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 3
  AND c.FILTER_STRUCT IS NULL
  AND l.STATUS <> N'CONVERTED'
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'MOU_MOULD.MOU_SORT=''3'' AND MOU_MOULD.CONFIRM_TAG=1  AND (MOU_MOULD.QTY>0) AND MOU_MOULD.CLIENT_ID=''{d.CLIENT_ID}''';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"TYPE_FORMULA.TYPE_ID","operator":"EQ","value":"{m.TYPE_ID}","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND l.STATUS <> N'CONVERTED'
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'TYPE_FORMULA.TYPE_ID=''{m.TYPE_ID}''';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"DEPOT.MRP","operator":"EQ","value":"1","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND l.STATUS <> N'CONVERTED'
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'DEPOT.MRP=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"DEPOT.MRP","operator":"EQ","value":"1","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND l.STATUS <> N'CONVERTED'
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'DEPOT.MRP=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"DEPOT.MRP","operator":"EQ","value":"1","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND l.STATUS <> N'CONVERTED'
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'DEPOT.MRP=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"DEPOT.MRP","operator":"EQ","value":"1","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND l.STATUS <> N'CONVERTED'
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'DEPOT.MRP=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"DEPOT.MRP","operator":"EQ","value":"1","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND l.STATUS <> N'CONVERTED'
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'DEPOT.MRP=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"BANK.CURR_ID","operator":"EQ","value":"{m.CURR_ID}","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND l.STATUS <> N'CONVERTED'
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'BANK.CURR_ID=''{m.CURR_ID}''';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"DEPOT.MRP","operator":"EQ","value":"1","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND l.STATUS <> N'CONVERTED'
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'DEPOT.MRP=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"DEPOT.MRP","operator":"EQ","value":"1","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND l.STATUS <> N'CONVERTED'
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'DEPOT.MRP=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"DEPOT.MRP","operator":"EQ","value":"1","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND l.STATUS <> N'CONVERTED'
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'DEPOT.MRP=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"DEPOT.MRP","operator":"EQ","value":"1","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND l.STATUS <> N'CONVERTED'
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'DEPOT.MRP=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"DEPOT.MRP","operator":"EQ","value":"1","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND l.STATUS <> N'CONVERTED'
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'DEPOT.MRP=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"DEPOT.MRP","operator":"EQ","value":"1","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND l.STATUS <> N'CONVERTED'
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'DEPOT.MRP=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":null,"operator":"EQ","value":null,"nullSafe":null,"left":{"kind":"isnull","table":null,"column":null,"value":"0","op":null,"left":{"kind":"column","table":"PRODUCT","column":"BUSINESS_TAG","value":null,"op":null,"left":null,"right":null},"right":null},"right":{"kind":"literal","table":null,"column":null,"value":"0","op":null,"left":null,"right":null},"negate":null,"group":null,"subquery":null},{"field":null,"operator":"EQ","value":null,"nullSafe":null,"left":{"kind":"isnull","table":null,"column":null,"value":"0","op":null,"left":{"kind":"column","table":"PRODUCT","column":"CONFIRM_TAG","value":null,"op":null,"left":null,"right":null},"right":null},"right":{"kind":"literal","table":null,"column":null,"value":"1","op":null,"left":null,"right":null},"negate":null,"group":null,"subquery":null},{"field":null,"operator":"LT","value":null,"nullSafe":null,"left":{"kind":"isnull","table":null,"column":null,"value":"\u0027\u0027","op":null,"left":{"kind":"column","table":"PRODUCT","column":"PRO_TYPE","value":null,"op":null,"left":null,"right":null},"right":null},"right":{"kind":"literal","table":null,"column":null,"value":"3","op":null,"left":null,"right":null},"negate":null,"group":null,"subquery":null},{"field":null,"operator":"NOT_IN","value":null,"nullSafe":null,"left":{"kind":"column","table":"PRODUCT","column":"PRO_NO","value":null,"op":null,"left":null,"right":null},"right":null,"negate":null,"group":null,"subquery":{"table":null,"from":[{"table":"QC_SAMPLE_M","alias":null}],"column":"PRO_NO","function":null,"args":null,"filter":null}}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND l.STATUS <> N'CONVERTED'
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'ISNULL(PRODUCT.BUSINESS_TAG,0)=0 AND ISNULL(PRODUCT.CONFIRM_TAG,0) = 1 AND ISNULL(PRODUCT.PRO_TYPE,'''') <''3'' AND PRODUCT.PRO_NO NOT IN (SELECT QC_SAMPLE_M.PRO_NO FROM QC_SAMPLE_M)';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":null,"operator":"NOT_IN","value":null,"nullSafe":null,"left":{"kind":"column","table":"MODULES","column":"M_IDX","value":null,"op":null,"left":null,"right":null},"right":null,"negate":null,"group":null,"subquery":{"table":null,"from":[{"table":"MODULES","alias":null}],"column":"M_P_IDX","function":null,"args":null,"filter":[{"field":null,"operator":"IS_NOT_NULL","value":null,"nullSafe":null,"left":{"kind":"column","table":null,"column":"MODULES.M_P_IDX","value":null,"op":null,"left":null,"right":null},"right":null,"negate":null,"group":null,"subquery":null}]}}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND l.STATUS <> N'CONVERTED'
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'MODULES.M_IDX NOT IN (SELECT M_P_IDX FROM MODULES WHERE M_P_IDX IS NOT NULL)';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":null,"operator":"NOT_IN","value":null,"nullSafe":null,"left":{"kind":"column","table":"MODULES","column":"M_IDX","value":null,"op":null,"left":null,"right":null},"right":null,"negate":null,"group":null,"subquery":{"table":null,"from":[{"table":"MODULES","alias":null}],"column":"M_P_IDX","function":null,"args":null,"filter":[{"field":null,"operator":"IS_NOT_NULL","value":null,"nullSafe":null,"left":{"kind":"column","table":null,"column":"MODULES.M_P_IDX","value":null,"op":null,"left":null,"right":null},"right":null,"negate":null,"group":null,"subquery":null}]}}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND l.STATUS <> N'CONVERTED'
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'MODULES.M_IDX NOT IN (SELECT M_P_IDX FROM MODULES WHERE M_P_IDX IS NOT NULL)';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"UNIT.UNIT_TYPE","operator":"EQ","value":"5","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND l.STATUS <> N'CONVERTED'
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'UNIT.UNIT_TYPE = ''5''';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"DEPOT.MRP","operator":"EQ","value":"1","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND l.STATUS <> N'CONVERTED'
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'DEPOT.MRP=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"UNIT.UNIT_TYPE","operator":"EQ","value":"3","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND l.STATUS <> N'CONVERTED'
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'UNIT.UNIT_TYPE = ''3''';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"TYPE_FORMULA.TYPE_ID","operator":"EQ","value":"{m.TYPE_ID}","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND l.STATUS <> N'CONVERTED'
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'TYPE_FORMULA.TYPE_ID=''{m.TYPE_ID}''';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"UNIT.UNIT_TYPE","operator":"EQ","value":"2","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND l.STATUS <> N'CONVERTED'
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'UNIT.UNIT_TYPE=''2''';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"HR_EMPLOYEE.STATE","operator":"LT","value":"4","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND l.STATUS <> N'CONVERTED'
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'HR_EMPLOYEE.STATE < 4';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":null,"operator":"IN_FUNCTION","value":null,"nullSafe":null,"left":{"kind":"column","table":"UNIT","column":"UNIT_ID","value":null,"op":null,"left":null,"right":null},"right":null,"negate":null,"group":null,"subquery":{"table":null,"from":null,"column":"UNIT_ID","function":"f_get_pro_units","args":["{d.PRO_NO}"],"filter":null}}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND l.STATUS <> N'CONVERTED'
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'UNIT.UNIT_ID in (select UNIT_ID from dbo.f_get_pro_units(''{d.PRO_NO}''))';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"HR_EMPLOYEE.STATE","operator":"LT","value":"4","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND l.STATUS <> N'CONVERTED'
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'HR_EMPLOYEE.STATE < 4';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":null,"operator":"EQ","value":null,"nullSafe":null,"left":{"kind":"isnull","table":null,"column":null,"value":"0","op":null,"left":{"kind":"column","table":"PRODUCT","column":"BUSINESS_TAG","value":null,"op":null,"left":null,"right":null},"right":null},"right":{"kind":"literal","table":null,"column":null,"value":"0","op":null,"left":null,"right":null},"negate":null,"group":null,"subquery":null},{"field":null,"operator":"EQ","value":null,"nullSafe":null,"left":{"kind":"isnull","table":null,"column":null,"value":"0","op":null,"left":{"kind":"column","table":"PRODUCT","column":"CONFIRM_TAG","value":null,"op":null,"left":null,"right":null},"right":null},"right":{"kind":"literal","table":null,"column":null,"value":"1","op":null,"left":null,"right":null},"negate":null,"group":null,"subquery":null},{"field":null,"operator":"EQ","value":null,"nullSafe":null,"left":{"kind":"column","table":"PRODUCT","column":"PRO_TYPE","value":null,"op":null,"left":null,"right":null},"right":{"kind":"literal","table":null,"column":null,"value":"1","op":null,"left":null,"right":null},"negate":null,"group":null,"subquery":null},{"field":null,"operator":"NOT_IN","value":null,"nullSafe":null,"left":{"kind":"column","table":"PRODUCT","column":"PRO_NO","value":null,"op":null,"left":null,"right":null},"right":null,"negate":null,"group":null,"subquery":{"table":null,"from":[{"table":"SFC_PROCESS_M","alias":null}],"column":"PRO_NO","function":null,"args":null,"filter":[{"field":null,"operator":"NE","value":null,"nullSafe":null,"left":{"kind":"column","table":"SFC_PROCESS_M","column":"PRO_NO","value":null,"op":null,"left":null,"right":null},"right":{"kind":"template","table":null,"column":null,"value":"{m.PRO_NO}","op":null,"left":null,"right":null},"negate":null,"group":null,"subquery":null}]}}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND l.STATUS <> N'CONVERTED'
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'ISNULL(PRODUCT.BUSINESS_TAG,0)=0 AND ISNULL(PRODUCT.CONFIRM_TAG,0) = 1 AND PRODUCT.PRO_TYPE=''1'' AND PRODUCT.PRO_NO NOT IN (SELECT PRO_NO FROM SFC_PROCESS_M WHERE SFC_PROCESS_M.PRO_NO <> ''{m.PRO_NO}'')';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"HR_EMPLOYEE.STATE","operator":"LT","value":"4","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND l.STATUS <> N'CONVERTED'
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'HR_EMPLOYEE.STATE < 4';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":null,"operator":"EQ","value":null,"nullSafe":null,"left":{"kind":"isnull","table":null,"column":null,"value":"0","op":null,"left":{"kind":"column","table":"PRODUCT","column":"BUSINESS_TAG","value":null,"op":null,"left":null,"right":null},"right":null},"right":{"kind":"literal","table":null,"column":null,"value":"0","op":null,"left":null,"right":null},"negate":null,"group":null,"subquery":null},{"field":null,"operator":"EQ","value":null,"nullSafe":null,"left":{"kind":"isnull","table":null,"column":null,"value":"0","op":null,"left":{"kind":"column","table":"PRODUCT","column":"CONFIRM_TAG","value":null,"op":null,"left":null,"right":null},"right":null},"right":{"kind":"literal","table":null,"column":null,"value":"1","op":null,"left":null,"right":null},"negate":null,"group":null,"subquery":null},{"field":null,"operator":"EQ","value":null,"nullSafe":null,"left":{"kind":"column","table":"PRODUCT","column":"PRO_TYPE","value":null,"op":null,"left":null,"right":null},"right":{"kind":"literal","table":null,"column":null,"value":"3","op":null,"left":null,"right":null},"negate":null,"group":null,"subquery":null},{"field":null,"operator":"NOT_IN","value":null,"nullSafe":null,"left":{"kind":"column","table":"PRODUCT","column":"PRO_NO","value":null,"op":null,"left":null,"right":null},"right":null,"negate":null,"group":null,"subquery":{"table":null,"from":[{"table":"SUPPLIER_PRICE_D","alias":null}],"column":"PRO_NO","function":null,"args":null,"filter":[{"field":null,"operator":"EQ","value":null,"nullSafe":null,"left":{"kind":"column","table":"SUPPLIER_PRICE_D","column":"SUPPLIER_ID","value":null,"op":null,"left":null,"right":null},"right":{"kind":"template","table":null,"column":null,"value":"{m.SUPPLIER_ID}","op":null,"left":null,"right":null},"negate":null,"group":null,"subquery":null}]}}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND l.STATUS <> N'CONVERTED'
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'ISNULL(PRODUCT.BUSINESS_TAG,0)=0 AND ISNULL(PRODUCT.CONFIRM_TAG,0) = 1 AND PRODUCT.PRO_TYPE=''3'' AND PRODUCT.PRO_NO NOT IN (SELECT PRO_NO FROM SUPPLIER_PRICE_D WHERE SUPPLIER_PRICE_D.SUPPLIER_ID=''{M.SUPPLIER_ID}'')';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":null,"operator":"NOT_IN","value":null,"nullSafe":null,"left":{"kind":"column","table":"SYSDG","column":"G_IDX","value":null,"op":null,"left":null,"right":null},"right":null,"negate":null,"group":null,"subquery":{"table":null,"from":[{"table":"SYSDG_USER","alias":null}],"column":"G_IDX","function":null,"args":null,"filter":[{"field":"SYSDG_USER.USER_ID","operator":"EQ","value":"{m.USER_ID}","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND l.STATUS <> N'CONVERTED'
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'SYSDG.G_IDX NOT IN (SELECT G_IDX FROM SYSDG_USER WHERE USER_ID=''{M.USER_ID}'')';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"SYSDL.ACTIVE_TAG","operator":"EQ","value":"1","nullSafe":"ZERO","left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND l.STATUS <> N'CONVERTED'
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'ISNULL(SYSDL.ACTIVE_TAG,0) = 1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"MODULES.MODI_URL","operator":"NE","value":"","nullSafe":"EMPTY","left":null,"right":null,"negate":null,"group":null,"subquery":null},{"field":"MODULES.MASTER_TABLE","operator":"NE","value":"","nullSafe":"EMPTY","left":null,"right":null,"negate":null,"group":null,"subquery":null},{"field":"MODULES.DETAIL_TABLE","operator":"NE","value":"","nullSafe":"EMPTY","left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND l.STATUS <> N'CONVERTED'
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'ISNULL(MODULES.MODI_URL,'''')<>'''' AND ISNULL(MODULES.MASTER_TABLE,'''')<>'''' AND ISNULL(MODULES.DETAIL_TABLE,'''')<>''''';

-- 2) 兼容失败行重转（日志原文一致才更新）
UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"BILLKIND.B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"BILLKIND.B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"BILLKIND.B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"BILLKIND.B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"BILLKIND.B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"BILLKIND.B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"BILLKIND.B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"BILLKIND.B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"BILLKIND.B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"BILLKIND.B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"BILLKIND.B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"BILLKIND.B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"BILLKIND.B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"BILLKIND.B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"BILLKIND.B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"BILLKIND.B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"BILLKIND.B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"BILLKIND.B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"BILLKIND.B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"BILLKIND.B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"BILLKIND.B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"BILLKIND.B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"BILLKIND.B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"BILLKIND.B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"BILLKIND.B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"BILLKIND.B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"BILLKIND.B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"BILLKIND.B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"BILLKIND.B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"BILLKIND.B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"BILLKIND.B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"BILLKIND.B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"BILLKIND.B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"BILLKIND.B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"BILLKIND.B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"BILLKIND.B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"BILLKIND.B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"BILLKIND.B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"BILLKIND.B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"BILLKIND.B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"BILLKIND.B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"BILLKIND.B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"BILLKIND.B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"BILLKIND.B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"BILLKIND.B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"BILLKIND.B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"BILLKIND.B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"BILLKIND.B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"BILLKIND.B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"BILLKIND.B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"BILLKIND.B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"BILLKIND.B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"BILLKIND.B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"BILLKIND.B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"BILLKIND.B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"BILLKIND.B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"BILLKIND.B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"BILLKIND.B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"BILLKIND.B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"BILLKIND.B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"BILLKIND.B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"BILLKIND.B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"BILLKIND.B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"BILLKIND.B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"BILLKIND.B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"BILLKIND.B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"BILLKIND.B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"BILLKIND.B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"BILLKIND.B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"BILLKIND.B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"BILLKIND.B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"BILLKIND.B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"BILLKIND.B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"BILLKIND.B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"BILLKIND.B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"BILLKIND.B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"BILLKIND.B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"BILLKIND.B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"BILLKIND.B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"BILLKIND.B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"BILLKIND.B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"BILLKIND.B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"BILLKIND.B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"BILLKIND.B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"BILLKIND.B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"BILLKIND.B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"BILLKIND.B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"BILLKIND.B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"BILLKIND.B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"BILLKIND.B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"BILLKIND.B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"BILLKIND.B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"BILLKIND.B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"BILLKIND.B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"BILLKIND.B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"BILLKIND.B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"BILLKIND.B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"BILLKIND.B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"BILLKIND.B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"BILLKIND.B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"BILLKIND.B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"BILLKIND.B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"BILLKIND.B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"BILLKIND.B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"BILLKIND.B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"BILLKIND.B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"BILLKIND.B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"BILLKIND.B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"BILLKIND.B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"BILLKIND.B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"BILLKIND.B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"BILLKIND.B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"BILLKIND.B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"BILLKIND.B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"BILLKIND.B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"BILLKIND.B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"BILLKIND.B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"BILLKIND.B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"BILLKIND.B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"BILLKIND.B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"BILLKIND.B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"BILLKIND.B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"BILLKIND.B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"REPORT.R_M_IDX","operator":"EQ","value":"{d.M_IDX}","nullSafe":null,"left":null,"right":null,"negate":null,"group":null,"subquery":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
WHERE c.SERIAL_NO = 1
  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER,''))) = N'R_M_IDX={d.M_IDX}';

-- 3) 日志状态：成功转换行标记 CONVERTED
UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'BILLKIND' AND LTRIM(RTRIM(F_ID)) = N'B_M_IDX' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'BOM_COST_M' AND LTRIM(RTRIM(F_ID)) = N'PRO_NO' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'BOM_INSTRUCT_M' AND LTRIM(RTRIM(F_ID)) = N'PRO_NO' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'BOM_REDEPLOY_D' AND LTRIM(RTRIM(F_ID)) = N'PRO_NO' AND SERIAL_NO = 4;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'BOM_STRU_M' AND LTRIM(RTRIM(F_ID)) = N'PRO_NO' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'CAR' AND LTRIM(RTRIM(F_ID)) = N'CHARGE_PERSON' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'CAR_FEE_M' AND LTRIM(RTRIM(F_ID)) = N'EMP_ID' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'CAR_REPAIR_M' AND LTRIM(RTRIM(F_ID)) = N'MOTORMAN' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'CAR_REPAIR_M' AND LTRIM(RTRIM(F_ID)) = N'REPAIR_EMP_ID' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'CAR_TAKEOUT_M' AND LTRIM(RTRIM(F_ID)) = N'EMP_ID' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'CLIENT_PRICE_D' AND LTRIM(RTRIM(F_ID)) = N'PRO_NO' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'COP_CHAFFER_M' AND LTRIM(RTRIM(F_ID)) = N'DEPOT_ID' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'COP_FITIN_D' AND LTRIM(RTRIM(F_ID)) = N'DEPOT_ID' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'COP_FITOUT_D' AND LTRIM(RTRIM(F_ID)) = N'DEPOT_ID' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'COP_ORDER_M' AND LTRIM(RTRIM(F_ID)) = N'DEPOT_ID' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'COP_PREPAY_M' AND LTRIM(RTRIM(F_ID)) = N'BANK_ID' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'COP_QUOTE_M' AND LTRIM(RTRIM(F_ID)) = N'DEPOT_ID' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'COP_RECEIPT_OTHER' AND LTRIM(RTRIM(F_ID)) = N'PREPAY_TYPE' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'COP_RETURN_D' AND LTRIM(RTRIM(F_ID)) = N'BAD_DEPOT_ID' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'COP_RETURN_D' AND LTRIM(RTRIM(F_ID)) = N'DEPOT_ID' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'COP_SEND_D' AND LTRIM(RTRIM(F_ID)) = N'DEPOT_ID' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'CUS_ACCOUNT_M' AND LTRIM(RTRIM(F_ID)) = N'PRO_ID' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'CUS_EXPORT_D' AND LTRIM(RTRIM(F_ID)) = N'PRO_NO' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'CUS_IMPORT_D' AND LTRIM(RTRIM(F_ID)) = N'PRO_NO' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'CUS_MANUAL_BOM' AND LTRIM(RTRIM(F_ID)) = N'PRO_ID' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'CUS_MANUAL_BOM' AND LTRIM(RTRIM(F_ID)) = N'PRO_NO' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'CUS_MANUAL_MAT' AND LTRIM(RTRIM(F_ID)) = N'PRO_ID' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'CUS_MANUAL_MAT' AND LTRIM(RTRIM(F_ID)) = N'PRO_NO' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'CUS_MANUAL_PRO' AND LTRIM(RTRIM(F_ID)) = N'PRO_ID' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'CUS_MANUAL_PRO' AND LTRIM(RTRIM(F_ID)) = N'PRO_NO' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'DEPOT' AND LTRIM(RTRIM(F_ID)) = N'DEPOT_ID' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'HALF_IN_D' AND LTRIM(RTRIM(F_ID)) = N'DEPOT_ID' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'HALF_OUT_D' AND LTRIM(RTRIM(F_ID)) = N'DEPOT_ID' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'HALF_PRO' AND LTRIM(RTRIM(F_ID)) = N'DEPOT_ID' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'HALF_PRO_DEPOT' AND LTRIM(RTRIM(F_ID)) = N'DEPOT_ID' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'HR_ABSENT_D' AND LTRIM(RTRIM(F_ID)) = N'EMP_ID' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'HR_ABSENT_D' AND LTRIM(RTRIM(F_ID)) = N'EMP_NO' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'HR_ADD_D' AND LTRIM(RTRIM(F_ID)) = N'EMP_ID' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'HR_ADD_D' AND LTRIM(RTRIM(F_ID)) = N'EMP_NO' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'HR_ADJUST_D' AND LTRIM(RTRIM(F_ID)) = N'EMP_NO' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'HR_AMERCE_D' AND LTRIM(RTRIM(F_ID)) = N'EMP_ID' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'HR_AMERCE_D' AND LTRIM(RTRIM(F_ID)) = N'EMP_NO' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'HR_APPLY_D' AND LTRIM(RTRIM(F_ID)) = N'EMP_ID' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'HR_APPLY_D' AND LTRIM(RTRIM(F_ID)) = N'EMP_NO' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'HR_AWARD_D' AND LTRIM(RTRIM(F_ID)) = N'EMP_ID' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'HR_AWARD_D' AND LTRIM(RTRIM(F_ID)) = N'EMP_NO' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'HR_BASEPAY_D' AND LTRIM(RTRIM(F_ID)) = N'EMP_ID' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'HR_CERTIFY_D' AND LTRIM(RTRIM(F_ID)) = N'EMP_NO' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'HR_CONTRACT_D' AND LTRIM(RTRIM(F_ID)) = N'EMP_NO' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'HR_DIARY' AND LTRIM(RTRIM(F_ID)) = N'EMP_NO' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'HR_DIMISSION_M' AND LTRIM(RTRIM(F_ID)) = N'EMP_ID' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'HR_DIMISSION_M' AND LTRIM(RTRIM(F_ID)) = N'EMP_NO' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'HR_EMPLOYEE' AND LTRIM(RTRIM(F_ID)) = N'INTRODUCER' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'HR_EMPLOYEE_CARD' AND LTRIM(RTRIM(F_ID)) = N'EMP_ID' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'HR_EMPLOYEE_CARD' AND LTRIM(RTRIM(F_ID)) = N'EMP_NO' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'HR_ENACTMENT_D' AND LTRIM(RTRIM(F_ID)) = N'EMP_NO' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'HR_EVECTION_D' AND LTRIM(RTRIM(F_ID)) = N'EMP_ID' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'HR_EVECTION_D' AND LTRIM(RTRIM(F_ID)) = N'EMP_NO' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'HR_FOREGIFT_D' AND LTRIM(RTRIM(F_ID)) = N'EMP_ID' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'HR_FOREGIFT_D' AND LTRIM(RTRIM(F_ID)) = N'EMP_NO' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'HR_LEAVE_D' AND LTRIM(RTRIM(F_ID)) = N'EMP_ID' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'HR_LEAVE_D' AND LTRIM(RTRIM(F_ID)) = N'EMP_NO' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'HR_LOAN_D' AND LTRIM(RTRIM(F_ID)) = N'EMP_ID' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'HR_LOAN_D' AND LTRIM(RTRIM(F_ID)) = N'EMP_NO' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'HR_PLAN_D' AND LTRIM(RTRIM(F_ID)) = N'EMP_NO' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'HR_RECESS_D' AND LTRIM(RTRIM(F_ID)) = N'EMP_NO' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'HR_REDEPLOY_D' AND LTRIM(RTRIM(F_ID)) = N'EMP_ID' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'HR_REDEPLOY_D' AND LTRIM(RTRIM(F_ID)) = N'EMP_NO' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'HR_SAFE_D' AND LTRIM(RTRIM(F_ID)) = N'EMP_NO' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'HR_SIGN_D' AND LTRIM(RTRIM(F_ID)) = N'EMP_ID' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'HR_SIGN_D' AND LTRIM(RTRIM(F_ID)) = N'EMP_NO' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'HR_SUBTRACT_D' AND LTRIM(RTRIM(F_ID)) = N'EMP_ID' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'HR_SUBTRACT_D' AND LTRIM(RTRIM(F_ID)) = N'EMP_NO' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'HR_WAGE_D' AND LTRIM(RTRIM(F_ID)) = N'EMP_ID' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'HR_WORKTIME_D' AND LTRIM(RTRIM(F_ID)) = N'EMP_ID' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'HRM_ADJUST_D' AND LTRIM(RTRIM(F_ID)) = N'EMP_NO' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'HRM_BASEPAY_D' AND LTRIM(RTRIM(F_ID)) = N'EMP_ID' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'HRM_DIARY' AND LTRIM(RTRIM(F_ID)) = N'EMP_NO' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'HRM_EVECTION_D' AND LTRIM(RTRIM(F_ID)) = N'EMP_ID' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'HRM_EVECTION_D' AND LTRIM(RTRIM(F_ID)) = N'EMP_NO' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'HRM_LEAVE_D' AND LTRIM(RTRIM(F_ID)) = N'EMP_ID' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'HRM_LEAVE_D' AND LTRIM(RTRIM(F_ID)) = N'EMP_NO' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'HRM_RECESS_D' AND LTRIM(RTRIM(F_ID)) = N'EMP_NO' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'HRM_SIGN_D' AND LTRIM(RTRIM(F_ID)) = N'EMP_ID' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'HRM_SIGN_D' AND LTRIM(RTRIM(F_ID)) = N'EMP_NO' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'HRM_WAGE_D' AND LTRIM(RTRIM(F_ID)) = N'EMP_ID' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'INV_BATCH_D' AND LTRIM(RTRIM(F_ID)) = N'DEPOT_ID' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'INV_CHECK_STOCK_D' AND LTRIM(RTRIM(F_ID)) = N'PRO_NO' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'INV_CHECK_STOCK_M' AND LTRIM(RTRIM(F_ID)) = N'DEPOT_ID' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'INV_DEPOT_LOG' AND LTRIM(RTRIM(F_ID)) = N'DEPOT_ID' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'INV_OCCUR_OUT_D' AND LTRIM(RTRIM(F_ID)) = N'PRO_NO' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'INV_OCCUR_OUT_D' AND LTRIM(RTRIM(F_ID)) = N'PRO_NO' AND SERIAL_NO = 4;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'INV_PRO_DEPOT' AND LTRIM(RTRIM(F_ID)) = N'DEPOT_ID' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'INV_PRO_MONTH_D' AND LTRIM(RTRIM(F_ID)) = N'DEPOT_ID' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'MOC_GET_D' AND LTRIM(RTRIM(F_ID)) = N'DEPOT_ID' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'MOC_OUT_PRODUCT_IN_D' AND LTRIM(RTRIM(F_ID)) = N'DEPOT_ID' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'MOC_OUT_PRODUCT_OUT_D' AND LTRIM(RTRIM(F_ID)) = N'DEPOT_ID' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'MOC_PRODUCE_D' AND LTRIM(RTRIM(F_ID)) = N'DEPOT_ID' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'MOC_PRODUCE_M' AND LTRIM(RTRIM(F_ID)) = N'DEPOT_ID' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'MOC_PRODUCE_M' AND LTRIM(RTRIM(F_ID)) = N'LINE_ID' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'MOC_PRODUCE_M' AND LTRIM(RTRIM(F_ID)) = N'LINE_ID' AND SERIAL_NO = 3;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'MOC_PRODUCE_M' AND LTRIM(RTRIM(F_ID)) = N'ORDER_TYPE' AND SERIAL_NO = 4;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'MOC_PRODUCE_M' AND LTRIM(RTRIM(F_ID)) = N'TWDEPOT_ID' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'MOC_PRODUCE_PROCESS_D' AND LTRIM(RTRIM(F_ID)) = N'PROCEDURE_TYPE_ID' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'MOC_PRODUCE_PROCESS_D' AND LTRIM(RTRIM(F_ID)) = N'PROCEDURE_TYPE_NAME' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'MOC_PRODUCT_IN_D' AND LTRIM(RTRIM(F_ID)) = N'DEPOT_ID' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'MOC_PRODUCT_OUT_D' AND LTRIM(RTRIM(F_ID)) = N'DEPOT_ID' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'MOC_PRODUCT_OUT_D' AND LTRIM(RTRIM(F_ID)) = N'PRODUCE_NO' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'MOC_WORK_IN_D' AND LTRIM(RTRIM(F_ID)) = N'DEPOT_ID' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'MOC_WORK_OUT_D' AND LTRIM(RTRIM(F_ID)) = N'NEXT_PROCEDURE_TYPE_ID' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'MOU_ACCEPT_M' AND LTRIM(RTRIM(F_ID)) = N'APPLY_NO' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'MOU_ACCEPT_M' AND LTRIM(RTRIM(F_ID)) = N'APPLY_TYPE' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'MOU_ACCEPT_M' AND LTRIM(RTRIM(F_ID)) = N'APPLY_TYPE' AND SERIAL_NO = 2;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'MOU_ACCEPTDELE_D' AND LTRIM(RTRIM(F_ID)) = N'ACCEPT_NO' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'MOU_ACCEPTDELE_D' AND LTRIM(RTRIM(F_ID)) = N'ACCEPT_NO' AND SERIAL_NO = 2;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'MOU_ACCEPTDELE_D' AND LTRIM(RTRIM(F_ID)) = N'ACCEPT_NO' AND SERIAL_NO = 3;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'MOU_APPLY_M' AND LTRIM(RTRIM(F_ID)) = N'APPLY_NO_OLD' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'MOU_APPLY_M' AND LTRIM(RTRIM(F_ID)) = N'ASSESS_TYPE' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'MOU_APPLY_M' AND LTRIM(RTRIM(F_ID)) = N'ASSESS_TYPE' AND SERIAL_NO = 2;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'MOU_APPLY_M' AND LTRIM(RTRIM(F_ID)) = N'MOULD_CUT' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'MOU_ASSESS_M' AND LTRIM(RTRIM(F_ID)) = N'ASSESS_TYPE' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'MOU_BATCH_M' AND LTRIM(RTRIM(F_ID)) = N'MOULD_ID' AND SERIAL_NO = 2;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'MOU_BATCHIN_D' AND LTRIM(RTRIM(F_ID)) = N'MOULD_ID' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'MOU_BATCHIN_D' AND LTRIM(RTRIM(F_ID)) = N'MOULD_ID' AND SERIAL_NO = 2;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'MOU_BATCHIN_D' AND LTRIM(RTRIM(F_ID)) = N'MOULD_ID' AND SERIAL_NO = 3;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'MOU_BATCHTOP_M' AND LTRIM(RTRIM(F_ID)) = N'MOULD_ID' AND SERIAL_NO = 2;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'MOU_OUT_D' AND LTRIM(RTRIM(F_ID)) = N'MOULD_ID' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'MOU_OUT_D' AND LTRIM(RTRIM(F_ID)) = N'MOULD_ID' AND SERIAL_NO = 2;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'MOU_OUT_D' AND LTRIM(RTRIM(F_ID)) = N'MOULD_ID' AND SERIAL_NO = 3;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'MOU_PRO_D' AND LTRIM(RTRIM(F_ID)) = N'MOULD_ID' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'MOU_PRO_D' AND LTRIM(RTRIM(F_ID)) = N'MOULD_ID' AND SERIAL_NO = 2;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'MOU_PRO_D' AND LTRIM(RTRIM(F_ID)) = N'MOULD_ID' AND SERIAL_NO = 3;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'MOU_PRO_D' AND LTRIM(RTRIM(F_ID)) = N'MOULD_ID' AND SERIAL_NO = 4;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'MOU_PRO_M' AND LTRIM(RTRIM(F_ID)) = N'PRO_NO' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'MOU_SCRAP_D' AND LTRIM(RTRIM(F_ID)) = N'MOULD_ID' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'MOU_SCRAP_D' AND LTRIM(RTRIM(F_ID)) = N'MOULD_ID' AND SERIAL_NO = 2;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'MOU_SCRAP_D' AND LTRIM(RTRIM(F_ID)) = N'MOULD_ID' AND SERIAL_NO = 3;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'PRODUCT' AND LTRIM(RTRIM(F_ID)) = N'STUFF_ID' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'PRODUCT_EDITION' AND LTRIM(RTRIM(F_ID)) = N'DEPOT_ID' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'PUR_APPLY_D' AND LTRIM(RTRIM(F_ID)) = N'DEPOT_ID' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'PUR_CANCEL_D' AND LTRIM(RTRIM(F_ID)) = N'DEPOT_ID' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'PUR_CANCEL_M' AND LTRIM(RTRIM(F_ID)) = N'DEPOT_ID' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'PUR_CHAFFER_M' AND LTRIM(RTRIM(F_ID)) = N'DEPOT_ID' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'PUR_PREPAY_M' AND LTRIM(RTRIM(F_ID)) = N'BANK_ID' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'PUR_PURCHASE_D' AND LTRIM(RTRIM(F_ID)) = N'DEPOT_ID' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'PUR_PURCHASE_D' AND LTRIM(RTRIM(F_ID)) = N'TWDEPOT_ID' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'PUR_PURCHASE_M' AND LTRIM(RTRIM(F_ID)) = N'DEPOT_ID' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'PUR_QUOTE_M' AND LTRIM(RTRIM(F_ID)) = N'DEPOT_ID' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'PUR_RECEIVE_D' AND LTRIM(RTRIM(F_ID)) = N'DEPOT_ID' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'PUR_RECEIVE_M' AND LTRIM(RTRIM(F_ID)) = N'DEPOT_ID' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'QC_SAMPLE_M' AND LTRIM(RTRIM(F_ID)) = N'PRO_NO' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'REPORT' AND LTRIM(RTRIM(F_ID)) = N'Q_M_IDX' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'REPORT' AND LTRIM(RTRIM(F_ID)) = N'R_M_IDX' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'SAMPLE_PRO' AND LTRIM(RTRIM(F_ID)) = N'CUBAGE_UNIT_ID' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'SAMPLE_PRO' AND LTRIM(RTRIM(F_ID)) = N'DEPOT_ID' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'SAMPLE_PRO' AND LTRIM(RTRIM(F_ID)) = N'SIZE_UNIT_ID' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'SAMPLE_PRO' AND LTRIM(RTRIM(F_ID)) = N'STUFF_ID' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'SAMPLE_PRO' AND LTRIM(RTRIM(F_ID)) = N'WEIGHT_UNIT_ID' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'SFC_AMERCE_D' AND LTRIM(RTRIM(F_ID)) = N'EMP_NAME' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'SFC_PAY_D' AND LTRIM(RTRIM(F_ID)) = N'UNIT_ID' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'SFC_PROCESS_IN_D' AND LTRIM(RTRIM(F_ID)) = N'EMP_NAME' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'SFC_PROCESS_M' AND LTRIM(RTRIM(F_ID)) = N'PRO_NO' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'SFC_REWORK_D' AND LTRIM(RTRIM(F_ID)) = N'EMP_NAME' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'SUPPLIER_PRICE_D' AND LTRIM(RTRIM(F_ID)) = N'PRO_NO' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'SYSDG_USER' AND LTRIM(RTRIM(F_ID)) = N'G_IDX' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'SYSDL' AND LTRIM(RTRIM(F_ID)) = N'OWNER' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'WFFORM' AND LTRIM(RTRIM(F_ID)) = N'WF_M_IDX' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'BOM_REDEPLOY_M' AND LTRIM(RTRIM(F_ID)) = N'REDEPLOY_TYPE' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'CAR_ADDUP_M' AND LTRIM(RTRIM(F_ID)) = N'ADDUP_TYPE' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'CAR_FEE_M' AND LTRIM(RTRIM(F_ID)) = N'FEE_TYPE' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'CAR_FILLOIL_M' AND LTRIM(RTRIM(F_ID)) = N'FILLOIL_TYPE' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'CAR_MISSION_M' AND LTRIM(RTRIM(F_ID)) = N'MISSION_TYPE' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'CAR_REPAIR_M' AND LTRIM(RTRIM(F_ID)) = N'REPAIR_TYPE' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'COP_ACCOUNT_DD_M' AND LTRIM(RTRIM(F_ID)) = N'ACCOUNT_TYPE' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'COP_ACCOUNT_M' AND LTRIM(RTRIM(F_ID)) = N'ACCOUNT_TYPE' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'COP_BACK_M' AND LTRIM(RTRIM(F_ID)) = N'BACK_TYPE' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'COP_CALLBACK_M' AND LTRIM(RTRIM(F_ID)) = N'CALLBACK_TYPE' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'COP_CHAFFER_M' AND LTRIM(RTRIM(F_ID)) = N'CHAFFER_TYPE' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'COP_FITIN_M' AND LTRIM(RTRIM(F_ID)) = N'FITIN_TYPE' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'COP_FITOUT_M' AND LTRIM(RTRIM(F_ID)) = N'FITOUT_TYPE' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'COP_ORDER_CALC_M' AND LTRIM(RTRIM(F_ID)) = N'CALC_ORDER_TYPE' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'COP_ORDER_CHANGE_M' AND LTRIM(RTRIM(F_ID)) = N'CHANGE_ORDER_TYPE' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'COP_ORDER_M' AND LTRIM(RTRIM(F_ID)) = N'ORDER_TYPE' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'COP_PREPAY_M' AND LTRIM(RTRIM(F_ID)) = N'PREPAY_TYPE' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'COP_QUOTE_M' AND LTRIM(RTRIM(F_ID)) = N'QUOTE_TYPE' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'COP_RETURN_M' AND LTRIM(RTRIM(F_ID)) = N'RETURN_TYPE' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'COP_SEND_M' AND LTRIM(RTRIM(F_ID)) = N'SEND_TYPE' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'COP_SHIPMENT_M' AND LTRIM(RTRIM(F_ID)) = N'SHIPMENT_TYPE' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'CUS_ACCOUNT_M' AND LTRIM(RTRIM(F_ID)) = N'ACCOUNT_TYPE' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'CUS_EXPORT_M' AND LTRIM(RTRIM(F_ID)) = N'EXPORT_TYPE' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'CUS_IMPORT_M' AND LTRIM(RTRIM(F_ID)) = N'IMPORT_TYPE' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'CUS_MATERIN_M' AND LTRIM(RTRIM(F_ID)) = N'BILL_TYPE' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'CUS_MATEROUT_M' AND LTRIM(RTRIM(F_ID)) = N'BILL_TYPE' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'CUS_PACK_M' AND LTRIM(RTRIM(F_ID)) = N'BILL_TYPE' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'CUS_PROIN_M' AND LTRIM(RTRIM(F_ID)) = N'BILL_TYPE' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'CUS_PROOUT_M' AND LTRIM(RTRIM(F_ID)) = N'BILL_TYPE' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'CUS_PURCHASE_M' AND LTRIM(RTRIM(F_ID)) = N'PURCHASE_TYPE' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'HALF_IN_M' AND LTRIM(RTRIM(F_ID)) = N'IN_TYPE' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'HALF_OUT_M' AND LTRIM(RTRIM(F_ID)) = N'OUT_TYPE' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'HR_ABSENT_M' AND LTRIM(RTRIM(F_ID)) = N'ABSENT_TYPE' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'HR_ADD_M' AND LTRIM(RTRIM(F_ID)) = N'ADD_TYPE' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'HR_ADJUST_M' AND LTRIM(RTRIM(F_ID)) = N'ADJUST_TYPE' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'HR_AMERCE_M' AND LTRIM(RTRIM(F_ID)) = N'AMERCE_TYPE' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'HR_APPLY_M' AND LTRIM(RTRIM(F_ID)) = N'APPLY_TYPE' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'HR_AWARD_M' AND LTRIM(RTRIM(F_ID)) = N'AWARD_TYPE' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'HR_BASEPAY_M' AND LTRIM(RTRIM(F_ID)) = N'BASEPAY_TYPE' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'HR_CERTIFY_M' AND LTRIM(RTRIM(F_ID)) = N'CERTIFY_TYPE' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'HR_CONTRACT_M' AND LTRIM(RTRIM(F_ID)) = N'CONT_TYPE' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'HR_DIMISSION_M' AND LTRIM(RTRIM(F_ID)) = N'DIMISSION_TYPE' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'HR_ENACTMENT_M' AND LTRIM(RTRIM(F_ID)) = N'ENACTMENT_TYPE' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'HR_EVECTION_M' AND LTRIM(RTRIM(F_ID)) = N'EVECTION_TYPE' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'HR_EXCHANGE_M' AND LTRIM(RTRIM(F_ID)) = N'EXCHANGE_TYPE' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'HR_FOREGIFT_M' AND LTRIM(RTRIM(F_ID)) = N'FOREGIFT_TYPE' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'HR_LEAVE_M' AND LTRIM(RTRIM(F_ID)) = N'LEAVE_TYPE' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'HR_LOAN_M' AND LTRIM(RTRIM(F_ID)) = N'LOAN_TYPE' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'HR_PLAN_M' AND LTRIM(RTRIM(F_ID)) = N'PLAN_TYPE' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'HR_RECESS_M' AND LTRIM(RTRIM(F_ID)) = N'RECESS_TYPE' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'HR_REDEPLOY_M' AND LTRIM(RTRIM(F_ID)) = N'REDEPLOY_TYPE' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'HR_SAFE_M' AND LTRIM(RTRIM(F_ID)) = N'SAFE_TYPE' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'HR_SIGN_M' AND LTRIM(RTRIM(F_ID)) = N'SIGN_TYPE' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'HR_SUBTRACT_M' AND LTRIM(RTRIM(F_ID)) = N'SUBTRACT_TYPE' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'HR_TXT_M' AND LTRIM(RTRIM(F_ID)) = N'TXT_TYPE' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'HR_WAGE_M' AND LTRIM(RTRIM(F_ID)) = N'WAGE_TYPE' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'HR_WORKTIME_M' AND LTRIM(RTRIM(F_ID)) = N'WORKTIME_TYPE' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'HRM_ADJUST_M' AND LTRIM(RTRIM(F_ID)) = N'ADJUST_TYPE' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'HRM_BASEPAY_M' AND LTRIM(RTRIM(F_ID)) = N'BASEPAY_TYPE' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'HRM_EVECTION_M' AND LTRIM(RTRIM(F_ID)) = N'EVECTION_TYPE' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'HRM_EXCHANGE_M' AND LTRIM(RTRIM(F_ID)) = N'EXCHANGE_TYPE' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'HRM_LEAVE_M' AND LTRIM(RTRIM(F_ID)) = N'LEAVE_TYPE' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'HRM_PLAN_M' AND LTRIM(RTRIM(F_ID)) = N'PLAN_TYPE' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'HRM_RECESS_M' AND LTRIM(RTRIM(F_ID)) = N'RECESS_TYPE' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'HRM_SIGN_M' AND LTRIM(RTRIM(F_ID)) = N'SIGN_TYPE' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'HRM_WAGE_M' AND LTRIM(RTRIM(F_ID)) = N'WAGE_TYPE' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'INV_CHECK_STOCK_M' AND LTRIM(RTRIM(F_ID)) = N'CHECK_STOCK_TYPE' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'INV_LOAN_M' AND LTRIM(RTRIM(F_ID)) = N'LOAN_TYPE' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'INV_OCCUR_ADJUST_M' AND LTRIM(RTRIM(F_ID)) = N'OCCUR_TYPE' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'INV_OCCUR_IN_M' AND LTRIM(RTRIM(F_ID)) = N'OCCUR_TYPE' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'INV_OCCUR_INIT_M' AND LTRIM(RTRIM(F_ID)) = N'OCCUR_TYPE' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'INV_OCCUR_OUT_M' AND LTRIM(RTRIM(F_ID)) = N'OCCUR_TYPE' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'INV_OCCUR_SCRAP_M' AND LTRIM(RTRIM(F_ID)) = N'OCCUR_TYPE' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'INV_OCCUR_TRANSFER_M' AND LTRIM(RTRIM(F_ID)) = N'OCCUR_TYPE' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'INV_RETURN_M' AND LTRIM(RTRIM(F_ID)) = N'RETURN_TYPE' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'MOC_BACK_M' AND LTRIM(RTRIM(F_ID)) = N'BACK_TYPE' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'MOC_GET_M' AND LTRIM(RTRIM(F_ID)) = N'GET_TYPE' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'MOC_OUT_PRODUCT_IN_M' AND LTRIM(RTRIM(F_ID)) = N'OUT_PRODUCT_IN_TYPE' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'MOC_OUT_PRODUCT_OUT_M' AND LTRIM(RTRIM(F_ID)) = N'OUT_PRODUCT_OUT_TYPE' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'MOC_PLAN_M' AND LTRIM(RTRIM(F_ID)) = N'PLAN_TYPE' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'MOC_PRODUCE_CHANGE_M' AND LTRIM(RTRIM(F_ID)) = N'CHANGE_PRODUCE_TYPE' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'MOC_PRODUCE_INSTRUCT_M' AND LTRIM(RTRIM(F_ID)) = N'ORDER_TYPE' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'MOC_PRODUCE_INSTRUCT_M' AND LTRIM(RTRIM(F_ID)) = N'PRODUCE_TYPE' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'MOC_PRODUCE_M' AND LTRIM(RTRIM(F_ID)) = N'PRODUCE_TYPE' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'MOC_PRODUCT_IN_M' AND LTRIM(RTRIM(F_ID)) = N'PRODUCT_IN_TYPE' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'MOC_PRODUCT_OUT_M' AND LTRIM(RTRIM(F_ID)) = N'PRODUCT_OUT_TYPE' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'MOC_WORK_IN_M' AND LTRIM(RTRIM(F_ID)) = N'WORK_IN_TYPE' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'MOC_WORK_M' AND LTRIM(RTRIM(F_ID)) = N'WORK_TYPE' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'MOC_WORK_OUT_M' AND LTRIM(RTRIM(F_ID)) = N'WORK_OUT_TYPE' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'MOU_ACCEPT_M' AND LTRIM(RTRIM(F_ID)) = N'ACCEPT_TYPE' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'MOU_ACCEPTDELE_M' AND LTRIM(RTRIM(F_ID)) = N'ACCEPTDELE_TYPE' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'MOU_APPLY_M' AND LTRIM(RTRIM(F_ID)) = N'APPLY_TYPE' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'MOU_BATCH_M' AND LTRIM(RTRIM(F_ID)) = N'BATCH_TYPE' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'MOU_BATCHIN_M' AND LTRIM(RTRIM(F_ID)) = N'BATCHIN_TYPE' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'MOU_BATCHTOP_M' AND LTRIM(RTRIM(F_ID)) = N'BATCH_TYPE' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'MOU_GET_M' AND LTRIM(RTRIM(F_ID)) = N'GET_TYPE' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'MOU_GET2_M' AND LTRIM(RTRIM(F_ID)) = N'GET_TYPE' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'MOU_IN_M' AND LTRIM(RTRIM(F_ID)) = N'IN_TYPE' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'MOU_OUT_M' AND LTRIM(RTRIM(F_ID)) = N'OUT_TYPE' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'MOU_SCRAP_M' AND LTRIM(RTRIM(F_ID)) = N'SCRAP_TYPE' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'PUR_APPLY_M' AND LTRIM(RTRIM(F_ID)) = N'APPLY_TYPE' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'PUR_CALLBACK_M' AND LTRIM(RTRIM(F_ID)) = N'CALLBACK_TYPE' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'PUR_CANCEL_M' AND LTRIM(RTRIM(F_ID)) = N'CANCEL_TYPE' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'PUR_CHAFFER_M' AND LTRIM(RTRIM(F_ID)) = N'CHAFFER_TYPE' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'PUR_DUE_M' AND LTRIM(RTRIM(F_ID)) = N'DUE_TYPE' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'PUR_PACK_M' AND LTRIM(RTRIM(F_ID)) = N'PACK_TYPE' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'PUR_PREPAY_M' AND LTRIM(RTRIM(F_ID)) = N'PREPAY_TYPE' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'PUR_PURCHASE_CHANGE_M' AND LTRIM(RTRIM(F_ID)) = N'CHANGE_PURCHASE_TYPE' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'PUR_PURCHASE_M' AND LTRIM(RTRIM(F_ID)) = N'PURCHASE_TYPE' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'PUR_QUOTE_M' AND LTRIM(RTRIM(F_ID)) = N'QUOTE_TYPE' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'PUR_RECEIVE_M' AND LTRIM(RTRIM(F_ID)) = N'RECEIVE_TYPE' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'QC_ANALYSIS_M' AND LTRIM(RTRIM(F_ID)) = N'ANALYSIS_TYPE' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'QC_APPLY_M' AND LTRIM(RTRIM(F_ID)) = N'APPLY_TYPE' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'QC_COMPLAIN_M' AND LTRIM(RTRIM(F_ID)) = N'COMPLAIN_TYPE' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'QC_EXCEPTION_M' AND LTRIM(RTRIM(F_ID)) = N'EXCEPTION_TYPE' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'QC_REWORK_M' AND LTRIM(RTRIM(F_ID)) = N'REWORK_TYPE' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'QC_SCRAP_M' AND LTRIM(RTRIM(F_ID)) = N'SCRAP_TYPE' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'SAM_APPLY_M' AND LTRIM(RTRIM(F_ID)) = N'APPLY_TYPE' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'SAM_IN_M' AND LTRIM(RTRIM(F_ID)) = N'IN_TYPE' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'SAM_OUT_M' AND LTRIM(RTRIM(F_ID)) = N'OUT_TYPE' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'SFC_DAILY_M' AND LTRIM(RTRIM(F_ID)) = N'DAILY_TYPE' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'SFC_PAY_M' AND LTRIM(RTRIM(F_ID)) = N'PAY_TYPE' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'SYS_WORK_TASK' AND LTRIM(RTRIM(F_ID)) = N'WORK_TYPE' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'CONVERTED'
WHERE T_ID = N'SYSDD_REPORT' AND LTRIM(RTRIM(F_ID)) = N'REPORT_ID' AND SERIAL_NO = 1;

-- 4) 未能自动转换/重转的行降级 MANUAL（FILTER_STRUCT 置 NULL fail-closed）
UPDATE c SET FILTER_STRUCT = NULL
FROM dbo.FIELDS_CHOOSER c
WHERE c.T_ID = N'CAR_MISSION_D' AND c.F_ID = N'SEND_NO' AND c.SERIAL_NO = 1;
UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'MANUAL'
WHERE T_ID = N'CAR_MISSION_D' AND LTRIM(RTRIM(F_ID)) = N'SEND_NO' AND SERIAL_NO = 1;

UPDATE c SET FILTER_STRUCT = NULL
FROM dbo.FIELDS_CHOOSER c
WHERE c.T_ID = N'COP_CHAFFER_D' AND c.F_ID = N'PRO_NO' AND c.SERIAL_NO = 1;
UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'MANUAL'
WHERE T_ID = N'COP_CHAFFER_D' AND LTRIM(RTRIM(F_ID)) = N'PRO_NO' AND SERIAL_NO = 1;

UPDATE c SET FILTER_STRUCT = NULL
FROM dbo.FIELDS_CHOOSER c
WHERE c.T_ID = N'COP_FITIN_M' AND c.F_ID = N'CLIENT_ID' AND c.SERIAL_NO = 1;
UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'MANUAL'
WHERE T_ID = N'COP_FITIN_M' AND LTRIM(RTRIM(F_ID)) = N'CLIENT_ID' AND SERIAL_NO = 1;

UPDATE c SET FILTER_STRUCT = NULL
FROM dbo.FIELDS_CHOOSER c
WHERE c.T_ID = N'COP_FITOUT_M' AND c.F_ID = N'CLIENT_ID' AND c.SERIAL_NO = 1;
UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'MANUAL'
WHERE T_ID = N'COP_FITOUT_M' AND LTRIM(RTRIM(F_ID)) = N'CLIENT_ID' AND SERIAL_NO = 1;

UPDATE c SET FILTER_STRUCT = NULL
FROM dbo.FIELDS_CHOOSER c
WHERE c.T_ID = N'COP_ORDER_CALC_D' AND c.F_ID = N'LINE_ID' AND c.SERIAL_NO = 2;
UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'MANUAL'
WHERE T_ID = N'COP_ORDER_CALC_D' AND LTRIM(RTRIM(F_ID)) = N'LINE_ID' AND SERIAL_NO = 2;

UPDATE c SET FILTER_STRUCT = NULL
FROM dbo.FIELDS_CHOOSER c
WHERE c.T_ID = N'COP_QUOTE_D' AND c.F_ID = N'PRO_NO' AND c.SERIAL_NO = 2;
UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'MANUAL'
WHERE T_ID = N'COP_QUOTE_D' AND LTRIM(RTRIM(F_ID)) = N'PRO_NO' AND SERIAL_NO = 2;

UPDATE c SET FILTER_STRUCT = NULL
FROM dbo.FIELDS_CHOOSER c
WHERE c.T_ID = N'COP_QUOTE_D' AND c.F_ID = N'PRO_NO' AND c.SERIAL_NO = 3;
UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'MANUAL'
WHERE T_ID = N'COP_QUOTE_D' AND LTRIM(RTRIM(F_ID)) = N'PRO_NO' AND SERIAL_NO = 3;

UPDATE c SET FILTER_STRUCT = NULL
FROM dbo.FIELDS_CHOOSER c
WHERE c.T_ID = N'COP_QUOTE_D' AND c.F_ID = N'PRO_NO' AND c.SERIAL_NO = 4;
UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'MANUAL'
WHERE T_ID = N'COP_QUOTE_D' AND LTRIM(RTRIM(F_ID)) = N'PRO_NO' AND SERIAL_NO = 4;

UPDATE c SET FILTER_STRUCT = NULL
FROM dbo.FIELDS_CHOOSER c
WHERE c.T_ID = N'COP_RECEIPT_OTHER' AND c.F_ID = N'CLIENT_ID' AND c.SERIAL_NO = 1;
UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'MANUAL'
WHERE T_ID = N'COP_RECEIPT_OTHER' AND LTRIM(RTRIM(F_ID)) = N'CLIENT_ID' AND SERIAL_NO = 1;

UPDATE c SET FILTER_STRUCT = NULL
FROM dbo.FIELDS_CHOOSER c
WHERE c.T_ID = N'CUS_PACK_M' AND c.F_ID = N'CLIENT_NAME' AND c.SERIAL_NO = 1;
UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'MANUAL'
WHERE T_ID = N'CUS_PACK_M' AND LTRIM(RTRIM(F_ID)) = N'CLIENT_NAME' AND SERIAL_NO = 1;

UPDATE c SET FILTER_STRUCT = NULL
FROM dbo.FIELDS_CHOOSER c
WHERE c.T_ID = N'HALF_PRO' AND c.F_ID = N'PRO_NO' AND c.SERIAL_NO = 1;
UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'MANUAL'
WHERE T_ID = N'HALF_PRO' AND LTRIM(RTRIM(F_ID)) = N'PRO_NO' AND SERIAL_NO = 1;

UPDATE c SET FILTER_STRUCT = NULL
FROM dbo.FIELDS_CHOOSER c
WHERE c.T_ID = N'HR_ABSENT_D' AND c.F_ID = N'ABSENT_TYPE' AND c.SERIAL_NO = 1;
UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'MANUAL'
WHERE T_ID = N'HR_ABSENT_D' AND LTRIM(RTRIM(F_ID)) = N'ABSENT_TYPE' AND SERIAL_NO = 1;

UPDATE c SET FILTER_STRUCT = NULL
FROM dbo.FIELDS_CHOOSER c
WHERE c.T_ID = N'HR_ADD_D' AND c.F_ID = N'ADD_TYPE' AND c.SERIAL_NO = 1;
UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'MANUAL'
WHERE T_ID = N'HR_ADD_D' AND LTRIM(RTRIM(F_ID)) = N'ADD_TYPE' AND SERIAL_NO = 1;

UPDATE c SET FILTER_STRUCT = NULL
FROM dbo.FIELDS_CHOOSER c
WHERE c.T_ID = N'HR_AMERCE_D' AND c.F_ID = N'AMERCE_TYPE' AND c.SERIAL_NO = 1;
UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'MANUAL'
WHERE T_ID = N'HR_AMERCE_D' AND LTRIM(RTRIM(F_ID)) = N'AMERCE_TYPE' AND SERIAL_NO = 1;

UPDATE c SET FILTER_STRUCT = NULL
FROM dbo.FIELDS_CHOOSER c
WHERE c.T_ID = N'HR_APPLY_D' AND c.F_ID = N'APPLY_TYPE' AND c.SERIAL_NO = 1;
UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'MANUAL'
WHERE T_ID = N'HR_APPLY_D' AND LTRIM(RTRIM(F_ID)) = N'APPLY_TYPE' AND SERIAL_NO = 1;

UPDATE c SET FILTER_STRUCT = NULL
FROM dbo.FIELDS_CHOOSER c
WHERE c.T_ID = N'HR_AWARD_D' AND c.F_ID = N'AWARD_TYPE' AND c.SERIAL_NO = 1;
UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'MANUAL'
WHERE T_ID = N'HR_AWARD_D' AND LTRIM(RTRIM(F_ID)) = N'AWARD_TYPE' AND SERIAL_NO = 1;

UPDATE c SET FILTER_STRUCT = NULL
FROM dbo.FIELDS_CHOOSER c
WHERE c.T_ID = N'HR_BASEPAY_D' AND c.F_ID = N'BASEPAY_TYPE' AND c.SERIAL_NO = 1;
UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'MANUAL'
WHERE T_ID = N'HR_BASEPAY_D' AND LTRIM(RTRIM(F_ID)) = N'BASEPAY_TYPE' AND SERIAL_NO = 1;

UPDATE c SET FILTER_STRUCT = NULL
FROM dbo.FIELDS_CHOOSER c
WHERE c.T_ID = N'HR_EVECTION_D' AND c.F_ID = N'EVECTION_TYPE' AND c.SERIAL_NO = 1;
UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'MANUAL'
WHERE T_ID = N'HR_EVECTION_D' AND LTRIM(RTRIM(F_ID)) = N'EVECTION_TYPE' AND SERIAL_NO = 1;

UPDATE c SET FILTER_STRUCT = NULL
FROM dbo.FIELDS_CHOOSER c
WHERE c.T_ID = N'HR_FOREGIFT_D' AND c.F_ID = N'FOREGIFT_TYPE' AND c.SERIAL_NO = 1;
UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'MANUAL'
WHERE T_ID = N'HR_FOREGIFT_D' AND LTRIM(RTRIM(F_ID)) = N'FOREGIFT_TYPE' AND SERIAL_NO = 1;

UPDATE c SET FILTER_STRUCT = NULL
FROM dbo.FIELDS_CHOOSER c
WHERE c.T_ID = N'HR_LEAVE_D' AND c.F_ID = N'LEAVE_TYPE' AND c.SERIAL_NO = 1;
UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'MANUAL'
WHERE T_ID = N'HR_LEAVE_D' AND LTRIM(RTRIM(F_ID)) = N'LEAVE_TYPE' AND SERIAL_NO = 1;

UPDATE c SET FILTER_STRUCT = NULL
FROM dbo.FIELDS_CHOOSER c
WHERE c.T_ID = N'HR_LOAN_D' AND c.F_ID = N'LOAN_TYPE' AND c.SERIAL_NO = 1;
UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'MANUAL'
WHERE T_ID = N'HR_LOAN_D' AND LTRIM(RTRIM(F_ID)) = N'LOAN_TYPE' AND SERIAL_NO = 1;

UPDATE c SET FILTER_STRUCT = NULL
FROM dbo.FIELDS_CHOOSER c
WHERE c.T_ID = N'HR_REDEPLOY_D' AND c.F_ID = N'REDEPLOY_TYPE' AND c.SERIAL_NO = 1;
UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'MANUAL'
WHERE T_ID = N'HR_REDEPLOY_D' AND LTRIM(RTRIM(F_ID)) = N'REDEPLOY_TYPE' AND SERIAL_NO = 1;

UPDATE c SET FILTER_STRUCT = NULL
FROM dbo.FIELDS_CHOOSER c
WHERE c.T_ID = N'HR_SETUP' AND c.F_ID = N'WAGE_ADD' AND c.SERIAL_NO = 1;
UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'MANUAL'
WHERE T_ID = N'HR_SETUP' AND LTRIM(RTRIM(F_ID)) = N'WAGE_ADD' AND SERIAL_NO = 1;

UPDATE c SET FILTER_STRUCT = NULL
FROM dbo.FIELDS_CHOOSER c
WHERE c.T_ID = N'HR_SUBTRACT_D' AND c.F_ID = N'SUBTRACT_TYPE' AND c.SERIAL_NO = 1;
UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'MANUAL'
WHERE T_ID = N'HR_SUBTRACT_D' AND LTRIM(RTRIM(F_ID)) = N'SUBTRACT_TYPE' AND SERIAL_NO = 1;

UPDATE c SET FILTER_STRUCT = NULL
FROM dbo.FIELDS_CHOOSER c
WHERE c.T_ID = N'HR_WAGE_D' AND c.F_ID = N'WAGE_TYPE' AND c.SERIAL_NO = 1;
UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'MANUAL'
WHERE T_ID = N'HR_WAGE_D' AND LTRIM(RTRIM(F_ID)) = N'WAGE_TYPE' AND SERIAL_NO = 1;

UPDATE c SET FILTER_STRUCT = NULL
FROM dbo.FIELDS_CHOOSER c
WHERE c.T_ID = N'HR_WORKTIME_D' AND c.F_ID = N'WORKTIME_TYPE' AND c.SERIAL_NO = 1;
UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'MANUAL'
WHERE T_ID = N'HR_WORKTIME_D' AND LTRIM(RTRIM(F_ID)) = N'WORKTIME_TYPE' AND SERIAL_NO = 1;

UPDATE c SET FILTER_STRUCT = NULL
FROM dbo.FIELDS_CHOOSER c
WHERE c.T_ID = N'HRM_BASEPAY_D' AND c.F_ID = N'BASEPAY_TYPE' AND c.SERIAL_NO = 1;
UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'MANUAL'
WHERE T_ID = N'HRM_BASEPAY_D' AND LTRIM(RTRIM(F_ID)) = N'BASEPAY_TYPE' AND SERIAL_NO = 1;

UPDATE c SET FILTER_STRUCT = NULL
FROM dbo.FIELDS_CHOOSER c
WHERE c.T_ID = N'HRM_EVECTION_D' AND c.F_ID = N'EVECTION_TYPE' AND c.SERIAL_NO = 1;
UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'MANUAL'
WHERE T_ID = N'HRM_EVECTION_D' AND LTRIM(RTRIM(F_ID)) = N'EVECTION_TYPE' AND SERIAL_NO = 1;

UPDATE c SET FILTER_STRUCT = NULL
FROM dbo.FIELDS_CHOOSER c
WHERE c.T_ID = N'HRM_LEAVE_D' AND c.F_ID = N'LEAVE_TYPE' AND c.SERIAL_NO = 1;
UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'MANUAL'
WHERE T_ID = N'HRM_LEAVE_D' AND LTRIM(RTRIM(F_ID)) = N'LEAVE_TYPE' AND SERIAL_NO = 1;

UPDATE c SET FILTER_STRUCT = NULL
FROM dbo.FIELDS_CHOOSER c
WHERE c.T_ID = N'HRM_WAGE_D' AND c.F_ID = N'WAGE_TYPE' AND c.SERIAL_NO = 1;
UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'MANUAL'
WHERE T_ID = N'HRM_WAGE_D' AND LTRIM(RTRIM(F_ID)) = N'WAGE_TYPE' AND SERIAL_NO = 1;

UPDATE c SET FILTER_STRUCT = NULL
FROM dbo.FIELDS_CHOOSER c
WHERE c.T_ID = N'INV_PRO_DEPOT' AND c.F_ID = N'CLIENT_ID' AND c.SERIAL_NO = 1;
UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'MANUAL'
WHERE T_ID = N'INV_PRO_DEPOT' AND LTRIM(RTRIM(F_ID)) = N'CLIENT_ID' AND SERIAL_NO = 1;

UPDATE c SET FILTER_STRUCT = NULL
FROM dbo.FIELDS_CHOOSER c
WHERE c.T_ID = N'INV_PRO_MONTH_M' AND c.F_ID = N'MONTH_TYPE' AND c.SERIAL_NO = 1;
UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'MANUAL'
WHERE T_ID = N'INV_PRO_MONTH_M' AND LTRIM(RTRIM(F_ID)) = N'MONTH_TYPE' AND SERIAL_NO = 1;

UPDATE c SET FILTER_STRUCT = NULL
FROM dbo.FIELDS_CHOOSER c
WHERE c.T_ID = N'INVOICE_OUT_M' AND c.F_ID = N'CLIENT_ID' AND c.SERIAL_NO = 1;
UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'MANUAL'
WHERE T_ID = N'INVOICE_OUT_M' AND LTRIM(RTRIM(F_ID)) = N'CLIENT_ID' AND SERIAL_NO = 1;

UPDATE c SET FILTER_STRUCT = NULL
FROM dbo.FIELDS_CHOOSER c
WHERE c.T_ID = N'MOC_PRODUCE_IN_M' AND c.F_ID = N'LINE_ID' AND c.SERIAL_NO = 2;
UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'MANUAL'
WHERE T_ID = N'MOC_PRODUCE_IN_M' AND LTRIM(RTRIM(F_ID)) = N'LINE_ID' AND SERIAL_NO = 2;

UPDATE c SET FILTER_STRUCT = NULL
FROM dbo.FIELDS_CHOOSER c
WHERE c.T_ID = N'MOC_PRODUCE_M' AND c.F_ID = N'ORDER_TYPE' AND c.SERIAL_NO = 2;
UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'MANUAL'
WHERE T_ID = N'MOC_PRODUCE_M' AND LTRIM(RTRIM(F_ID)) = N'ORDER_TYPE' AND SERIAL_NO = 2;

UPDATE c SET FILTER_STRUCT = NULL
FROM dbo.FIELDS_CHOOSER c
WHERE c.T_ID = N'MOC_PRODUCT_IN_M' AND c.F_ID = N'LINE_ID' AND c.SERIAL_NO = 2;
UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'MANUAL'
WHERE T_ID = N'MOC_PRODUCT_IN_M' AND LTRIM(RTRIM(F_ID)) = N'LINE_ID' AND SERIAL_NO = 2;

UPDATE c SET FILTER_STRUCT = NULL
FROM dbo.FIELDS_CHOOSER c
WHERE c.T_ID = N'MOC_PRODUCT_OUT_M' AND c.F_ID = N'LINE_ID' AND c.SERIAL_NO = 2;
UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'MANUAL'
WHERE T_ID = N'MOC_PRODUCT_OUT_M' AND LTRIM(RTRIM(F_ID)) = N'LINE_ID' AND SERIAL_NO = 2;

UPDATE c SET FILTER_STRUCT = NULL
FROM dbo.FIELDS_CHOOSER c
WHERE c.T_ID = N'MOC_WORK_D' AND c.F_ID = N'SIZE_UNIT_ID' AND c.SERIAL_NO = 1;
UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'MANUAL'
WHERE T_ID = N'MOC_WORK_D' AND LTRIM(RTRIM(F_ID)) = N'SIZE_UNIT_ID' AND SERIAL_NO = 1;

UPDATE c SET FILTER_STRUCT = NULL
FROM dbo.FIELDS_CHOOSER c
WHERE c.T_ID = N'MOC_WORK_IN_D' AND c.F_ID = N'CLIENT_ID' AND c.SERIAL_NO = 1;
UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'MANUAL'
WHERE T_ID = N'MOC_WORK_IN_D' AND LTRIM(RTRIM(F_ID)) = N'CLIENT_ID' AND SERIAL_NO = 1;

UPDATE c SET FILTER_STRUCT = NULL
FROM dbo.FIELDS_CHOOSER c
WHERE c.T_ID = N'MOC_WORK_IN_M' AND c.F_ID = N'LINE_ID' AND c.SERIAL_NO = 2;
UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'MANUAL'
WHERE T_ID = N'MOC_WORK_IN_M' AND LTRIM(RTRIM(F_ID)) = N'LINE_ID' AND SERIAL_NO = 2;

UPDATE c SET FILTER_STRUCT = NULL
FROM dbo.FIELDS_CHOOSER c
WHERE c.T_ID = N'MOC_WORK_M' AND c.F_ID = N'LINE_ID' AND c.SERIAL_NO = 2;
UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'MANUAL'
WHERE T_ID = N'MOC_WORK_M' AND LTRIM(RTRIM(F_ID)) = N'LINE_ID' AND SERIAL_NO = 2;

UPDATE c SET FILTER_STRUCT = NULL
FROM dbo.FIELDS_CHOOSER c
WHERE c.T_ID = N'MOC_WORK_OUT_M' AND c.F_ID = N'LINE_ID' AND c.SERIAL_NO = 2;
UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'MANUAL'
WHERE T_ID = N'MOC_WORK_OUT_M' AND LTRIM(RTRIM(F_ID)) = N'LINE_ID' AND SERIAL_NO = 2;

UPDATE c SET FILTER_STRUCT = NULL
FROM dbo.FIELDS_CHOOSER c
WHERE c.T_ID = N'MOU_ACCEPT_M' AND c.F_ID = N'ELEMENT_PRO_NO1' AND c.SERIAL_NO = 1;
UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'MANUAL'
WHERE T_ID = N'MOU_ACCEPT_M' AND LTRIM(RTRIM(F_ID)) = N'ELEMENT_PRO_NO1' AND SERIAL_NO = 1;

UPDATE c SET FILTER_STRUCT = NULL
FROM dbo.FIELDS_CHOOSER c
WHERE c.T_ID = N'MOU_ACCEPT_M' AND c.F_ID = N'ELEMENT_PRO_NO2' AND c.SERIAL_NO = 1;
UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'MANUAL'
WHERE T_ID = N'MOU_ACCEPT_M' AND LTRIM(RTRIM(F_ID)) = N'ELEMENT_PRO_NO2' AND SERIAL_NO = 1;

UPDATE c SET FILTER_STRUCT = NULL
FROM dbo.FIELDS_CHOOSER c
WHERE c.T_ID = N'MOU_ACCEPT_M' AND c.F_ID = N'ELEMENT_PRO_NO3' AND c.SERIAL_NO = 1;
UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'MANUAL'
WHERE T_ID = N'MOU_ACCEPT_M' AND LTRIM(RTRIM(F_ID)) = N'ELEMENT_PRO_NO3' AND SERIAL_NO = 1;

UPDATE c SET FILTER_STRUCT = NULL
FROM dbo.FIELDS_CHOOSER c
WHERE c.T_ID = N'MOU_ACCEPT_M' AND c.F_ID = N'PRO_NO' AND c.SERIAL_NO = 1;
UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'MANUAL'
WHERE T_ID = N'MOU_ACCEPT_M' AND LTRIM(RTRIM(F_ID)) = N'PRO_NO' AND SERIAL_NO = 1;

UPDATE c SET FILTER_STRUCT = NULL
FROM dbo.FIELDS_CHOOSER c
WHERE c.T_ID = N'MOU_ACCEPTDELE_D' AND c.F_ID = N'PRO_NO' AND c.SERIAL_NO = 1;
UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'MANUAL'
WHERE T_ID = N'MOU_ACCEPTDELE_D' AND LTRIM(RTRIM(F_ID)) = N'PRO_NO' AND SERIAL_NO = 1;

UPDATE c SET FILTER_STRUCT = NULL
FROM dbo.FIELDS_CHOOSER c
WHERE c.T_ID = N'MOU_APPLY_M' AND c.F_ID = N'CLIENT_ID' AND c.SERIAL_NO = 1;
UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'MANUAL'
WHERE T_ID = N'MOU_APPLY_M' AND LTRIM(RTRIM(F_ID)) = N'CLIENT_ID' AND SERIAL_NO = 1;

UPDATE c SET FILTER_STRUCT = NULL
FROM dbo.FIELDS_CHOOSER c
WHERE c.T_ID = N'MOU_APPLY_M' AND c.F_ID = N'PRO_NO' AND c.SERIAL_NO = 1;
UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'MANUAL'
WHERE T_ID = N'MOU_APPLY_M' AND LTRIM(RTRIM(F_ID)) = N'PRO_NO' AND SERIAL_NO = 1;

UPDATE c SET FILTER_STRUCT = NULL
FROM dbo.FIELDS_CHOOSER c
WHERE c.T_ID = N'MOU_ASSESS_M' AND c.F_ID = N'MOULD_ID' AND c.SERIAL_NO = 1;
UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'MANUAL'
WHERE T_ID = N'MOU_ASSESS_M' AND LTRIM(RTRIM(F_ID)) = N'MOULD_ID' AND SERIAL_NO = 1;

UPDATE c SET FILTER_STRUCT = NULL
FROM dbo.FIELDS_CHOOSER c
WHERE c.T_ID = N'MOU_BATCHIN_D' AND c.F_ID = N'CLIENT_ID' AND c.SERIAL_NO = 1;
UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'MANUAL'
WHERE T_ID = N'MOU_BATCHIN_D' AND LTRIM(RTRIM(F_ID)) = N'CLIENT_ID' AND SERIAL_NO = 1;

UPDATE c SET FILTER_STRUCT = NULL
FROM dbo.FIELDS_CHOOSER c
WHERE c.T_ID = N'MOU_MOULD' AND c.F_ID = N'LINE_ID' AND c.SERIAL_NO = 2;
UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'MANUAL'
WHERE T_ID = N'MOU_MOULD' AND LTRIM(RTRIM(F_ID)) = N'LINE_ID' AND SERIAL_NO = 2;

UPDATE c SET FILTER_STRUCT = NULL
FROM dbo.FIELDS_CHOOSER c
WHERE c.T_ID = N'MOU_SCRAP_D' AND c.F_ID = N'CLIENT_ID' AND c.SERIAL_NO = 1;
UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'MANUAL'
WHERE T_ID = N'MOU_SCRAP_D' AND LTRIM(RTRIM(F_ID)) = N'CLIENT_ID' AND SERIAL_NO = 1;

UPDATE c SET FILTER_STRUCT = NULL
FROM dbo.FIELDS_CHOOSER c
WHERE c.T_ID = N'PAP_BARCODE_M' AND c.F_ID = N'BARCODE_TYPE' AND c.SERIAL_NO = 1;
UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'MANUAL'
WHERE T_ID = N'PAP_BARCODE_M' AND LTRIM(RTRIM(F_ID)) = N'BARCODE_TYPE' AND SERIAL_NO = 1;

UPDATE c SET FILTER_STRUCT = NULL
FROM dbo.FIELDS_CHOOSER c
WHERE c.T_ID = N'PAP_BARCODE_M' AND c.F_ID = N'SEND_TYPE' AND c.SERIAL_NO = 1;
UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'MANUAL'
WHERE T_ID = N'PAP_BARCODE_M' AND LTRIM(RTRIM(F_ID)) = N'SEND_TYPE' AND SERIAL_NO = 1;

UPDATE c SET FILTER_STRUCT = NULL
FROM dbo.FIELDS_CHOOSER c
WHERE c.T_ID = N'PUR_PAY_OTHER' AND c.F_ID = N'SUPPLIER_ID' AND c.SERIAL_NO = 1;
UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'MANUAL'
WHERE T_ID = N'PUR_PAY_OTHER' AND LTRIM(RTRIM(F_ID)) = N'SUPPLIER_ID' AND SERIAL_NO = 1;

UPDATE c SET FILTER_STRUCT = NULL
FROM dbo.FIELDS_CHOOSER c
WHERE c.T_ID = N'QC_EXCEPTION_M' AND c.F_ID = N'SUPPLIER_ID' AND c.SERIAL_NO = 1;
UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'MANUAL'
WHERE T_ID = N'QC_EXCEPTION_M' AND LTRIM(RTRIM(F_ID)) = N'SUPPLIER_ID' AND SERIAL_NO = 1;

UPDATE c SET FILTER_STRUCT = NULL
FROM dbo.FIELDS_CHOOSER c
WHERE c.T_ID = N'SAM_APPLY_M' AND c.F_ID = N'CLIENT_ID' AND c.SERIAL_NO = 1;
UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'MANUAL'
WHERE T_ID = N'SAM_APPLY_M' AND LTRIM(RTRIM(F_ID)) = N'CLIENT_ID' AND SERIAL_NO = 1;

UPDATE c SET FILTER_STRUCT = NULL
FROM dbo.FIELDS_CHOOSER c
WHERE c.T_ID = N'SAM_IN_M' AND c.F_ID = N'CLIENT_ID' AND c.SERIAL_NO = 1;
UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'MANUAL'
WHERE T_ID = N'SAM_IN_M' AND LTRIM(RTRIM(F_ID)) = N'CLIENT_ID' AND SERIAL_NO = 1;

UPDATE c SET FILTER_STRUCT = NULL
FROM dbo.FIELDS_CHOOSER c
WHERE c.T_ID = N'SAM_OUT_M' AND c.F_ID = N'CLIENT_ID' AND c.SERIAL_NO = 1;
UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'MANUAL'
WHERE T_ID = N'SAM_OUT_M' AND LTRIM(RTRIM(F_ID)) = N'CLIENT_ID' AND SERIAL_NO = 1;

UPDATE c SET FILTER_STRUCT = NULL
FROM dbo.FIELDS_CHOOSER c
WHERE c.T_ID = N'SFC_PLAN_D' AND c.F_ID = N'CLIENT_ID' AND c.SERIAL_NO = 1;
UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'MANUAL'
WHERE T_ID = N'SFC_PLAN_D' AND LTRIM(RTRIM(F_ID)) = N'CLIENT_ID' AND SERIAL_NO = 1;

UPDATE c SET FILTER_STRUCT = NULL
FROM dbo.FIELDS_CHOOSER c
WHERE c.T_ID = N'SFC_PLAN_M' AND c.F_ID = N'PLAN_TYPE' AND c.SERIAL_NO = 1;
UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'MANUAL'
WHERE T_ID = N'SFC_PLAN_M' AND LTRIM(RTRIM(F_ID)) = N'PLAN_TYPE' AND SERIAL_NO = 1;

UPDATE c SET FILTER_STRUCT = NULL
FROM dbo.FIELDS_CHOOSER c
WHERE c.T_ID = N'SFC_PLAN_PROCESS_M' AND c.F_ID = N'PLAN_PROCESS_TYPE' AND c.SERIAL_NO = 1;
UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'MANUAL'
WHERE T_ID = N'SFC_PLAN_PROCESS_M' AND LTRIM(RTRIM(F_ID)) = N'PLAN_PROCESS_TYPE' AND SERIAL_NO = 1;

UPDATE c SET FILTER_STRUCT = NULL
FROM dbo.FIELDS_CHOOSER c
WHERE c.T_ID = N'SUPPLIER_LINKMAN' AND c.F_ID = N'SUPPLIER_ID' AND c.SERIAL_NO = 1;
UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'MANUAL'
WHERE T_ID = N'SUPPLIER_LINKMAN' AND LTRIM(RTRIM(F_ID)) = N'SUPPLIER_ID' AND SERIAL_NO = 1;

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG SET STATUS = N'MANUAL' WHERE STATUS IN (N'PENDING_P3', N'DRIFT');

DECLARE @STRUCT_COUNT INT = (SELECT COUNT(*) FROM dbo.FIELDS_CHOOSER WHERE FILTER_STRUCT IS NOT NULL);
DECLARE @MANUAL_COUNT INT = (SELECT COUNT(*) FROM dbo.CHOOSER_FILTER_MIGRATION_LOG WHERE STATUS = N'MANUAL');
PRINT N'[ADR-008/P3] 回填完成：' + CAST(@STRUCT_COUNT AS NVARCHAR(10)) + N' 行已结构化，' + CAST(@MANUAL_COUNT AS NVARCHAR(10)) + N' 行待人工。';
