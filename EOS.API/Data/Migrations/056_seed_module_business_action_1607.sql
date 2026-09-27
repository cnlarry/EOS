-- ============================================================================
-- EOS.ERP migration 057: seed 1607 收料单业务动作/校验配置
-- ----------------------------------------------------------------------------
-- 把 1607 收料单的批核效果与校验规则落为配置数据行
-- （MODULE_BUSINESS_ACTION / MODULE_BUSINESS_ACTION_OP / MODULE_VALIDATION_RULE）。
-- 幂等：模块 1607 已有 APPROVE_EFFECT 动作即跳过（IF NOT EXISTS 守卫）。
-- ============================================================================

SET NOCOUNT ON;

DECLARE @GUARD_MESSAGE NVARCHAR(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, @GUARD_MESSAGE, 1;

IF NOT EXISTS (SELECT 1 FROM dbo.MODULE_BUSINESS_ACTION WHERE MODULE_ID = 1607 AND EVENT_CODE = N'APPROVE_EFFECT')
BEGIN
    DECLARE @A BIGINT;

    -- Action 1：收料量回写采购单（含实交日期）
    INSERT dbo.MODULE_BUSINESS_ACTION (MODULE_ID, EVENT_CODE, SEQ, EFFECT_KEY, EFFECT_NAME, ENABLED, FAIL_MODE, PARAM_STRUCT, REVERSE_STRUCT, REMARK, SOURCE_REF, CREATE_PERSON, CREATE_DATE)
    VALUES (1607, N'APPROVE_EFFECT', 1, N'field-accumulate', N'收料量回写采购单（含实交日期）', 1, N'BLOCK',
        NULL, N'{"kind":"auto-reverse","note":"数量 -=；日期不回退"}',
        N'批核后把收料量累加回采购行，并写实交日期', N'P_WF_PUR_RECEIVE ', N'seed-agent', SYSDATETIME());
    SET @A = SCOPE_IDENTITY();
    INSERT dbo.MODULE_BUSINESS_ACTION_OP (ACTION_ID, OP_SEQ, TARGET_TABLE, TARGET_FIELD, OP_CODE, SOURCE_SCOPE, SOURCE_FIELD, SOURCE_AGG, MATCH_STRUCT)
    VALUES
        (@A, 1, N'PUR_PURCHASE_D', N'RECEIVE_QTY', N'ACCUM', N'DETAIL', N'QTY', N'SUM',
            N'[{"target":"PURCHASE_TYPE","source":{"scope":"DETAIL","field":"PURCHASE_TYPE"}},{"target":"PURCHASE_NO","source":{"scope":"DETAIL","field":"PURCHASE_NO"}},{"target":"SERIAL_NO","source":{"scope":"DETAIL","field":"PURCHASE_SERIAL_NO"}}]'),
        (@A, 2, N'PUR_PURCHASE_D', N'RECEIVE_SPARE_QTY', N'ACCUM', N'DETAIL', N'SPARE_QTY', N'SUM',
            N'[{"target":"PURCHASE_TYPE","source":{"scope":"DETAIL","field":"PURCHASE_TYPE"}},{"target":"PURCHASE_NO","source":{"scope":"DETAIL","field":"PURCHASE_NO"}},{"target":"SERIAL_NO","source":{"scope":"DETAIL","field":"PURCHASE_SERIAL_NO"}}]'),
        (@A, 3, N'PUR_PURCHASE_D', N'REAL_DELIVERY_DATE', N'ASSIGN', N'MASTER', N'RECEIVE_DATE', NULL,
            N'[{"target":"PURCHASE_TYPE","source":{"scope":"DETAIL","field":"PURCHASE_TYPE"}},{"target":"PURCHASE_NO","source":{"scope":"DETAIL","field":"PURCHASE_NO"}},{"target":"SERIAL_NO","source":{"scope":"DETAIL","field":"PURCHASE_SERIAL_NO"}}]');

    -- Action 2：客户订单收货汇总累加
    INSERT dbo.MODULE_BUSINESS_ACTION (MODULE_ID, EVENT_CODE, SEQ, EFFECT_KEY, EFFECT_NAME, ENABLED, FAIL_MODE, PARAM_STRUCT, REVERSE_STRUCT, REMARK, SOURCE_REF, CREATE_PERSON, CREATE_DATE)
    VALUES (1607, N'APPROVE_EFFECT', 2, N'field-accumulate', N'客户订单收货汇总累加', 1, N'BLOCK',
        NULL, N'{"kind":"auto-reverse","note":"-= 减回"}',
        N'按订单+料号累加收货量', N'P_WF_PUR_RECEIVE §COP_ORDER_MORE', N'seed-agent', SYSDATETIME());
    SET @A = SCOPE_IDENTITY();
    INSERT dbo.MODULE_BUSINESS_ACTION_OP (ACTION_ID, OP_SEQ, TARGET_TABLE, TARGET_FIELD, OP_CODE, SOURCE_SCOPE, SOURCE_FIELD, SOURCE_AGG, MATCH_STRUCT)
    VALUES (@A, 1, N'COP_ORDER_MORE', N'RECEIVE_QTY', N'ACCUM', N'DETAIL', N'QTY', N'SUM',
        N'[{"target":"ORDER_TYPE","source":{"scope":"DETAIL","field":"ORDER_TYPE"}},{"target":"ORDER_NO","source":{"scope":"DETAIL","field":"ORDER_NO"}},{"target":"PRO_NO","source":{"scope":"DETAIL","field":"PRO_NO"}}]');

    -- Action 3/4：主档戳记
    INSERT dbo.MODULE_BUSINESS_ACTION (MODULE_ID, EVENT_CODE, SEQ, EFFECT_KEY, EFFECT_NAME, ENABLED, FAIL_MODE, PARAM_STRUCT, REVERSE_STRUCT, REMARK, SOURCE_REF, CREATE_PERSON, CREATE_DATE)
    VALUES (1607, N'APPROVE_EFFECT', 3, N'stamp-last-activity', N'供应商最近交易日期', 1, N'BLOCK',
        NULL, N'{"kind":"no-reverse"}', NULL, N'P_WF_PUR_RECEIVE §SUPPLIER', N'seed-agent', SYSDATETIME());
    SET @A = SCOPE_IDENTITY();
    INSERT dbo.MODULE_BUSINESS_ACTION_OP (ACTION_ID, OP_SEQ, TARGET_TABLE, TARGET_FIELD, OP_CODE, SOURCE_SCOPE, SOURCE_FIELD, MATCH_STRUCT)
    VALUES (@A, 1, N'SUPPLIER', N'LAST_TRADE_DATE', N'ASSIGN_MAX', N'MASTER', N'RECEIVE_DATE',
        N'[{"target":"SUPPLIER_ID","source":{"scope":"MASTER","field":"SUPPLIER_ID"}}]');

    INSERT dbo.MODULE_BUSINESS_ACTION (MODULE_ID, EVENT_CODE, SEQ, EFFECT_KEY, EFFECT_NAME, ENABLED, FAIL_MODE, PARAM_STRUCT, REVERSE_STRUCT, REMARK, SOURCE_REF, CREATE_PERSON, CREATE_DATE)
    VALUES (1607, N'APPROVE_EFFECT', 4, N'stamp-last-activity', N'料件最近交易日期', 1, N'BLOCK',
        NULL, N'{"kind":"no-reverse"}', NULL, N'P_WF_PUR_RECEIVE §PRODUCT', N'seed-agent', SYSDATETIME());
    SET @A = SCOPE_IDENTITY();
    INSERT dbo.MODULE_BUSINESS_ACTION_OP (ACTION_ID, OP_SEQ, TARGET_TABLE, TARGET_FIELD, OP_CODE, SOURCE_SCOPE, SOURCE_FIELD, MATCH_STRUCT)
    VALUES (@A, 1, N'PRODUCT', N'LAST_TRADE_DATE', N'ASSIGN_MAX', N'MASTER', N'RECEIVE_DATE',
        N'[{"target":"PRO_NO","source":{"scope":"DETAIL","field":"PRO_NO"}}]');

    -- Action 5：采购行/单收满自动结案
    INSERT dbo.MODULE_BUSINESS_ACTION (MODULE_ID, EVENT_CODE, SEQ, EFFECT_KEY, EFFECT_NAME, ENABLED, FAIL_MODE, PARAM_STRUCT, REVERSE_STRUCT, REMARK, SOURCE_REF, CREATE_PERSON, CREATE_DATE)
    VALUES (1607, N'APPROVE_EFFECT', 5, N'completion-close', N'采购行/单收满自动结案', 1, N'BLOCK',
        NULL, N'{"kind":"recompute","note":"解批后按当前量重算结案"}',
        NULL, N'P_PUR_PURCHASE_FINISHED', N'seed-agent', SYSDATETIME());
    SET @A = SCOPE_IDENTITY();
    INSERT dbo.MODULE_BUSINESS_ACTION_OP (ACTION_ID, OP_SEQ, TARGET_TABLE, TARGET_FIELD, OP_CODE, SOURCE_SCOPE, SOURCE_CONSTANT, CONDITION_STRUCT, MATCH_STRUCT)
    VALUES
        (@A, 1, N'PUR_PURCHASE_D', N'FINISHED_TAG', N'SET_WHEN', N'CONSTANT', N'1',
            N'{"logic":"AND","items":[{"type":"field-compare","left":{"scope":"TARGET","field":"RECEIVE_QTY"},"op":"GE","right":{"scope":"TARGET","field":"QTY"}},{"type":"field-compare","left":{"scope":"TARGET","field":"RECEIVE_SPARE_QTY"},"op":"GE","right":{"scope":"TARGET","field":"SPARE_QTY"}}]}',
            N'[{"target":"PURCHASE_TYPE","source":{"scope":"DETAIL","field":"PURCHASE_TYPE"}},{"target":"PURCHASE_NO","source":{"scope":"DETAIL","field":"PURCHASE_NO"}},{"target":"SERIAL_NO","source":{"scope":"DETAIL","field":"PURCHASE_SERIAL_NO"}}]'),
        (@A, 2, N'PUR_PURCHASE_M', N'FINISHED_TAG', N'SET_WHEN', N'CONSTANT', N'1',
            N'{"logic":"AND","items":[{"type":"not-exists","targetTable":"PUR_PURCHASE_D","match":[{"target":"PURCHASE_TYPE","source":{"field":"PURCHASE_TYPE"}},{"target":"PURCHASE_NO","source":{"field":"PURCHASE_NO"}}],"condition":{"left":{"scope":"TARGET","field":"FINISHED_TAG"},"op":"EQ","right":{"value":0}}}]}',
            N'[{"target":"PURCHASE_TYPE","source":{"scope":"DETAIL","field":"PURCHASE_TYPE"}},{"target":"PURCHASE_NO","source":{"scope":"DETAIL","field":"PURCHASE_NO"}}]'),
        (@A, 3, N'PUR_PURCHASE_M', N'FINISHED_PERSON', N'SET_WHEN', N'CONSTANT', N'SYSTEM',
            N'{"logic":"AND","items":[{"type":"not-exists","targetTable":"PUR_PURCHASE_D","match":[{"target":"PURCHASE_TYPE","source":{"field":"PURCHASE_TYPE"}},{"target":"PURCHASE_NO","source":{"field":"PURCHASE_NO"}}],"condition":{"left":{"scope":"TARGET","field":"FINISHED_TAG"},"op":"EQ","right":{"value":0}}}]}',
            N'[{"target":"PURCHASE_TYPE","source":{"scope":"DETAIL","field":"PURCHASE_TYPE"}},{"target":"PURCHASE_NO","source":{"scope":"DETAIL","field":"PURCHASE_NO"}}]'),
        (@A, 4, N'PUR_PURCHASE_M', N'FINISHED_DATE', N'SET_WHEN', N'CONSTANT', N'SYSDATETIME',
            N'{"logic":"AND","items":[{"type":"not-exists","targetTable":"PUR_PURCHASE_D","match":[{"target":"PURCHASE_TYPE","source":{"field":"PURCHASE_TYPE"}},{"target":"PURCHASE_NO","source":{"field":"PURCHASE_NO"}}],"condition":{"left":{"scope":"TARGET","field":"FINISHED_TAG"},"op":"EQ","right":{"value":0}}}]}',
            N'[{"target":"PURCHASE_TYPE","source":{"scope":"DETAIL","field":"PURCHASE_TYPE"}},{"target":"PURCHASE_NO","source":{"scope":"DETAIL","field":"PURCHASE_NO"}}]');

    -- Action 6：库存入库（服务级）
    INSERT dbo.MODULE_BUSINESS_ACTION (MODULE_ID, EVENT_CODE, SEQ, EFFECT_KEY, EFFECT_NAME, ENABLED, FAIL_MODE, PARAM_STRUCT, REVERSE_STRUCT, REMARK, SOURCE_REF, CREATE_PERSON, CREATE_DATE)
    VALUES (1607, N'APPROVE_EFFECT', 6, N'inventory-move', N'收料入库（服务级）', 1, N'BLOCK',
        N'{"direction":"IN","mrp":false,"fieldMap":{"masterDate":"RECEIVE_DATE","qty":{"terms":[{"field":"QTY","coef":1},{"field":"SPARE_QTY","coef":1}]},"detail":["SERIAL_NO","PRO_NO","DEPOT_ID","UNIT_ID","PRICE","CURR_ID","CURR_RATE","AMOUNT","BATCH_NO"]}}',
        N'{"kind":"reverse-flow","note":"解批写反向流水，不删除"}',
        NULL, N'P_UPDATE_PRO_DEPOT', N'seed-agent', SYSDATETIME());

    -- Action 7：预计量调整（在途减少）
    INSERT dbo.MODULE_BUSINESS_ACTION (MODULE_ID, EVENT_CODE, SEQ, EFFECT_KEY, EFFECT_NAME, ENABLED, FAIL_MODE, CONDITION_STRUCT, PARAM_STRUCT, REVERSE_STRUCT, REMARK, SOURCE_REF, CREATE_PERSON, CREATE_DATE)
    VALUES (1607, N'APPROVE_EFFECT', 7, N'adjust-projection', N'在途采购量减少', 1, N'BLOCK',
        N'{"logic":"AND","items":[{"type":"switch","key":"PRO_MRP","value":true}]}', NULL,
        N'{"kind":"auto-reverse","note":"+= 加回"}', NULL, N'P_WF_PUR_RECEIVE §IN_BUY_QTY', N'seed-agent', SYSDATETIME());
    SET @A = SCOPE_IDENTITY();
    INSERT dbo.MODULE_BUSINESS_ACTION_OP (ACTION_ID, OP_SEQ, TARGET_TABLE, TARGET_FIELD, OP_CODE, SOURCE_SCOPE, SOURCE_TERMS_STRUCT, MATCH_STRUCT)
    VALUES (@A, 1, N'PRODUCT', N'IN_BUY_QTY', N'DEACCUM', N'DETAIL',
        N'[{"field":"QTY","coef":1},{"field":"SPARE_QTY","coef":1}]',
        N'[{"target":"PRO_NO","source":{"scope":"DETAIL","field":"PRO_NO"}}]');

    -- 校验规则：收料不超采购（批核前）
    IF NOT EXISTS (SELECT 1 FROM dbo.MODULE_VALIDATION_RULE WHERE MODULE_ID = 1607 AND STAGE = N'APPROVE')
    BEGIN
        INSERT dbo.MODULE_VALIDATION_RULE (MODULE_ID, STAGE, SEQ, VALIDATION_KEY, ENABLED, PARAM_STRUCT, MESSAGE, REMARK, SOURCE_REF, CREATE_PERSON, CREATE_DATE)
        VALUES (1607, N'APPROVE', 1, N'qty-not-exceed', 1,
            N'{"mode":"usage-not-exceed","checks":[{"match":[{"target":"PURCHASE_TYPE","source":{"scope":"DETAIL","field":"PURCHASE_TYPE"}},{"target":"PURCHASE_NO","source":{"scope":"DETAIL","field":"PURCHASE_NO"}},{"target":"SERIAL_NO","source":{"scope":"DETAIL","field":"PURCHASE_SERIAL_NO"}}],"thisQty":{"scope":"DETAIL","terms":[{"field":"QTY","coef":1}]},"usage":{"scope":"TARGET","fields":["RECEIVE_QTY"]},"limit":{"scope":"TARGET","fields":["QTY"]}},{"match":[{"target":"PURCHASE_TYPE","source":{"scope":"DETAIL","field":"PURCHASE_TYPE"}},{"target":"PURCHASE_NO","source":{"scope":"DETAIL","field":"PURCHASE_NO"}},{"target":"SERIAL_NO","source":{"scope":"DETAIL","field":"PURCHASE_SERIAL_NO"}}],"thisQty":{"scope":"DETAIL","terms":[{"field":"SPARE_QTY","coef":1}]},"usage":{"scope":"TARGET","fields":["RECEIVE_SPARE_QTY"]},"limit":{"scope":"TARGET","fields":["SPARE_QTY"]}}]}',
            N'收料数量超过采购单可收数量',
            N'批核前置校验（对应 P_PUR_RECEIVE_CHECK 移植）', N'P_PUR_RECEIVE_CHECK', N'seed-agent', SYSDATETIME());
    END
END

PRINT N'== 1607 收料单业务动作/校验配置种子完成 ==';
