-- ============================================================================
-- EOS.ERP migration 118: field-copy 三例下沉为公式配置（方案 A 第一步）
-- ----------------------------------------------------------------------------
-- 语义对照（`FieldCopyHandler.BuildCopyUpdate`）：
--   UPDATE T SET T.<field> = M.<field> FROM dbo.<target> T
--     JOIN dbo.<master> M ON T.<refTarget> = M.<refSource> … WHERE <主键过滤>
-- 即"源恒为主表列、目标行由 refs 定位"——正对应公式行的
--   OP_CODE=ASSIGN + SOURCE_SCOPE=MASTER + SOURCE_FIELD + MATCH_STRUCT(refs)。
-- 三例的反向结构均为 `{"kind":"none"}`（解批不回退，与旧一致），故无需反向公式行。
-- targets[].refs 的元素 `{target, source}`：`source` 为主表列名 → MATCH 的
--   `{target, source:{scope:"MASTER", field:<source>}}`；
-- targets 中表名以 `_M` 结尾者额外应用顶层 `headerFields`（与处理器一致）。
-- 落地后动作键由 `field-copy` 变为 `field-accumulate`（公式解释器），PARAM_STRUCT 置空，
-- 既有占位公式行清掉后写入真实 ASSIGN 行。幂等：按动作重写其公式行。
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

DECLARE @Actions TABLE (MODULE_ID INT, SEQ INT, ACTION_ID BIGINT);
INSERT INTO @Actions (MODULE_ID, SEQ, ACTION_ID)
SELECT A.MODULE_ID, A.SEQ, A.ACTION_ID FROM dbo.MODULE_BUSINESS_ACTION A
WHERE A.EFFECT_KEY = N'field-copy' AND ((A.MODULE_ID = 1418 AND A.SEQ = 3) OR (A.MODULE_ID = 1502 AND A.SEQ IN (5, 6)));

IF (SELECT COUNT(*) FROM @Actions) <> 3
    THROW 50001, N'待下沉的 field-copy 动作不是预期的 3 条，迁移中止（先核对配置）。', 1;

/* 清掉服务型占位公式行 */
DELETE O FROM dbo.MODULE_BUSINESS_ACTION_OP O JOIN @Actions A ON A.ACTION_ID = O.ACTION_ID;

/* 1418 SEQ3：变更单把本单字段回写到原订单及其下游单据（源恒为主表列） */
DECLARE @A1418 BIGINT = (SELECT ACTION_ID FROM @Actions WHERE MODULE_ID = 1418 AND SEQ = 3);
DECLARE @M1418 NVARCHAR(MAX) =
    N'[{"target":"ORDER_TYPE","source":{"scope":"MASTER","field":"ORDER_TYPE"}},'
    + N'{"target":"ORDER_NO","source":{"scope":"MASTER","field":"ORDER_NO"}}]';

INSERT INTO dbo.MODULE_BUSINESS_ACTION_OP
    (ACTION_ID, OP_SEQ, TARGET_TABLE, TARGET_FIELD, OP_CODE, SOURCE_SCOPE, SOURCE_FIELD, MATCH_STRUCT, REMARK)
VALUES
    (@A1418, 1, N'COP_ORDER_M',            N'CLIENT_ORDER_NO',   N'ASSIGN', N'MASTER', N'CLIENT_ORDER_NO',   @M1418, N'原订单主表：客户单号'),
    (@A1418, 2, N'COP_ORDER_M',            N'PAYMENT_CONDITION', N'ASSIGN', N'MASTER', N'PAYMENT_CONDITION', @M1418, N'原订单主表：付款条件'),
    (@A1418, 3, N'COP_ORDER_M',            N'PRICE_CONDITION',   N'ASSIGN', N'MASTER', N'PRICE_CONDITION',   @M1418, N'原订单主表：价格条件'),
    (@A1418, 4, N'COP_ORDER_M',            N'SEND_ADDRESS',      N'ASSIGN', N'MASTER', N'SEND_ADDRESS',      @M1418, N'原订单主表：送货地址'),
    (@A1418, 5, N'COP_ORDER_D',            N'CLIENT_ORDER_NO',   N'ASSIGN', N'MASTER', N'CLIENT_ORDER_NO',   @M1418, N'原订单明细'),
    (@A1418, 6, N'PUR_APPLY_D',            N'CLIENT_ORDER_NO',   N'ASSIGN', N'MASTER', N'CLIENT_ORDER_NO',   @M1418, N'请购明细'),
    (@A1418, 7, N'PUR_PURCHASE_D',         N'CLIENT_ORDER_NO',   N'ASSIGN', N'MASTER', N'CLIENT_ORDER_NO',   @M1418, N'采购明细'),
    (@A1418, 8, N'PUR_PURCHASE_CHANGE_D',  N'CLIENT_ORDER_NO',   N'ASSIGN', N'MASTER', N'CLIENT_ORDER_NO',   @M1418, N'采购变更明细');

