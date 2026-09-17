-- ============================================================================
-- EOS.ERP migration 123: link-stamp 明细取值五例下沉为公式配置（方案 A 第三步·下）
-- ----------------------------------------------------------------------------
-- 这五条的取值来自**明细行**（`fromDetail:true` + `fields[].source` 指向明细列），
-- 与上一步（迁移 121，取值来自主表）的区别只有取值来源，故下沉方式为：
--   OP_CODE=ASSIGN + SOURCE_SCOPE=TABLE + SOURCE_TABLE=<本模块明细表> + SOURCE_AGG=PICK
--   MATCH_STRUCT 的定位键取 scope=TABLE（内联 table 与公式行 SOURCE_TABLE 同为该明细表）
-- 语义对照（`LinkStampHandler.BuildUpdate` 的 fromDetail 形态）：
--   UPDATE T SET T.<f> = D.<source> FROM <目标> T
--     JOIN <明细> D ON T.<refTarget>=D.<sourceRef> … JOIN <主表> M ON D.<主键>=M.<主键>
--     WHERE M.<主键过滤>
--   ⇒ 公式路径：
--     · 取值：`(SELECT MAX(D.<source>) FROM <明细> D WHERE <定位相关> AND EXISTS(D 属本单))`
--       —— `SOURCE_AGG=PICK` 表示"按定位键的唯一相关行原值取值"，**不做数值默认**
--       （否则可空字符/日期列会被 `ISNULL(col,0)` 写成 '0'/1900-01-01，与旧处理器写 NULL 不符）；
--     · 目标行：`WHERE EXISTS(SELECT 1 FROM <明细> D JOIN <主表> M ON D.<主键>=M.<主键>
--                 WHERE M.<主键>=@k AND <定位相关>)` —— **TABLE 域的定位键自带本单范围**，
--       与旧处理器的 JOIN+主键过滤逐字对齐（不再有 DETAIL 域"未绑定本单"的越界风险）。
-- 反向保持原 kind：1404/1606/1615/1616 为 `clear-refs`（含 SERIAL 列归 0），1418 为 `none`。
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

DECLARE @Actions TABLE (MODULE_ID INT, SEQ INT, ACTION_ID BIGINT, REVERSE_KIND NVARCHAR(40));
INSERT INTO @Actions (MODULE_ID, SEQ, ACTION_ID, REVERSE_KIND)
SELECT A.MODULE_ID, A.SEQ, A.ACTION_ID, ISNULL(JSON_VALUE(A.REVERSE_STRUCT, N'$.kind'), N'')
FROM dbo.MODULE_BUSINESS_ACTION A
WHERE A.EFFECT_KEY = N'link-stamp'
  AND ((A.MODULE_ID = 1404 AND A.SEQ = 3)
    OR (A.MODULE_ID = 1418 AND A.SEQ = 4)
    OR (A.MODULE_ID = 1606 AND A.SEQ = 8)
    OR (A.MODULE_ID IN (1615, 1616) AND A.SEQ = 3));

IF (SELECT COUNT(*) FROM @Actions) <> 5
    THROW 50001, N'待下沉的明细取值 link-stamp 动作不是预期的 5 条，迁移中止（先核对配置）。', 1;

DELETE O FROM dbo.MODULE_BUSINESS_ACTION_OP O JOIN @Actions A ON A.ACTION_ID = O.ACTION_ID;

/* 定位键（TABLE 域：内联 table 与公式行 SOURCE_TABLE 一致，均为本模块明细表） */
DECLARE @MatchChaffer NVARCHAR(MAX) =
    N'[{"target":"CHAFFER_TYPE","source":{"scope":"TABLE","table":"COP_QUOTE_D","field":"CHAFFER_TYPE"}},'
    + N'{"target":"CHAFFER_NO","source":{"scope":"TABLE","table":"COP_QUOTE_D","field":"CHAFFER_NO"}},'
    + N'{"target":"SERIAL_NO","source":{"scope":"TABLE","table":"COP_QUOTE_D","field":"CHAFFER_SERIAL_NO"}}]';
DECLARE @MatchOrderChange NVARCHAR(MAX) =
    N'[{"target":"ORDER_TYPE","source":{"scope":"TABLE","table":"COP_ORDER_CHANGE_D","field":"ORDER_TYPE"}},'
    + N'{"target":"ORDER_NO","source":{"scope":"TABLE","table":"COP_ORDER_CHANGE_D","field":"ORDER_NO"}},'
    + N'{"target":"SERIAL_NO","source":{"scope":"TABLE","table":"COP_ORDER_CHANGE_D","field":"ORDER_SERIAL_NO"}}]';
