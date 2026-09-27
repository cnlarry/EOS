-- ============================================================================
-- EOS.ERP migration 125: 2912 量产模入库单 —— 受门控的"入库不超完工未入"校验入目录（批 4：退役 `mou-batchin` 族）
-- ----------------------------------------------------------------------------
-- 退役对象：`DomainRuleMap[2912] = "mou-batchin"` → `MouDomainRules.MouBatchinAfterSaveAsync`：
--   在 `MODULES.ERROR_NO_SAVE=1` 门控下（当前该模块门=0 ⇒ 判据休眠）执行：
--     SELECT d.SERIAL_NO
--     FROM dbo.MOU_BATCH_M m
--     INNER JOIN (SELECT BATCH_TYPE, BATCH_NO, MAX(SERIAL_NO) SERIAL_NO, SUM(QTY) QTY
--                 FROM dbo.MOU_BATCHIN_D WHERE BATCHIN_TYPE=@Type AND BATCHIN_NO=@No
--                 GROUP BY BATCH_TYPE, BATCH_NO) d
--       ON m.BATCH_TYPE=d.BATCH_TYPE AND m.BATCH_NO=d.BATCH_NO
--     WHERE ISNULL(m.QTY,0) < ISNULL(m.FINISHED_QTY,0) + d.QTY;
--   命中即拒绝，文案 N'以下序号项量产模入库不能大于模具完工未入数量\r\n' + 各行（`MAX(SERIAL_NO)` 后跟四空格、行间 CRLF）。
-- 目录承接：SAVE 期 `qty-not-exceed`（mode=usage-not-exceed）——
--   targetTable=MOU_BATCH_M、match 按明细 MOU_BATCHIN_D 的 BATCH_TYPE/BATCH_NO 定位、
--   thisQty=SUM(本单 QTY)（**分组形态**，与既有实现按 BATCH 分组求和一致）、
--   usage=模具行 FINISHED_QTY、limit=模具行 QTY，比较式 `usage + thisQty > limit` ⇔ 旧 `ISNULL(m.QTY,0) < ISNULL(m.FINISHED_QTY,0) + d.QTY`；
--   门控 `switch.gates=[{scope:"MODULE",key:"ERROR_NO_SAVE",expect:1}]`（**开关语义保持**）；
--   诊断 `[{scope:"SOURCE",field:"SERIAL_NO",agg:"MAX"}]` + 行分隔 CRLF + 文案尾部四空格
--   —— 复刻既有实现逐行 `MAX(SERIAL_NO) + " "`（`maxRows` 取上限 100；超过 100 行的极端单据只截断列表尾部）。
-- 说明：本迁移只补 SAVE 期规则（2912 既有 SAVE 期 `reference-exists` 不受影响）；C# 侧退役同步在代码里完成。
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

DECLARE @Message NVARCHAR(1000) = N'以下序号项量产模入库不能大于模具完工未入数量' + CHAR(13) + CHAR(10) + N'{ROWS}    ';
DECLARE @Params NVARCHAR(MAX) =
    N'{"mode":"usage-not-exceed","checks":[{"targetTable":"MOU_BATCH_M",'
    + N'"match":[{"target":"BATCH_TYPE","source":{"scope":"DETAIL","field":"BATCH_TYPE"}},'
    + N'{"target":"BATCH_NO","source":{"scope":"DETAIL","field":"BATCH_NO"}}],'
    + N'"thisQty":{"scope":"DETAIL","agg":"SUM","terms":[{"field":"QTY","coef":1}]},'
    + N'"usage":{"scope":"TARGET","fields":["FINISHED_QTY"]},'
    + N'"limit":{"scope":"TARGET","fields":["QTY"]},'
    + N'"switch":{"gates":[{"scope":"MODULE","key":"ERROR_NO_SAVE","expect":1}]},'
    + N'"diagnosticFields":[{"scope":"SOURCE","field":"SERIAL_NO","agg":"MAX"}],'
    + N'"diagnosticRowSeparator":"\r\n",'
    + N'"maxRows":100,'
    + N'"message":"以下序号项量产模入库不能大于模具完工未入数量\r\n{ROWS}    "}]}';

IF EXISTS (SELECT 1 FROM dbo.MODULE_VALIDATION_RULE
           WHERE MODULE_ID = 2912 AND STAGE = N'SAVE' AND VALIDATION_KEY = N'qty-not-exceed')
BEGIN
    IF NOT EXISTS (SELECT 1 FROM dbo.MODULE_VALIDATION_RULE
                   WHERE MODULE_ID = 2912 AND STAGE = N'SAVE' AND VALIDATION_KEY = N'qty-not-exceed'
                     AND PARAM_STRUCT = @Params AND ISNULL(MESSAGE, N'') = @Message)
        THROW 50001, N'2912 SAVE 期 qty-not-exceed 已存在但与预期参数不一致（漂移），迁移中止。', 1;
    PRINT N'2912 受门控数量校验已存在且参数一致，跳过。';
END
ELSE
BEGIN
    INSERT INTO dbo.MODULE_VALIDATION_RULE
        (MODULE_ID, STAGE, SEQ, VALIDATION_KEY, ENABLED, PARAM_STRUCT, MESSAGE, REMARK, SOURCE_REF, CREATE_PERSON, CREATE_DATE, LAST_UPDATE_BY, LAST_UPDATE_DATE)
    VALUES
        (2912, N'SAVE', 2, N'qty-not-exceed', 1, @Params, @Message,
         N'量产模入库：入库不超模具完工未入数量（受 ERROR_NO_SAVE 门控）', N'P_MOU_BATCHIN', N'DbUp', SYSDATETIME(), N'DbUp', SYSDATETIME());
    PRINT N'2912 SAVE 期受门控数量校验已播种（qty-not-exceed / usage-not-exceed + MODULE 门控 + 源列聚合诊断）。';
END

IF NOT EXISTS (SELECT 1 FROM dbo.MODULE_VALIDATION_RULE
               WHERE MODULE_ID = 2912 AND STAGE = N'SAVE' AND VALIDATION_KEY = N'qty-not-exceed'
                 AND PARAM_STRUCT LIKE N'%{"scope":"MODULE","key":"ERROR_NO_SAVE","expect":1}%'
                 AND PARAM_STRUCT LIKE N'%"agg":"MAX"%')
    THROW 50002, N'2912 数量校验缺少 MODULE 门控或源列聚合诊断，迁移中止。', 1;
