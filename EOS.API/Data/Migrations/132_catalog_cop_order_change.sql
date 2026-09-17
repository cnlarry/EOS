-- ============================================================================
-- EOS.ERP migration 132: 1418 客户订单变更单 —— 四段判据入目录（批 4：退役 `cop-order-change` 族）
-- ----------------------------------------------------------------------------
-- 退役对象：`DomainRuleMap[1418] = "cop-order-change"` → `CopDomainRules.CopOrderChangeAfterSaveAsync`，四段判据：
--   ⒜ 原订单未批核：EXISTS(COP_ORDER_M m JOIN COP_ORDER_CHANGE_M c ON c.ORDER_TYPE=m.ORDER_TYPE AND c.ORDER_NO=m.ORDER_NO
--                        WHERE c.CHANGE_ORDER_TYPE=@T AND c.CHANGE_ORDER_NO=@N AND m.CONFIRM_TAG=0)
--        ⇒ N'订单未批核，不可变更'。
--   ⒝ 明细级"变更后数量小于已完工或已送货"：`oc.QTY < od.FINISHED_SEND_QTY OR oc.SPARE_QTY < od.FINISHED_SPARE_QTY
--        OR oc.QTY < od.FINISHED_PRODUCE_QTY OR oc.SPARE_QTY < od.FINISHED_PRODUCE_SPARE_QTY`
--        （od 由 oc 的 (ORDER_TYPE,ORDER_NO,ORDER_SERIAL_NO) ↔ (ORDER_TYPE,ORDER_NO,SERIAL_NO) 关联）
--        ⇒ N'变更后以下序号项订单数量小于已完工或已送货数量' + 本单明细序号。
--   ⒞ 明细级计划量：`oc.PLAN_QTY < od.FINISHED_PRODUCE_QTY` ⇒ N'变更后以下序号项计划生产数量小于已下生产单数量' + 序号。
--   ⒟ 客户订单号不得重复：变更单的 CLIENT_ORDER_NO 在**本单所引用的原订单之外**的其它订单中不得再出现
--        ⇒ N'客户订单号重复。'
-- 目录承接（三条 SAVE 期规则）：
--   SEQ=1 `reference-exists` + `refCondition`（被引用原单 `CONFIRM_TAG=0` 即命中）；
--   SEQ=2 `qty-not-exceed`（mode=`not-below-usage`）五条 check——⒝ 的四条（本行 QTY/SPARE_QTY 对
--         FINISHED_SEND_QTY/FINISHED_SPARE_QTY/FINISHED_PRODUCE_QTY/FINISHED_PRODUCE_SPARE_QTY）与
--         ⒞ 的一条（本行 PLAN_QTY 对 FINISHED_PRODUCE_QTY），均带本单明细序号诊断；
--   SEQ=3 `duplicate-check`（entity 模式，本轮新增 **`excludeVia`**）——候选表 COP_ORDER_M 按
--         `CLIENT_ORDER_NO = M.CLIENT_ORDER_NO` 且非空匹配，并把"被本单引用的原单"排除
--         （`excludeVia` 子查询按 excludeVia 表携带的主表主键列限定在当前单据内，再用 join 与候选行对齐）。
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

DECLARE @RefMessage NVARCHAR(200) = N'订单未批核，不可变更';
DECLARE @RefParams NVARCHAR(MAX) =
    N'{"checks":[{"refTable":"COP_ORDER_M",'
    + N'"join":[{"target":"ORDER_TYPE","source":{"scope":"MASTER","field":"ORDER_TYPE"}},'
    + N'{"target":"ORDER_NO","source":{"scope":"MASTER","field":"ORDER_NO"}}],'
    + N'"refCondition":{"logic":"AND","items":[{"type":"value-eq","field":{"scope":"TARGET","field":"CONFIRM_TAG"},"value":0}]},'
    + N'"message":"订单未批核，不可变更"}]}';

DECLARE @QtyMessage1 NVARCHAR(300) = N'变更后以下序号项订单数量小于已完工或已送货数量' + CHAR(13) + CHAR(10) + N'    {ROWS}';
DECLARE @QtyMessage2 NVARCHAR(300) = N'变更后以下序号项计划生产数量小于已下生产单数量' + CHAR(13) + CHAR(10) + N'    {ROWS}';
DECLARE @Match NVARCHAR(MAX) =
    N'"match":[{"target":"ORDER_TYPE","source":{"scope":"DETAIL","field":"ORDER_TYPE"}},'
    + N'{"target":"ORDER_NO","source":{"scope":"DETAIL","field":"ORDER_NO"}},'
    + N'{"target":"SERIAL_NO","source":{"scope":"DETAIL","field":"ORDER_SERIAL_NO"}}]';