DECLARE @MatchPurchase NVARCHAR(MAX) =
    N'[{"target":"APPLY_TYPE","source":{"scope":"TABLE","table":"PUR_PURCHASE_D","field":"APPLY_TYPE"}},'
    + N'{"target":"APPLY_NO","source":{"scope":"TABLE","table":"PUR_PURCHASE_D","field":"APPLY_NO"}},'
    + N'{"target":"SERIAL_NO","source":{"scope":"TABLE","table":"PUR_PURCHASE_D","field":"APPLY_SERIAL_NO"}}]';
DECLARE @MatchApply NVARCHAR(MAX) =
    N'[{"target":"ORDER_TYPE","source":{"scope":"TABLE","table":"PUR_APPLY_D","field":"ORDER_TYPE"}},'
    + N'{"target":"ORDER_NO","source":{"scope":"TABLE","table":"PUR_APPLY_D","field":"ORDER_NO"}},'
    + N'{"target":"SERIAL_NO","source":{"scope":"TABLE","table":"PUR_APPLY_D","field":"ORDER_SERIAL_NO"}}]';

/* 1404 报价单：报价类别/单号/序号回写到询价明细（值取本单明细行） */
DECLARE @A1404 BIGINT = (SELECT ACTION_ID FROM @Actions WHERE MODULE_ID = 1404);
INSERT INTO dbo.MODULE_BUSINESS_ACTION_OP
    (ACTION_ID, OP_SEQ, TARGET_TABLE, TARGET_FIELD, OP_CODE, SOURCE_SCOPE, SOURCE_TABLE, SOURCE_FIELD, SOURCE_AGG, MATCH_STRUCT, REMARK)
VALUES
    (@A1404, 1, N'COP_CHAFFER_D', N'QUOTE_TYPE',      N'ASSIGN', N'TABLE', N'COP_QUOTE_D', N'QUOTE_TYPE', N'PICK', @MatchChaffer, N'询价明细：报价单别'),
    (@A1404, 2, N'COP_CHAFFER_D', N'QUOTE_NO',        N'ASSIGN', N'TABLE', N'COP_QUOTE_D', N'QUOTE_NO',   N'PICK', @MatchChaffer, N'询价明细：报价单号'),
    (@A1404, 3, N'COP_CHAFFER_D', N'QUOTE_SERIAL_NO', N'ASSIGN', N'TABLE', N'COP_QUOTE_D', N'SERIAL_NO',  N'PICK', @MatchChaffer, N'询价明细：报价序号');

/* 1418 客户订单变更：客户品号回写到被引用订单行（取值来自变更明细行） */
DECLARE @A1418 BIGINT = (SELECT ACTION_ID FROM @Actions WHERE MODULE_ID = 1418);
INSERT INTO dbo.MODULE_BUSINESS_ACTION_OP
    (ACTION_ID, OP_SEQ, TARGET_TABLE, TARGET_FIELD, OP_CODE, SOURCE_SCOPE, SOURCE_TABLE, SOURCE_FIELD, SOURCE_AGG, MATCH_STRUCT, REMARK)
VALUES
    (@A1418, 1, N'COP_ORDER_D', N'CLIENT_PRO_NO', N'ASSIGN', N'TABLE', N'COP_ORDER_CHANGE_D', N'CLIENT_PRO_NO', N'PICK', @MatchOrderChange, N'订单行：客户品号');

/* 1606 采购单：采购类别/单号/序号回写到请购明细（值取本单明细行） */
DECLARE @A1606 BIGINT = (SELECT ACTION_ID FROM @Actions WHERE MODULE_ID = 1606);
INSERT INTO dbo.MODULE_BUSINESS_ACTION_OP
    (ACTION_ID, OP_SEQ, TARGET_TABLE, TARGET_FIELD, OP_CODE, SOURCE_SCOPE, SOURCE_TABLE, SOURCE_FIELD, SOURCE_AGG, MATCH_STRUCT, REMARK)
VALUES
    (@A1606, 1, N'PUR_APPLY_D', N'PURCHASE_TYPE',      N'ASSIGN', N'TABLE', N'PUR_PURCHASE_D', N'PURCHASE_TYPE', N'PICK', @MatchPurchase, N'请购明细：采购单别'),
    (@A1606, 2, N'PUR_APPLY_D', N'PURCHASE_NO',        N'ASSIGN', N'TABLE', N'PUR_PURCHASE_D', N'PURCHASE_NO',   N'PICK', @MatchPurchase, N'请购明细：采购单号'),
    (@A1606, 3, N'PUR_APPLY_D', N'PURCHASE_SERIAL_NO', N'ASSIGN', N'TABLE', N'PUR_PURCHASE_D', N'SERIAL_NO',     N'PICK', @MatchPurchase, N'请购明细：采购序号');

