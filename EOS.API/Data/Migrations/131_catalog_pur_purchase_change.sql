-- ============================================================================
-- EOS.ERP migration 131: 1609 采购变更单 —— "原单已批核 + 变更量不小于已收货" 入目录（批 4：退役 `pur-purchase-change` 族）
-- ----------------------------------------------------------------------------
-- 退役对象：`DomainRuleMap[1609] = "pur-purchase-change"` → `PurDomainRules.PurPurchaseChangeAfterSaveAsync`：
--     ⒜ EXISTS(SELECT 1 FROM PUR_PURCHASE_M m JOIN PUR_PURCHASE_CHANGE_M c
--                ON c.PURCHASE_TYPE=m.PURCHASE_TYPE AND c.PURCHASE_NO=m.PURCHASE_NO
--              WHERE c.CHANGE_PURCHASE_TYPE=@T AND c.CHANGE_PURCHASE_NO=@N AND m.CONFIRM_TAG=0)
--        ⇒ 拒绝，文案 N'采购单未批核，不可变更'。
--     ⒝ SELECT oc.SERIAL_NO FROM PUR_PURCHASE_D od
--          JOIN PUR_PURCHASE_CHANGE_D oc
--            ON oc.PURCHASE_TYPE=od.PURCHASE_TYPE AND oc.PURCHASE_NO=od.PURCHASE_NO AND oc.PURCHASE_SERIAL_NO=od.SERIAL_NO
--        WHERE oc.CHANGE_PURCHASE_TYPE=@T AND oc.CHANGE_PURCHASE_NO=@N AND oc.QTY < ISNULL(od.RECEIVE_QTY,0)
--        ⇒ 拒绝，文案 N'变更后以下序号项采购单数量小于已收货数量\r\n' + 本单明细序号（每行前缀四空格、行间 CRLF）。
-- 目录承接（两条 SAVE 期规则，形态与 1509 同构）：
--   SEQ=1 `reference-exists` + `refCondition`（被引用行 `CONFIRM_TAG=0` 即命中）；
--   SEQ=2 `qty-not-exceed`（mode=`not-below-usage`，`本单量 < 已发生量` 即违规）——
--     `targetTable=PUR_PURCHASE_D`、match 按明细行定位、`thisQty` 取本行 `QTY`、`usage` 取 `RECEIVE_QTY`、
--     诊断取本单明细序号（文案内 `{ROWS}` 前四空格；后续行无前缀，差异仅限不可见空白）。
-- 说明：1609 的 `ERROR_NO_SAVE=0`，但该判据本身无门控（旧 C# 不读开关），故不配 `switch`。
-- ============================================================================

SET NOCOUNT ON;

SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET ARITHABORT ON;

DECLARE @GUARD_MESSAGE NVARCHAR(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, @GUARD_MESSAGE, 1;

DECLARE @RefMessage NVARCHAR(200) = N'采购单未批核，不可变更';
DECLARE @RefParams NVARCHAR(MAX) =
    N'{"checks":[{"refTable":"PUR_PURCHASE_M",'
    + N'"join":[{"target":"PURCHASE_TYPE","source":{"scope":"MASTER","field":"PURCHASE_TYPE"}},'
    + N'{"target":"PURCHASE_NO","source":{"scope":"MASTER","field":"PURCHASE_NO"}}],'
    + N'"refCondition":{"logic":"AND","items":[{"type":"value-eq","field":{"scope":"TARGET","field":"CONFIRM_TAG"},"value":0}]},'
    + N'"message":"采购单未批核，不可变更"}]}';

DECLARE @QtyParams NVARCHAR(MAX) =
    N'{"mode":"not-below-usage","checks":[{"targetTable":"PUR_PURCHASE_D",'
    + N'"match":[{"target":"PURCHASE_TYPE","source":{"scope":"DETAIL","field":"PURCHASE_TYPE"}},'
    + N'{"target":"PURCHASE_NO","source":{"scope":"DETAIL","field":"PURCHASE_NO"}},'
    + N'{"target":"SERIAL_NO","source":{"scope":"DETAIL","field":"PURCHASE_SERIAL_NO"}}],'
    + N'"thisQty":{"scope":"DETAIL","terms":[{"field":"QTY","coef":1}]},'
    + N'"usage":{"scope":"TARGET","fields":["RECEIVE_QTY"]},'
    + N'"diagnosticFields":[{"scope":"SOURCE","field":"SERIAL_NO"}],"maxRows":100,'
    + N'"message":"变更后以下序号项采购单数量小于已收货数量\r\n    {ROWS}"}]}';

IF EXISTS (SELECT 1 FROM dbo.MODULE_VALIDATION_RULE
           WHERE MODULE_ID = 1609 AND STAGE = N'SAVE'
             AND VALIDATION_KEY IN (N'reference-exists', N'qty-not-exceed'))
BEGIN
    IF NOT EXISTS (SELECT 1 FROM dbo.MODULE_VALIDATION_RULE
                   WHERE MODULE_ID = 1609 AND STAGE = N'SAVE' AND VALIDATION_KEY = N'reference-exists'
                     AND PARAM_STRUCT = @RefParams)
       OR NOT EXISTS (SELECT 1 FROM dbo.MODULE_VALIDATION_RULE
                      WHERE MODULE_ID = 1609 AND STAGE = N'SAVE' AND VALIDATION_KEY = N'qty-not-exceed'
                        AND PARAM_STRUCT = @QtyParams)
       OR NOT EXISTS (SELECT 1 FROM dbo.MODULE_VALIDATION_RULE
                      WHERE MODULE_ID = 1609 AND STAGE = N'SAVE' AND VALIDATION_KEY = N'qty-not-exceed'
                        AND ISNULL(MESSAGE, N'') = N'')
        THROW 50001, N'1609 的 SAVE 期规则已存在但与预期参数不一致（漂移），迁移中止。', 1;
    PRINT N'1609 变更校验已存在且参数一致，跳过。';
END
ELSE
BEGIN
    INSERT INTO dbo.MODULE_VALIDATION_RULE
        (MODULE_ID, STAGE, SEQ, VALIDATION_KEY, ENABLED, PARAM_STRUCT, MESSAGE, REMARK, SOURCE_REF, CREATE_PERSON, CREATE_DATE, LAST_UPDATE_BY, LAST_UPDATE_DATE)
    VALUES
        (1609, N'SAVE', 1, N'reference-exists', 1, @RefParams, @RefMessage,
         N'采购变更：原单必须已批核（被引用行条件）', N'P_PUR_PURCHASE_CHANGE', N'DbUp', SYSDATETIME(), N'DbUp', SYSDATETIME()),
        (1609, N'SAVE', 2, N'qty-not-exceed', 1, @QtyParams, NULL,
         N'采购变更：变更量不得小于已收货（this < usage）', N'P_PUR_PURCHASE_CHANGE', N'DbUp', SYSDATETIME(), N'DbUp', SYSDATETIME());
    PRINT N'1609 SAVE 期变更校验已播种（reference-exists + refCondition；qty-not-exceed mode=not-below-usage）。';
END

IF EXISTS (
    SELECT 1 FROM (VALUES (N'reference-exists'), (N'qty-not-exceed')) K(KEY_NAME)
    WHERE NOT EXISTS (SELECT 1 FROM dbo.MODULE_VALIDATION_RULE R
                      WHERE R.MODULE_ID = 1609 AND R.STAGE = N'SAVE' AND R.ENABLED = 1
                        AND R.VALIDATION_KEY = K.KEY_NAME))
    THROW 50002, N'1609 缺少启用的变更校验规则，迁移中止。', 1;