DECLARE @Diag NVARCHAR(200) = N'"diagnosticFields":[{"scope":"SOURCE","field":"SERIAL_NO"}],"maxRows":100';
DECLARE @QtyParams NVARCHAR(MAX) =
    N'{"mode":"not-below-usage","checks":['
    + N'{"targetTable":"COP_ORDER_D",' + @Match
    + N',"thisQty":{"scope":"DETAIL","terms":[{"field":"QTY","coef":1}]},"usage":{"scope":"TARGET","fields":["FINISHED_SEND_QTY"]},'
    + @Diag + N',"message":"变更后以下序号项订单数量小于已完工或已送货数量\r\n    {ROWS}"},'
    + N'{"targetTable":"COP_ORDER_D",' + @Match
    + N',"thisQty":{"scope":"DETAIL","terms":[{"field":"SPARE_QTY","coef":1}]},"usage":{"scope":"TARGET","fields":["FINISHED_SPARE_QTY"]},'
    + @Diag + N',"message":"变更后以下序号项订单数量小于已完工或已送货数量\r\n    {ROWS}"},'
    + N'{"targetTable":"COP_ORDER_D",' + @Match
    + N',"thisQty":{"scope":"DETAIL","terms":[{"field":"QTY","coef":1}]},"usage":{"scope":"TARGET","fields":["FINISHED_PRODUCE_QTY"]},'
    + @Diag + N',"message":"变更后以下序号项订单数量小于已完工或已送货数量\r\n    {ROWS}"},'
    + N'{"targetTable":"COP_ORDER_D",' + @Match
    + N',"thisQty":{"scope":"DETAIL","terms":[{"field":"SPARE_QTY","coef":1}]},"usage":{"scope":"TARGET","fields":["FINISHED_PRODUCE_SPARE_QTY"]},'
    + @Diag + N',"message":"变更后以下序号项订单数量小于已完工或已送货数量\r\n    {ROWS}"},'
    + N'{"targetTable":"COP_ORDER_D",' + @Match
    + N',"thisQty":{"scope":"DETAIL","terms":[{"field":"PLAN_QTY","coef":1}]},"usage":{"scope":"TARGET","fields":["FINISHED_PRODUCE_QTY"]},'
    + @Diag + N',"message":"变更后以下序号项计划生产数量小于已下生产单数量\r\n    {ROWS}"}]}';

DECLARE @DupMessage NVARCHAR(200) = N'客户订单号重复。';
DECLARE @DupParams NVARCHAR(MAX) =
    N'{"mode":"entity","table":"COP_ORDER_M","keyFields":["CLIENT_ORDER_NO"],'
    + N'"keySource":{"scope":"MASTER","fields":["CLIENT_ORDER_NO"]},'
    + N'"filter":{"logic":"AND","items":[{"type":"blank","field":{"scope":"TARGET","field":"CLIENT_ORDER_NO"},"negate":true}]},'
    + N'"excludeVia":{"table":"COP_ORDER_CHANGE_M",'
    + N'"join":[{"target":"ORDER_TYPE","source":{"scope":"TARGET","field":"ORDER_TYPE"}},'
    + N'{"target":"ORDER_NO","source":{"scope":"TARGET","field":"ORDER_NO"}}]}}';

IF EXISTS (SELECT 1 FROM dbo.MODULE_VALIDATION_RULE
           WHERE MODULE_ID = 1418 AND STAGE = N'SAVE'
             AND VALIDATION_KEY IN (N'reference-exists', N'qty-not-exceed', N'duplicate-check'))
BEGIN
    IF NOT EXISTS (SELECT 1 FROM dbo.MODULE_VALIDATION_RULE
                   WHERE MODULE_ID = 1418 AND STAGE = N'SAVE' AND VALIDATION_KEY = N'reference-exists'
                     AND PARAM_STRUCT = @RefParams)
       OR NOT EXISTS (SELECT 1 FROM dbo.MODULE_VALIDATION_RULE
                      WHERE MODULE_ID = 1418 AND STAGE = N'SAVE' AND VALIDATION_KEY = N'qty-not-exceed'
                        AND PARAM_STRUCT = @QtyParams AND ISNULL(MESSAGE, N'') = N'')
       OR NOT EXISTS (SELECT 1 FROM dbo.MODULE_VALIDATION_RULE
                      WHERE MODULE_ID = 1418 AND STAGE = N'SAVE' AND VALIDATION_KEY = N'duplicate-check'
                        AND PARAM_STRUCT = @DupParams)
        THROW 50001, N'1418 的 SAVE 期规则已存在但与预期参数不一致（漂移），迁移中止。', 1;
    PRINT N'1418 变更校验已存在且参数一致，跳过。';
END
ELSE
BEGIN
    INSERT INTO dbo.MODULE_VALIDATION_RULE
        (MODULE_ID, STAGE, SEQ, VALIDATION_KEY, ENABLED, PARAM_STRUCT, MESSAGE, REMARK, SOURCE_REF, CREATE_PERSON, CREATE_DATE, LAST_UPDATE_BY, LAST_UPDATE_DATE)
    VALUES
        (1418, N'SAVE', 1, N'reference-exists', 1, @RefParams, @RefMessage,
         N'订单变更：原订单必须已批核（被引用行条件）', N'P_WF_COP_ORDER_CHANGE', N'DbUp', SYSDATETIME(), N'DbUp', SYSDATETIME()),
        (1418, N'SAVE', 2, N'qty-not-exceed', 1, @QtyParams, NULL,
         N'订单变更：变更量不得小于已完工/已送货/已下生产单（this < usage）', N'P_WF_COP_ORDER_CHANGE', N'DbUp', SYSDATETIME(), N'DbUp', SYSDATETIME()),
        (1418, N'SAVE', 3, N'duplicate-check', 1, @DupParams, @DupMessage,
         N'订单变更：客户订单号在引用原单之外不得重复（excludeVia）', N'P_WF_COP_ORDER_CHANGE', N'DbUp', SYSDATETIME(), N'DbUp', SYSDATETIME());
    PRINT N'1418 SAVE 期变更校验已播种（reference-exists + qty-not-exceed(not-below-usage) + duplicate-check(excludeVia)）。';
END

IF EXISTS (
    SELECT 1 FROM (VALUES (N'reference-exists'), (N'qty-not-exceed'), (N'duplicate-check')) K(KEY_NAME)
    WHERE NOT EXISTS (SELECT 1 FROM dbo.MODULE_VALIDATION_RULE R
                      WHERE R.MODULE_ID = 1418 AND R.STAGE = N'SAVE' AND R.ENABLED = 1
                        AND R.VALIDATION_KEY = K.KEY_NAME))
    THROW 50002, N'1418 缺少启用的变更校验规则，迁移中止。', 1;