/* 1615 成品请购单 / 1616 备料单：请购类别/单号/序号回写到被引用订单行（值取本单明细行） */
DECLARE @A1615 BIGINT = (SELECT ACTION_ID FROM @Actions WHERE MODULE_ID = 1615);
DECLARE @A1616 BIGINT = (SELECT ACTION_ID FROM @Actions WHERE MODULE_ID = 1616);
INSERT INTO dbo.MODULE_BUSINESS_ACTION_OP
    (ACTION_ID, OP_SEQ, TARGET_TABLE, TARGET_FIELD, OP_CODE, SOURCE_SCOPE, SOURCE_TABLE, SOURCE_FIELD, SOURCE_AGG, MATCH_STRUCT, REMARK)
VALUES
    (@A1615, 1, N'COP_ORDER_D', N'APPLY_TYPE',      N'ASSIGN', N'TABLE', N'PUR_APPLY_D', N'APPLY_TYPE', N'PICK', @MatchApply, N'订单行：请购单别'),
    (@A1615, 2, N'COP_ORDER_D', N'APPLY_NO',        N'ASSIGN', N'TABLE', N'PUR_APPLY_D', N'APPLY_NO',   N'PICK', @MatchApply, N'订单行：请购单号'),
    (@A1615, 3, N'COP_ORDER_D', N'APPLY_SERIAL_NO', N'ASSIGN', N'TABLE', N'PUR_APPLY_D', N'SERIAL_NO',  N'PICK', @MatchApply, N'订单行：请购序号'),
    (@A1616, 1, N'COP_ORDER_D', N'APPLY_TYPE',      N'ASSIGN', N'TABLE', N'PUR_APPLY_D', N'APPLY_TYPE', N'PICK', @MatchApply, N'订单行：备料单别'),
    (@A1616, 2, N'COP_ORDER_D', N'APPLY_NO',        N'ASSIGN', N'TABLE', N'PUR_APPLY_D', N'APPLY_NO',   N'PICK', @MatchApply, N'订单行：备料单号'),
    (@A1616, 3, N'COP_ORDER_D', N'APPLY_SERIAL_NO', N'ASSIGN', N'TABLE', N'PUR_APPLY_D', N'SERIAL_NO',  N'PICK', @MatchApply, N'订单行：备料序号');

/* 动作改为公式键（服务参数不再需要；反向 kind 保持不变） */
UPDATE A
   SET A.EFFECT_KEY = N'field-accumulate',
       A.PARAM_STRUCT = NULL,
       A.EFFECT_NAME = A.EFFECT_NAME + N'（公式）',
       A.LAST_UPDATE_BY = N'DbUp',
       A.LAST_UPDATE_DATE = SYSDATETIME()
FROM dbo.MODULE_BUSINESS_ACTION A JOIN @Actions X ON X.ACTION_ID = A.ACTION_ID;

/* 守卫：五条动作必须是公式键、参数已清空、反向 kind 未变、每条都有真实公式行且来源域为 TABLE+PICK */
IF EXISTS (
    SELECT 1 FROM @Actions X
    WHERE NOT EXISTS (SELECT 1 FROM dbo.MODULE_BUSINESS_ACTION A
                      WHERE A.ACTION_ID = X.ACTION_ID AND A.EFFECT_KEY = N'field-accumulate'
                        AND A.PARAM_STRUCT IS NULL
                        AND ISNULL(JSON_VALUE(A.REVERSE_STRUCT, N'$.kind'), N'') = X.REVERSE_KIND)
       OR NOT EXISTS (SELECT 1 FROM dbo.MODULE_BUSINESS_ACTION_OP O
                      WHERE O.ACTION_ID = X.ACTION_ID AND O.OP_CODE = N'ASSIGN'
                        AND O.SOURCE_SCOPE = N'TABLE' AND O.SOURCE_AGG = N'PICK'
                        AND LTRIM(RTRIM(ISNULL(O.SOURCE_TABLE, N''))) <> N''
                        AND LTRIM(RTRIM(ISNULL(O.MATCH_STRUCT, N''))) <> N'')
)
    THROW 50002, N'下沉后仍有动作不是公式键、参数未清空、反向 kind 变化或缺少 TABLE+PICK 公式行，迁移中止。', 1;

PRINT N'== link-stamp 明细取值五例下沉完成（1404 → 3 行；1418 → 1 行；1606 → 3 行；1615/1616 → 各 3 行）==';
