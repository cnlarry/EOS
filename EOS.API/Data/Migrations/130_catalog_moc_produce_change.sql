-- ============================================================================
-- EOS.ERP migration 130: 1509 制令变更单 —— "原单已批核 + 变更量不小于已发生" 入目录（批 4：退役 `moc-produce-change` 族）
-- ----------------------------------------------------------------------------
-- 退役对象：`DomainRuleMap[1509] = "moc-produce-change"` → `MocDomainRules.MocProduceChangeAfterSaveAsync`，三段判据：
--   ⒜ 原单未批核：`EXISTS(SELECT 1 FROM MOC_PRODUCE_M m JOIN MOC_PRODUCE_CHANGE_M c
--                          ON c.PRODUCE_TYPE=m.PRODUCE_TYPE AND c.PRODUCE_NO=m.PRODUCE_NO
--                        WHERE c.CHANGE_PRODUCE_TYPE=@T AND c.CHANGE_PRODUCE_NO=@N AND m.CONFIRM_TAG=0)`
--        ⇒ 文案 N'生产单未批核，不可变更'（无逐行诊断）。
--   ⒝ 变更后主表数量不得小于已生产：`ISNULL(m.FINISHED_QTY,0) > ISNULL(c.QTY,0)`（备品同形）
--        ⇒ 文案 N'变更后以下序号项数量小于已生产数量'（无逐行诊断）。
--   ⒞ 变更后明细应领料不得小于制令已领料：`oc.NEED_QTY < ISNULL(od.USED_QTY,0)`（od 按 (制令单别,制令单号,制令序号) 关联）
--        ⇒ 文案 N'变更后以下序号项应领料数量小于制令已领料\r\n' + 本单明细序号（每行前缀四空格、行间 CRLF）。
-- 目录承接（两条规则）：
--   SEQ=1 `reference-exists`——用**被引用行条件**（`refCondition`）表达"原单未批核"：
--     `EXISTS(SELECT 1 FROM MOC_PRODUCE_M R WHERE R.PRODUCE_TYPE=M.PRODUCE_TYPE AND R.PRODUCE_NO=M.PRODUCE_NO
--              AND R.CONFIRM_TAG=0)`。
--   SEQ=2 `qty-not-exceed`（mode=**not-below-usage**，即 `本单量 < 已发生量` 即违规；本轮新增的第三种比较形态）
--     三条 check：两条主表级（`thisQty` 取本单主表 QTY/SPARE_QTY、`usage` 取制令单 FINISHED_QTY/FINISHED_SPARE_QTY，
--     无诊断）与一条明细级（`targetTable=MOC_PRODUCE_D`、match 按明细行定位、`thisQty` 取本行 NEED_QTY、
--     `usage` 取 TARGET.USED_QTY、诊断取本单明细序号）。
--     说明：明细级文案的"每行前缀四空格"在目录形态下只能写成"文案内 `{ROWS}` 前四空格"（首行有前缀、后续行没有），
--     差异仅限不可见空白；`maxRows` 取上限 100（旧实现不限行数）。
-- 说明：1509 的 `ERROR_NO_SAVE=1` 但该判据本身无门控（旧 C# 不读开关），故不配 `switch`。
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

/* ---------- SEQ=1：原单已批核 ---------- */
DECLARE @RefMessage NVARCHAR(200) = N'生产单未批核，不可变更';
DECLARE @RefParams NVARCHAR(MAX) =
    N'{"checks":[{"refTable":"MOC_PRODUCE_M",'
    + N'"join":[{"target":"PRODUCE_TYPE","source":{"scope":"MASTER","field":"PRODUCE_TYPE"}},'
    + N'{"target":"PRODUCE_NO","source":{"scope":"MASTER","field":"PRODUCE_NO"}}],'
    + N'"refCondition":{"logic":"AND","items":[{"type":"value-eq","field":{"scope":"TARGET","field":"CONFIRM_TAG"},"value":0}]},'
    + N'"message":"生产单未批核，不可变更"}]}';

