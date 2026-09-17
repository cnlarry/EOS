-- ============================================================================
-- EOS.ERP migration 099: 保存侧不超量校验入目录（B4 首批：1607 收料单）
-- ----------------------------------------------------------------------------
-- C# 判据（PurDomainRules.PurReceiveAfterSaveAsync 末段，受 MODULES.ERROR_NO_SAVE 门控，
-- 实测该模块 =1 即生效）：
--   SELECT i.PURCHASE_SERIAL_NO, o.QTY, ISNULL(o.RECEIVE_QTY,0), i.QTY
--   FROM (SELECT PURCHASE_TYPE, PURCHASE_NO, PURCHASE_SERIAL_NO, SUM(QTY) QTY
--         FROM dbo.PUR_RECEIVE_D WHERE RECEIVE_TYPE=@Type AND RECEIVE_NO=@No
--         GROUP BY PURCHASE_TYPE, PURCHASE_NO, PURCHASE_SERIAL_NO) i
--   INNER JOIN dbo.PUR_PURCHASE_D o
--     ON o.PURCHASE_TYPE=i.PURCHASE_TYPE AND o.PURCHASE_NO=i.PURCHASE_NO AND o.SERIAL_NO=i.PURCHASE_SERIAL_NO
--   WHERE i.QTY > o.QTY-ISNULL(o.RECEIVE_QTY,0)+0.1;
--   文案「以下项收料数量超出采购数量\r\n序号  采购数量  已收数量  单据数量\r\n」+ 逐行 4 列。
--
-- 目录表达：usage-not-exceed（本次量 + 已用量 > 上限 + 容差 即命中）
--   · thisQty.agg=SUM：旧实现按采购行分组求和；逐行比较会在"同单同采购行多行"时放宽约束；
--   · offset=0.1：旧实现的容差；
--   · diagnosticFields：SOURCE=采购序号、TARGET=采购量/已收量、THIS=本单合计，列间 4 空格。
--
-- 说明：批核侧同款规则（迁移 057）保持不变——stage 不同，SAVE 与 APPROVE 各司其职。
-- 幂等：按 模块+SAVE+顺序 覆盖参数与启用位。
-- ============================================================================

SET NOCOUNT ON;

DECLARE @GUARD_MESSAGE NVARCHAR(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, @GUARD_MESSAGE, 1;

PRINT N'== 播种 1607 保存侧收料不超采购 ==';

DECLARE @NLJ NVARCHAR(4) = N'\r\n';
DECLARE @PARAM NVARCHAR(MAX) =
    N'{"mode":"usage-not-exceed","checks":[{"targetTable":"PUR_PURCHASE_D",'
    + N'"match":['
    + N'{"target":"PURCHASE_TYPE","source":{"scope":"DETAIL","field":"PURCHASE_TYPE"}},'
    + N'{"target":"PURCHASE_NO","source":{"scope":"DETAIL","field":"PURCHASE_NO"}},'
    + N'{"target":"SERIAL_NO","source":{"scope":"DETAIL","field":"PURCHASE_SERIAL_NO"}}],'
    + N'"thisQty":{"scope":"DETAIL","agg":"SUM","terms":[{"field":"QTY","coef":1}]},'
    + N'"usage":{"scope":"TARGET","fields":["RECEIVE_QTY"]},'
    + N'"limit":{"scope":"TARGET","fields":["QTY"]},'
    + N'"offset":0.1,'
    + N'"diagnosticFields":['
    + N'{"scope":"SOURCE","field":"PURCHASE_SERIAL_NO"},'
    + N'{"scope":"TARGET","field":"QTY"},'
    + N'{"scope":"TARGET","field":"RECEIVE_QTY"},'
    + N'{"scope":"THIS"}],'
    + N'"message":"以下项收料数量超出采购数量 ' + @NLJ + N'序号  采购数量  已收数量  单据数量 ' + @NLJ + N'{ROWS}"}]}';

IF EXISTS (SELECT 1 FROM dbo.MODULE_VALIDATION_RULE WHERE MODULE_ID = 1607 AND STAGE = N'SAVE' AND SEQ = 2)
    UPDATE dbo.MODULE_VALIDATION_RULE
    SET VALIDATION_KEY = N'qty-not-exceed', ENABLED = 1, PARAM_STRUCT = @PARAM, MESSAGE = NULL,
        REMARK = N'保存侧收料不超采购（按采购行分组求和 + 0.1 容差）',
        SOURCE_REF = N'pur-receive',
        LAST_UPDATE_BY = N'migration-099', LAST_UPDATE_DATE = SYSDATETIME()
    WHERE MODULE_ID = 1607 AND STAGE = N'SAVE' AND SEQ = 2;
ELSE
    INSERT INTO dbo.MODULE_VALIDATION_RULE
        (MODULE_ID, STAGE, SEQ, VALIDATION_KEY, ENABLED, PARAM_STRUCT, MESSAGE, REMARK, SOURCE_REF, CREATE_PERSON, CREATE_DATE)
    VALUES
        (1607, N'SAVE', 2, N'qty-not-exceed', 1, @PARAM, NULL,
         N'保存侧收料不超采购（按采购行分组求和 + 0.1 容差）', N'pur-receive', N'migration-099', SYSDATETIME());

PRINT N'-- 影响面：现存收料按采购行合计后超采购量的行数（只读） --';
SELECT CONCAT(N'  按采购行合计后超量的行=', COUNT(*)) AS IMPACT
FROM (
    SELECT i.QTY AS DOC_QTY, o.QTY AS PUR_QTY, ISNULL(o.RECEIVE_QTY,0) AS REC_QTY
    FROM (SELECT PURCHASE_TYPE, PURCHASE_NO, PURCHASE_SERIAL_NO, SUM(QTY) QTY
          FROM dbo.PUR_RECEIVE_D GROUP BY PURCHASE_TYPE, PURCHASE_NO, PURCHASE_SERIAL_NO) i
    INNER JOIN dbo.PUR_PURCHASE_D o
      ON o.PURCHASE_TYPE=i.PURCHASE_TYPE AND o.PURCHASE_NO=i.PURCHASE_NO AND o.SERIAL_NO=i.PURCHASE_SERIAL_NO
) x
WHERE x.DOC_QTY > x.PUR_QTY - x.REC_QTY + 0.1;

PRINT N'== 播种完成 ==';
