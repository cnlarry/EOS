-- ============================================================================
-- EOS.ERP migration 124: 3303 品质日分析单 —— 受模块开关门控的数量校验入目录（批 4：退役 `qc-analysis` 族）
-- ----------------------------------------------------------------------------
-- 退役对象：`DomainRuleMap[3303] = "qc-analysis"` → `CusDomainRules.QcAnalysisAfterSaveAsync`，
--   旧判据分两段：
--     ⒜ 两条**无门控**引用校验（"以下序号项制令单不存在"/"以下序号项产品编号不存在"）
--        —— 目录里**已有**同款 SAVE 期 `reference-exists`（SEQ=1，refTable=MOC_PRODUCE_M 按
--        PRODUCE_TYPE/PRODUCE_NO 定位 + PRODUCT 按 PRO_NO 定位，文案一致），无需重复播种；
--     ⒝ 一条 `MODULES.ERROR_NO_SAVE=1` 门控的数量校验（当前该模块门=0 ⇒ 判据休眠）：
--          SELECT t.PRODUCE_NO FROM (SELECT PRODUCE_TYPE, PRODUCE_NO, SUM(PRODUCE_QTY) QTY
--                                    FROM dbo.QC_ANALYSIS_D WHERE ANALYSIS_TYPE=@T AND ANALYSIS_NO=@N
--                                    GROUP BY PRODUCE_TYPE, PRODUCE_NO) t
--          JOIN dbo.MOC_PRODUCE_M m ON m.PRODUCE_TYPE=t.PRODUCE_TYPE AND m.PRODUCE_NO=t.PRODUCE_NO
--          WHERE ISNULL(m.FINISHED_ANALYSIS_QTY,0) + t.QTY > ISNULL(m.QTY,0);
--        命中即拒绝，文案 N'以下生产单号已品检数量超出生产单生产数量！ \r\n' + 各行 PRODUCE_NO（行间两空格、行尾亦两空格）。
--   本迁移补 ⒝：SAVE 期 `qty-not-exceed`（mode=usage-not-exceed）——
--     targetTable=MOC_PRODUCE_M、match 按明细定位、thisQty=SUM(本单 PRODUCE_QTY)、
--     usage=承认行 FINISHED_ANALYSIS_QTY、limit=生产单 QTY，比较式 `usage + thisQty > limit`（与旧判据等价）；
--     门控用 `switch.gates=[{scope:"MODULE",key:"ERROR_NO_SAVE",expect:1}]` —— **开关语义保持**
--     （门=1 才生效，与旧 C# 的 `HasErrorNoSaveAsync` 分支一致；门=0 时同样跳过）。
--   诊断行：`diagnosticFields=[TARGET.PRODUCE_NO]` + 行分隔两空格 + 文案尾部两空格（复刻旧实现逐行 `PRODUCE_NO + "  "`）。
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

DECLARE @Message NVARCHAR(1000) = N'以下生产单号已品检数量超出生产单生产数量！ ' + CHAR(13) + CHAR(10) + N'{ROWS}  ';
DECLARE @Params NVARCHAR(MAX) =
    N'{"mode":"usage-not-exceed","checks":[{"targetTable":"MOC_PRODUCE_M",'
    + N'"match":[{"target":"PRODUCE_TYPE","source":{"scope":"DETAIL","field":"PRODUCE_TYPE"}},'
    + N'{"target":"PRODUCE_NO","source":{"scope":"DETAIL","field":"PRODUCE_NO"}}],'
    + N'"thisQty":{"scope":"DETAIL","agg":"SUM","terms":[{"field":"PRODUCE_QTY","coef":1}]},'
    + N'"usage":{"scope":"TARGET","fields":["FINISHED_ANALYSIS_QTY"]},'
    + N'"limit":{"scope":"TARGET","fields":["QTY"]},'
    + N'"switch":{"gates":[{"scope":"MODULE","key":"ERROR_NO_SAVE","expect":1}]},'
    + N'"diagnosticFields":[{"scope":"TARGET","field":"PRODUCE_NO"}],'
    + N'"diagnosticRowSeparator":"  ",'
    + N'"maxRows":11,'
    + N'"message":"以下生产单号已品检数量超出生产单生产数量！ \r\n{ROWS}  "}]}';

IF EXISTS (SELECT 1 FROM dbo.MODULE_VALIDATION_RULE
           WHERE MODULE_ID = 3303 AND STAGE = N'SAVE' AND VALIDATION_KEY = N'qty-not-exceed')
BEGIN
    IF NOT EXISTS (SELECT 1 FROM dbo.MODULE_VALIDATION_RULE
                   WHERE MODULE_ID = 3303 AND STAGE = N'SAVE' AND VALIDATION_KEY = N'qty-not-exceed'
                     AND PARAM_STRUCT = @Params AND ISNULL(MESSAGE, N'') = @Message)
        THROW 50001, N'3303 SAVE 期 qty-not-exceed 已存在但与预期参数不一致（漂移），迁移中止。', 1;
    PRINT N'3303 受门控数量校验已存在且参数一致，跳过。';
END
ELSE
BEGIN
    INSERT INTO dbo.MODULE_VALIDATION_RULE
        (MODULE_ID, STAGE, SEQ, VALIDATION_KEY, ENABLED, PARAM_STRUCT, MESSAGE, REMARK, SOURCE_REF, CREATE_PERSON, CREATE_DATE, LAST_UPDATE_BY, LAST_UPDATE_DATE)
    VALUES
        (3303, N'SAVE', 2, N'qty-not-exceed', 1, @Params, @Message,
         N'品质日分析：品检数量不超生产单数量（受 ERROR_NO_SAVE 门控）', N'P_QC_ANALYSIS', N'DbUp', SYSDATETIME(), N'DbUp', SYSDATETIME());
    PRINT N'3303 SAVE 期受门控数量校验已播种（qty-not-exceed / usage-not-exceed + MODULE 门控）。';
END

/* 守卫：该模块必须有启用的 SAVE 期规则（目录承接后保存路径才拦得住），且门控形状完整 */
IF NOT EXISTS (SELECT 1 FROM dbo.MODULE_VALIDATION_RULE
               WHERE MODULE_ID = 3303 AND STAGE = N'SAVE' AND ENABLED = 1)
    THROW 50002, N'3303 缺少启用的 SAVE 期目录规则，迁移中止。', 1;
IF NOT EXISTS (SELECT 1 FROM dbo.MODULE_VALIDATION_RULE
               WHERE MODULE_ID = 3303 AND STAGE = N'SAVE' AND VALIDATION_KEY = N'qty-not-exceed'
                 AND PARAM_STRUCT LIKE N'%{"scope":"MODULE","key":"ERROR_NO_SAVE","expect":1}%')
    THROW 50003, N'3303 数量校验缺少 MODULE 门控（开关语义会丢），迁移中止。', 1;
