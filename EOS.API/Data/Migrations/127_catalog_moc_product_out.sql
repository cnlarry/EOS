-- ============================================================================
-- EOS.ERP migration 127: 2815 托外返工单 / 1515 返工单 —— 受门控的出库数量校验 + 批号必填入目录
--                                                       （批 4：退役 `moc-product-out` 族）
-- ----------------------------------------------------------------------------
-- 退役对象：`DomainRuleMap[2815|1515] = "moc-product-out"` → `MocDomainRules.MocProductOutAfterSaveAsync`：
--   ⒜ `MODULES.ERROR_NO_SAVE=1` 门控 + `SYSSS.FITOUT_TAG` 二选一的数量校验：
--        出库明细按 (PRODUCE_TYPE, PRODUCE_NO) 分组求和，与制令单行比较——
--          FITOUT_TAG=1：`t.QTY + ISNULL(m.FINISHED_FITOUT_QTY,0) > ISNULL(m.FINISHED_QTY,0)`
--                        或 `t.SPARE_QTY + ISNULL(m.FINISHED_FITOUT_SPARE_QTY,0) > ISNULL(m.FINISHED_SPARE_QTY,0)`
--          FITOUT_TAG=0：同上但用 FINISHED_SEND_QTY / FINISHED_SEND_SPARE_QTY
--        命中文案：N'以下生产单出库数量超出制令可出库 \r\n'（FITOUT）或 N'以下生产单出库数量超出订单可出库 \r\n'（SEND）
--                  + 至多 10 个 PRODUCE_NO（两空格分隔）；旧 SQL `TOP 11` + `Take(10)`。
--   ⒝ 无门控的批号必填（`line-require`）：批管品（`PRODUCT.MANAGE_BATCH=1`）未填 `BATCH_NO` 即拒绝，
--        文案 N'以下序号项需要输入批号 '，逐行回报序号。
-- 目录承接（每个模块两条规则）：
--   SEQ=2 `qty-not-exceed`（mode=usage-not-exceed）——四条 check（FITOUT/SEND × 数量/备品），
--     每条 `switch.gates=[{scope:"MODULE",key:"ERROR_NO_SAVE",expect:1},{scope:"SYSSS",key:"FITOUT_TAG",expect:1|0}]`
--     （**模块开关语义保持**：门=1 才生效；FITOUT 分支由全局开关二选一，与旧 if/else 一致）；
--     `usage`/`limit` 取制令单行字段、`thisQty` 分组求和、诊断取分组键 `PRODUCE_NO`（两空格行分隔、`maxRows`=10）。
--   SEQ=3 `line-require`——参数与既有同形族（迁移 111/113）逐字一致，换用本模块明细表。
-- 说明：诊断里的字符列由模板按 `Trim` 输出，而既有实现直接拼接 `nchar` 原值（含尾部填充空格）——
--   差异仅限不可见空白；数值列（数量/备品）两侧格式一致。
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

/* ---------- 数量校验参数（四分支） ---------- */
DECLARE @QtyMessageFitout NVARCHAR(600) =
    N'以下生产单出库数量超出制令可出库 ' + CHAR(13) + CHAR(10) + N'{ROWS}';
DECLARE @QtyMessageSend NVARCHAR(600) =
    N'以下生产单出库数量超出订单可出库 ' + CHAR(13) + CHAR(10) + N'{ROWS}';
