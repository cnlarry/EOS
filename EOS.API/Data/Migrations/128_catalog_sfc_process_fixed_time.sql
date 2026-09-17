-- ============================================================================
-- EOS.ERP migration 128: 2703 产品制程 —— "用固定时间时不得为 0" 入目录（批 4：退役 `sfc-process` 族）
-- ----------------------------------------------------------------------------
-- 退役对象：`DomainRuleMap[2703] = "sfc-process"` → `SfcDomainRules.SfcProcessAfterSaveAsync`：
--     SELECT TOP 1 1 FROM dbo.SFC_PROCESS_D t
--     WHERE t.PRO_NO=@ProNo AND t.STANDARD_TIME_TAG=1 AND ISNULL(t.STANDARD_TIME,0)=0;
--   命中即拒绝，文案 N'产品编号使用固定时间时，固定时间不能为0 \r\n'（**无逐行诊断**）。
-- 目录承接：SAVE 期 `line-require` —— 触发条件用**结构化 condition**（本行 `STANDARD_TIME_TAG=1`，
--   DETAIL 域即本行别名 S），要求字段 `STANDARD_TIME` 已填；判据 `S.STANDARD_TIME IS NULL OR = ''`
--   对 float 列恰好等价旧 `ISNULL(t.STANDARD_TIME,0)=0`（ SQL 把 '' 转成 0 比较）。
--   文案与旧实现逐字一致（含尾部 ` \r\n`），且不配置 diagnosticFields（旧实现也不回报行）。
-- 说明：本迁移只播种 SAVE 期规则（2703 既有 SAVE 期 `reference-exists` 不受影响）；C# 侧退役同步在代码里完成。
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

DECLARE @Message NVARCHAR(200) = N'产品编号使用固定时间时，固定时间不能为0 ' + CHAR(13) + CHAR(10);
DECLARE @Params NVARCHAR(MAX) =
    N'{"checks":[{"scope":"DETAIL","field":"STANDARD_TIME",'
    + N'"condition":{"logic":"AND","items":[{"type":"value-eq","field":{"scope":"DETAIL","field":"STANDARD_TIME_TAG"},"value":1}]},'
    + N'"message":"产品编号使用固定时间时，固定时间不能为0 \r\n"}]}';

IF EXISTS (SELECT 1 FROM dbo.MODULE_VALIDATION_RULE
           WHERE MODULE_ID = 2703 AND STAGE = N'SAVE' AND VALIDATION_KEY = N'line-require')
BEGIN
    IF NOT EXISTS (SELECT 1 FROM dbo.MODULE_VALIDATION_RULE
                   WHERE MODULE_ID = 2703 AND STAGE = N'SAVE' AND VALIDATION_KEY = N'line-require'
                     AND PARAM_STRUCT = @Params AND ISNULL(MESSAGE, N'') = @Message)
        THROW 50001, N'2703 SAVE 期 line-require 已存在但与预期参数不一致（漂移），迁移中止。', 1;
    PRINT N'2703 固定时间校验已存在且参数一致，跳过。';
END
ELSE
BEGIN
    INSERT INTO dbo.MODULE_VALIDATION_RULE
        (MODULE_ID, STAGE, SEQ, VALIDATION_KEY, ENABLED, PARAM_STRUCT, MESSAGE, REMARK, SOURCE_REF, CREATE_PERSON, CREATE_DATE, LAST_UPDATE_BY, LAST_UPDATE_DATE)
    VALUES
        (2703, N'SAVE', 2, N'line-require', 1, @Params, @Message,
         N'产品制程：用固定时间时固定时间不得为 0', N'P_SFC_PROCESS', N'DbUp', SYSDATETIME(), N'DbUp', SYSDATETIME());
    PRINT N'2703 SAVE 期固定时间校验已播种（line-require + 结构化触发条件）。';
END

IF NOT EXISTS (SELECT 1 FROM dbo.MODULE_VALIDATION_RULE
               WHERE MODULE_ID = 2703 AND STAGE = N'SAVE' AND VALIDATION_KEY = N'line-require'
                 AND ENABLED = 1 AND PARAM_STRUCT LIKE N'%STANDARD_TIME_TAG%')
    THROW 50002, N'2703 缺少启用的固定时间校验规则，迁移中止。', 1;