/* ---------- SEQ=2：变更量不小于已发生（三态比较：this < usage） ---------- */
DECLARE @QtyParams NVARCHAR(MAX) =
    N'{"mode":"not-below-usage","checks":['
    + N'{"targetTable":"MOC_PRODUCE_M",'
    + N'"match":[{"target":"PRODUCE_TYPE","source":{"scope":"MASTER","field":"PRODUCE_TYPE"}},'
    + N'{"target":"PRODUCE_NO","source":{"scope":"MASTER","field":"PRODUCE_NO"}}],'
    + N'"thisQty":{"scope":"MASTER","terms":[{"field":"QTY","coef":1}]},'
    + N'"usage":{"scope":"TARGET","fields":["FINISHED_QTY"]},'
    + N'"message":"变更后以下序号项数量小于已生产数量"},'
    + N'{"targetTable":"MOC_PRODUCE_M",'
    + N'"match":[{"target":"PRODUCE_TYPE","source":{"scope":"MASTER","field":"PRODUCE_TYPE"}},'
    + N'{"target":"PRODUCE_NO","source":{"scope":"MASTER","field":"PRODUCE_NO"}}],'
    + N'"thisQty":{"scope":"MASTER","terms":[{"field":"SPARE_QTY","coef":1}]},'
    + N'"usage":{"scope":"TARGET","fields":["FINISHED_SPARE_QTY"]},'
    + N'"message":"变更后以下序号项数量小于已生产数量"},'
    + N'{"targetTable":"MOC_PRODUCE_D",'
    + N'"match":[{"target":"PRODUCE_TYPE","source":{"scope":"DETAIL","field":"PRODUCE_TYPE"}},'
    + N'{"target":"PRODUCE_NO","source":{"scope":"DETAIL","field":"PRODUCE_NO"}},'
    + N'{"target":"SERIAL_NO","source":{"scope":"DETAIL","field":"PRODUCE_SERIAL_NO"}}],'
    + N'"thisQty":{"scope":"DETAIL","terms":[{"field":"NEED_QTY","coef":1}]},'
    + N'"usage":{"scope":"TARGET","fields":["USED_QTY"]},'
    + N'"diagnosticFields":[{"scope":"SOURCE","field":"SERIAL_NO"}],"maxRows":100,'
    + N'"message":"变更后以下序号项应领料数量小于制令已领料\r\n    {ROWS}"}]}';

IF EXISTS (SELECT 1 FROM dbo.MODULE_VALIDATION_RULE
           WHERE MODULE_ID = 1509 AND STAGE = N'SAVE'
             AND VALIDATION_KEY IN (N'reference-exists', N'qty-not-exceed'))
BEGIN
    IF NOT EXISTS (SELECT 1 FROM dbo.MODULE_VALIDATION_RULE
                   WHERE MODULE_ID = 1509 AND STAGE = N'SAVE' AND VALIDATION_KEY = N'reference-exists'
                     AND PARAM_STRUCT = @RefParams)
       OR NOT EXISTS (SELECT 1 FROM dbo.MODULE_VALIDATION_RULE
                      WHERE MODULE_ID = 1509 AND STAGE = N'SAVE' AND VALIDATION_KEY = N'qty-not-exceed'
                        AND PARAM_STRUCT = @QtyParams)
       OR NOT EXISTS (SELECT 1 FROM dbo.MODULE_VALIDATION_RULE
                      WHERE MODULE_ID = 1509 AND STAGE = N'SAVE' AND VALIDATION_KEY = N'qty-not-exceed'
                        AND ISNULL(MESSAGE, N'') = N'')
        THROW 50001, N'1509 的 SAVE 期规则已存在但与预期参数不一致（漂移），迁移中止。', 1;
    PRINT N'1509 变更校验已存在且参数一致，跳过。';
END
ELSE
BEGIN
    INSERT INTO dbo.MODULE_VALIDATION_RULE
        (MODULE_ID, STAGE, SEQ, VALIDATION_KEY, ENABLED, PARAM_STRUCT, MESSAGE, REMARK, SOURCE_REF, CREATE_PERSON, CREATE_DATE, LAST_UPDATE_BY, LAST_UPDATE_DATE)
    VALUES
        (1509, N'SAVE', 1, N'reference-exists', 1, @RefParams, @RefMessage,
         N'制令变更：原单必须已批核（被引用行条件）', N'P_MOC_PRODUCE_CHANGE', N'DbUp', SYSDATETIME(), N'DbUp', SYSDATETIME()),
        (1509, N'SAVE', 2, N'qty-not-exceed', 1, @QtyParams, NULL,
         N'制令变更：变更量不得小于已生产/已领料（this < usage）', N'P_MOC_PRODUCE_CHANGE', N'DbUp', SYSDATETIME(), N'DbUp', SYSDATETIME());
    PRINT N'1509 SAVE 期变更校验已播种（reference-exists + refCondition；qty-not-exceed mode=not-below-usage）。';
END

IF EXISTS (
    SELECT 1 FROM (VALUES (N'reference-exists'), (N'qty-not-exceed')) K(KEY_NAME)
    WHERE NOT EXISTS (SELECT 1 FROM dbo.MODULE_VALIDATION_RULE R
                      WHERE R.MODULE_ID = 1509 AND R.STAGE = N'SAVE' AND R.ENABLED = 1
                        AND R.VALIDATION_KEY = K.KEY_NAME))
    THROW 50002, N'1509 缺少启用的变更校验规则，迁移中止。', 1;
IF NOT EXISTS (SELECT 1 FROM dbo.MODULE_VALIDATION_RULE
               WHERE MODULE_ID = 1509 AND STAGE = N'SAVE' AND VALIDATION_KEY = N'qty-not-exceed'
                 AND PARAM_STRUCT LIKE N'%"mode":"not-below-usage"%')
    THROW 50003, N'1509 数量校验未使用 not-below-usage 形态，迁移中止。', 1;