DECLARE @QtyParams NVARCHAR(MAX) =
    N'{"mode":"usage-not-exceed","checks":['
    + N'{"targetTable":"MOC_PRODUCE_M",'
    + N'"match":[{"target":"PRODUCE_TYPE","source":{"scope":"DETAIL","field":"PRODUCE_TYPE"}},'
    + N'{"target":"PRODUCE_NO","source":{"scope":"DETAIL","field":"PRODUCE_NO"}}],'
    + N'"thisQty":{"scope":"DETAIL","agg":"SUM","terms":[{"field":"QTY","coef":1}]},'
    + N'"usage":{"scope":"TARGET","fields":["FINISHED_FITOUT_QTY"]},'
    + N'"limit":{"scope":"TARGET","fields":["FINISHED_QTY"]},'
    + N'"switch":{"gates":[{"scope":"MODULE","key":"ERROR_NO_SAVE","expect":1},{"scope":"SYSSS","key":"FITOUT_TAG","expect":1}]},'
    + N'"diagnosticFields":[{"scope":"SOURCE","field":"PRODUCE_NO"}],"diagnosticRowSeparator":"  ","maxRows":10,'
    + N'"message":"以下生产单出库数量超出制令可出库 \r\n{ROWS}"},'
    + N'{"targetTable":"MOC_PRODUCE_M",'
    + N'"match":[{"target":"PRODUCE_TYPE","source":{"scope":"DETAIL","field":"PRODUCE_TYPE"}},'
    + N'{"target":"PRODUCE_NO","source":{"scope":"DETAIL","field":"PRODUCE_NO"}}],'
    + N'"thisQty":{"scope":"DETAIL","agg":"SUM","terms":[{"field":"SPARE_QTY","coef":1}]},'
    + N'"usage":{"scope":"TARGET","fields":["FINISHED_FITOUT_SPARE_QTY"]},'
    + N'"limit":{"scope":"TARGET","fields":["FINISHED_SPARE_QTY"]},'
    + N'"switch":{"gates":[{"scope":"MODULE","key":"ERROR_NO_SAVE","expect":1},{"scope":"SYSSS","key":"FITOUT_TAG","expect":1}]},'
    + N'"diagnosticFields":[{"scope":"SOURCE","field":"PRODUCE_NO"}],"diagnosticRowSeparator":"  ","maxRows":10,'
    + N'"message":"以下生产单出库数量超出制令可出库 \r\n{ROWS}"},'
    + N'{"targetTable":"MOC_PRODUCE_M",'
    + N'"match":[{"target":"PRODUCE_TYPE","source":{"scope":"DETAIL","field":"PRODUCE_TYPE"}},'
    + N'{"target":"PRODUCE_NO","source":{"scope":"DETAIL","field":"PRODUCE_NO"}}],'
    + N'"thisQty":{"scope":"DETAIL","agg":"SUM","terms":[{"field":"QTY","coef":1}]},'
    + N'"usage":{"scope":"TARGET","fields":["FINISHED_SEND_QTY"]},'
    + N'"limit":{"scope":"TARGET","fields":["FINISHED_QTY"]},'
    + N'"switch":{"gates":[{"scope":"MODULE","key":"ERROR_NO_SAVE","expect":1},{"scope":"SYSSS","key":"FITOUT_TAG","expect":0}]},'
    + N'"diagnosticFields":[{"scope":"SOURCE","field":"PRODUCE_NO"}],"diagnosticRowSeparator":"  ","maxRows":10,'
    + N'"message":"以下生产单出库数量超出订单可出库 \r\n{ROWS}"},'
    + N'{"targetTable":"MOC_PRODUCE_M",'
    + N'"match":[{"target":"PRODUCE_TYPE","source":{"scope":"DETAIL","field":"PRODUCE_TYPE"}},'
    + N'{"target":"PRODUCE_NO","source":{"scope":"DETAIL","field":"PRODUCE_NO"}}],'
    + N'"thisQty":{"scope":"DETAIL","agg":"SUM","terms":[{"field":"SPARE_QTY","coef":1}]},'
    + N'"usage":{"scope":"TARGET","fields":["FINISHED_SEND_SPARE_QTY"]},'
    + N'"limit":{"scope":"TARGET","fields":["FINISHED_SPARE_QTY"]},'
    + N'"switch":{"gates":[{"scope":"MODULE","key":"ERROR_NO_SAVE","expect":1},{"scope":"SYSSS","key":"FITOUT_TAG","expect":0}]},'
    + N'"diagnosticFields":[{"scope":"SOURCE","field":"PRODUCE_NO"}],"diagnosticRowSeparator":"  ","maxRows":10,'
    + N'"message":"以下生产单出库数量超出订单可出库 \r\n{ROWS}"}]}';

/* ---------- 批号必填参数（与既有同形族逐字一致） ---------- */
DECLARE @LineMessage NVARCHAR(200) = N'以下序号项需要输入批号 ';DECLARE @LineParams NVARCHAR(MAX) =
    N'{"checks":[{"scope":"DETAIL","field":"BATCH_NO","message":"以下序号项需要输入批号 ",'
    + N'"diagnosticFields":["SERIAL_NO"],'
    + N'"condition":{"logic":"AND","items":[{"type":"not-exists","targetTable":"PRODUCT","negate":true,'
    + N'"condition":{"type":"value-eq","field":{"scope":"TARGET","field":"MANAGE_BATCH"},"value":1},'
    + N'"match":[{"target":"PRO_NO","source":{"scope":"DETAIL","field":"PRO_NO"}}]}]}}]}';

