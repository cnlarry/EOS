-- ============================================================================
-- EOS.ERP migration 126: 2707 工序发料单 —— 受门控的"出库不超工序工单入库"校验入目录（批 4：退役 `moc-work-out` 族）
-- ----------------------------------------------------------------------------
-- 退役对象：`DomainRuleMap[2707] = "moc-work-out"` → `MocDomainRules.MocWorkOutAfterSaveAsync`：
--   在 `MODULES.ERROR_NO_SAVE=1` 门控下（当前该模块门=0 ⇒ 判据休眠）**逐行**比较：
--     SELECT od.WORK_TYPE, od.WORK_NO, od.PROCESS_QTY, od.FINISHED_OUT_QTY, sd.QTY
--     FROM dbo.MOC_WORK_D od
--     INNER JOIN dbo.MOC_WORK_OUT_D sd
--       ON sd.WORK_TYPE=od.WORK_TYPE AND sd.WORK_NO=od.WORK_NO AND sd.WORK_SERIAL_NO=od.SERIAL_NO
--     WHERE sd.WORK_OUT_TYPE=@Type AND sd.WORK_OUT_NO=@No
--       AND ISNULL(od.FINISHED_OUT_QTY,0) + ISNULL(sd.QTY,0) > ISNULL(od.FINISHED_IN_QTY,0);
--   命中即拒绝，文案 = N'以下出库超出工序工单入库数量\r\n工序工单单别   单号   数量   已入库数量   单据数量\r\n' + 各行（五列四空格分隔、行间 CRLF）。
-- 目录承接：SAVE 期 `qty-not-exceed`（mode=usage-not-exceed）——
--   targetTable=MOC_WORK_D、match 按**明细行**定位（WORK_TYPE/WORK_NO/SERIAL_NO ← MOC_WORK_OUT_D 的
--   WORK_TYPE/WORK_NO/WORK_SERIAL_NO，与旧 JOIN 逐字一致）、thisQty=本行 QTY（**逐行**，与既有实现一致，不做分组求和）、
--   usage=FINISHED_OUT_QTY、limit=FINISHED_IN_QTY，比较式 `usage + thisQty > limit` ⇔ 旧判据；
--   门控 `switch.gates=[{scope:"MODULE",key:"ERROR_NO_SAVE",expect:1}]`（**开关语义保持**）；
--   诊断五列（TARGET.WORK_TYPE/WORK_NO/PROCESS_QTY/FINISHED_OUT_QTY + SOURCE.QTY，列间四空格、行间 CRLF）
--   与文案头部逐字复刻（`maxRows` 取上限 100；既有实现不限行数，超过 100 行的极端单据只截断列表尾部）。
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

DECLARE @Message NVARCHAR(1000) =
    N'以下出库超出工序工单入库数量' + CHAR(13) + CHAR(10)
    + N'工序工单单别   单号   数量   已入库数量   单据数量' + CHAR(13) + CHAR(10) + N'{ROWS}';
DECLARE @Params NVARCHAR(MAX) =
    N'{"mode":"usage-not-exceed","checks":[{"targetTable":"MOC_WORK_D",'
    + N'"match":[{"target":"WORK_TYPE","source":{"scope":"DETAIL","field":"WORK_TYPE"}},'
    + N'{"target":"WORK_NO","source":{"scope":"DETAIL","field":"WORK_NO"}},'
    + N'{"target":"SERIAL_NO","source":{"scope":"DETAIL","field":"WORK_SERIAL_NO"}}],'
    + N'"thisQty":{"scope":"DETAIL","terms":[{"field":"QTY","coef":1}]},'
    + N'"usage":{"scope":"TARGET","fields":["FINISHED_OUT_QTY"]},'
    + N'"limit":{"scope":"TARGET","fields":["FINISHED_IN_QTY"]},'
    + N'"switch":{"gates":[{"scope":"MODULE","key":"ERROR_NO_SAVE","expect":1}]},'
    + N'"diagnosticFields":[{"scope":"TARGET","field":"WORK_TYPE"},{"scope":"TARGET","field":"WORK_NO"},'
    + N'{"scope":"TARGET","field":"PROCESS_QTY"},{"scope":"TARGET","field":"FINISHED_OUT_QTY"},'
    + N'{"scope":"SOURCE","field":"QTY"}],'
    + N'"diagnosticCellSeparator":"    ",'
    + N'"maxRows":100,'
    + N'"message":"以下出库超出工序工单入库数量\r\n工序工单单别   单号   数量   已入库数量   单据数量\r\n{ROWS}"}]}';

IF EXISTS (SELECT 1 FROM dbo.MODULE_VALIDATION_RULE
           WHERE MODULE_ID = 2707 AND STAGE = N'SAVE' AND VALIDATION_KEY = N'qty-not-exceed')
BEGIN
    IF NOT EXISTS (SELECT 1 FROM dbo.MODULE_VALIDATION_RULE
                   WHERE MODULE_ID = 2707 AND STAGE = N'SAVE' AND VALIDATION_KEY = N'qty-not-exceed'
                     AND PARAM_STRUCT = @Params AND ISNULL(MESSAGE, N'') = @Message)
        THROW 50001, N'2707 SAVE 期 qty-not-exceed 已存在但与预期参数不一致（漂移），迁移中止。', 1;
    PRINT N'2707 受门控数量校验已存在且参数一致，跳过。';
END
ELSE
BEGIN
    INSERT INTO dbo.MODULE_VALIDATION_RULE
        (MODULE_ID, STAGE, SEQ, VALIDATION_KEY, ENABLED, PARAM_STRUCT, MESSAGE, REMARK, SOURCE_REF, CREATE_PERSON, CREATE_DATE, LAST_UPDATE_BY, LAST_UPDATE_DATE)
    VALUES
        (2707, N'SAVE', 1, N'qty-not-exceed', 1, @Params, @Message,
         N'工序发料：出库不超工序工单入库数量（受 ERROR_NO_SAVE 门控）', N'P_MOC_WORK_OUT', N'DbUp', SYSDATETIME(), N'DbUp', SYSDATETIME());
    PRINT N'2707 SAVE 期受门控数量校验已播种（qty-not-exceed / usage-not-exceed + MODULE 门控 + 五列诊断）。';
END

IF NOT EXISTS (SELECT 1 FROM dbo.MODULE_VALIDATION_RULE
               WHERE MODULE_ID = 2707 AND STAGE = N'SAVE' AND ENABLED = 1
                 AND PARAM_STRUCT LIKE N'%{"scope":"MODULE","key":"ERROR_NO_SAVE","expect":1}%')
    THROW 50002, N'2707 缺少启用的受门控 SAVE 期规则，迁移中止。', 1;