/* 1502 SEQ5：制令单把制令号回写到来源订单明细 */
DECLARE @A1502_5 BIGINT = (SELECT ACTION_ID FROM @Actions WHERE MODULE_ID = 1502 AND SEQ = 5);
DECLARE @M1502_5 NVARCHAR(MAX) =
    N'[{"target":"ORDER_TYPE","source":{"scope":"MASTER","field":"ORDER_TYPE"}},'
    + N'{"target":"ORDER_NO","source":{"scope":"MASTER","field":"ORDER_NO"}},'
    + N'{"target":"SERIAL_NO","source":{"scope":"MASTER","field":"ORDER_SERIAL_NO"}}]';

INSERT INTO dbo.MODULE_BUSINESS_ACTION_OP
    (ACTION_ID, OP_SEQ, TARGET_TABLE, TARGET_FIELD, OP_CODE, SOURCE_SCOPE, SOURCE_FIELD, MATCH_STRUCT, REMARK)
VALUES
    (@A1502_5, 1, N'COP_ORDER_D', N'PRODUCE_TYPE', N'ASSIGN', N'MASTER', N'PRODUCE_TYPE', @M1502_5, N'来源订单明细：制令单别'),
    (@A1502_5, 2, N'COP_ORDER_D', N'PRODUCE_NO',   N'ASSIGN', N'MASTER', N'PRODUCE_NO',   @M1502_5, N'来源订单明细：制令单号');

/* 1502 SEQ6：制令单把制令号回写到生产计划-制令对照表 */
DECLARE @A1502_6 BIGINT = (SELECT ACTION_ID FROM @Actions WHERE MODULE_ID = 1502 AND SEQ = 6);
DECLARE @M1502_6 NVARCHAR(MAX) =
    N'[{"target":"PLAN_TYPE","source":{"scope":"MASTER","field":"PLAN_TYPE"}},'
    + N'{"target":"PLAN_NO","source":{"scope":"MASTER","field":"PLAN_NO"}},'
    + N'{"target":"SERIAL_NO","source":{"scope":"MASTER","field":"PLAN_SERIAL_NO"}}]';

INSERT INTO dbo.MODULE_BUSINESS_ACTION_OP
    (ACTION_ID, OP_SEQ, TARGET_TABLE, TARGET_FIELD, OP_CODE, SOURCE_SCOPE, SOURCE_FIELD, MATCH_STRUCT, REMARK)
VALUES
    (@A1502_6, 1, N'MOC_PLAN_MOC', N'PRODUCE_TYPE', N'ASSIGN', N'MASTER', N'PRODUCE_TYPE', @M1502_6, N'计划制令对照：制令单别'),
    (@A1502_6, 2, N'MOC_PLAN_MOC', N'PRODUCE_NO',   N'ASSIGN', N'MASTER', N'PRODUCE_NO',   @M1502_6, N'计划制令对照：制令单号');

/* 动作改为公式键（服务参数不再需要） */
UPDATE A
   SET A.EFFECT_KEY = N'field-accumulate',
       A.PARAM_STRUCT = NULL,
       A.EFFECT_NAME = N'字段回写（公式）',
       A.LAST_UPDATE_BY = N'DbUp',
       A.LAST_UPDATE_DATE = SYSDATETIME()
FROM dbo.MODULE_BUSINESS_ACTION A JOIN @Actions X ON X.ACTION_ID = A.ACTION_ID;

/* 守卫：三条动作必须都是公式键且各有真实公式行（无空算子行） */
IF EXISTS (
    SELECT 1 FROM @Actions X
    WHERE NOT EXISTS (SELECT 1 FROM dbo.MODULE_BUSINESS_ACTION A
                      WHERE A.ACTION_ID = X.ACTION_ID AND A.EFFECT_KEY = N'field-accumulate')
       OR NOT EXISTS (SELECT 1 FROM dbo.MODULE_BUSINESS_ACTION_OP O
                      WHERE O.ACTION_ID = X.ACTION_ID AND LTRIM(RTRIM(ISNULL(O.OP_CODE,''))) <> '')
)
    THROW 50002, N'下沉后仍有动作不是公式键或缺少真实公式行，迁移中止。', 1;

PRINT N'== field-copy 三例下沉完成（1418/SEQ3 → 8 行；1502/SEQ5 → 2 行；1502/SEQ6 → 2 行）==';