DECLARE @Targets TABLE (MODULE_ID INT);
INSERT INTO @Targets (MODULE_ID) VALUES (2815), (1515);

/* 幂等 + 漂移守卫（数量校验的规则级 MESSAGE 必须为空：四条 check 各自带分支文案） */
IF EXISTS (
    SELECT 1 FROM @Targets T
    WHERE EXISTS (SELECT 1 FROM dbo.MODULE_VALIDATION_RULE R
                  WHERE R.MODULE_ID = T.MODULE_ID AND R.STAGE = N'SAVE' AND R.VALIDATION_KEY = N'qty-not-exceed'
                    AND (R.PARAM_STRUCT <> @QtyParams OR ISNULL(R.MESSAGE, N'') <> N''))
       OR EXISTS (SELECT 1 FROM dbo.MODULE_VALIDATION_RULE R
                  WHERE R.MODULE_ID = T.MODULE_ID AND R.STAGE = N'SAVE' AND R.VALIDATION_KEY = N'line-require'
                    AND R.PARAM_STRUCT <> @LineParams))
    THROW 50001, N'2815/1515 的 SAVE 期规则已存在但与预期参数不一致（漂移），迁移中止。', 1;

INSERT INTO dbo.MODULE_VALIDATION_RULE
    (MODULE_ID, STAGE, SEQ, VALIDATION_KEY, ENABLED, PARAM_STRUCT, MESSAGE, REMARK, SOURCE_REF, CREATE_PERSON, CREATE_DATE, LAST_UPDATE_BY, LAST_UPDATE_DATE)
SELECT T.MODULE_ID, N'SAVE', 2, N'qty-not-exceed', 1, @QtyParams, NULL,
       N'生产出库：出库不超制令/订单可出库（受 ERROR_NO_SAVE 与 FITOUT_TAG 门控）', N'P_MOC_PRODUCT_OUT', N'DbUp', SYSDATETIME(), N'DbUp', SYSDATETIME()
FROM @Targets T
WHERE NOT EXISTS (SELECT 1 FROM dbo.MODULE_VALIDATION_RULE R
                  WHERE R.MODULE_ID = T.MODULE_ID AND R.STAGE = N'SAVE' AND R.VALIDATION_KEY = N'qty-not-exceed');

INSERT INTO dbo.MODULE_VALIDATION_RULE
    (MODULE_ID, STAGE, SEQ, VALIDATION_KEY, ENABLED, PARAM_STRUCT, MESSAGE, REMARK, SOURCE_REF, CREATE_PERSON, CREATE_DATE, LAST_UPDATE_BY, LAST_UPDATE_DATE)
SELECT T.MODULE_ID, N'SAVE', 3, N'line-require', 1, @LineParams, @LineMessage,
       N'生产出库：批管品必填批号', N'P_MOC_PRODUCT_OUT', N'DbUp', SYSDATETIME(), N'DbUp', SYSDATETIME()
FROM @Targets T
WHERE NOT EXISTS (SELECT 1 FROM dbo.MODULE_VALIDATION_RULE R
                  WHERE R.MODULE_ID = T.MODULE_ID AND R.STAGE = N'SAVE' AND R.VALIDATION_KEY = N'line-require');

/* 守卫：两个模块都具备启用的 SAVE 期规则、且数量校验带双域门控 */
IF EXISTS (
    SELECT 1 FROM @Targets T
    WHERE NOT EXISTS (SELECT 1 FROM dbo.MODULE_VALIDATION_RULE R
                      WHERE R.MODULE_ID = T.MODULE_ID AND R.STAGE = N'SAVE' AND R.ENABLED = 1
                        AND R.VALIDATION_KEY = N'qty-not-exceed'
                        AND R.PARAM_STRUCT LIKE N'%"scope":"MODULE","key":"ERROR_NO_SAVE","expect":1%'
                        AND R.PARAM_STRUCT LIKE N'%"scope":"SYSSS","key":"FITOUT_TAG"%')
       OR NOT EXISTS (SELECT 1 FROM dbo.MODULE_VALIDATION_RULE R
                      WHERE R.MODULE_ID = T.MODULE_ID AND R.STAGE = N'SAVE' AND R.ENABLED = 1
                        AND R.VALIDATION_KEY = N'line-require'))
    THROW 50002, N'2815/1515 缺少启用的受门控数量校验或批号必填规则，迁移中止。', 1;

PRINT N'== 2815/1515 出库数量校验（四分支双域门控）+ 批号必填已入目录 ==';
