-- ============================================================================
-- EOS.ERP migration 122: 2906 量产模具（开模完工）单 SAVE 期数量校验入目录（批 4：退役 `mou-batch` 族）
-- ----------------------------------------------------------------------------
-- 退役对象：`DomainRuleMap[2906] = "mou-batch"` → `MouDomainRules.MouBatchAfterSaveAsync`
--   旧判据（保存期，同一事务内）：
--     SELECT TOP 1 1 FROM dbo.MOU_BATCH_M d
--       JOIN dbo.MOU_ACCEPT_M m ON m.ACCEPT_TYPE=d.ACCEPT_TYPE AND m.ACCEPT_NO=d.ACCEPT_NO
--      WHERE d.BATCH_TYPE=@Type AND d.BATCH_NO=@No
--        AND ISNULL(m.QTY,0) < ISNULL(m.FINISHED_QTY,0) + ISNULL(d.QTY,0);
--   命中即拒绝，文案 N'申请数量已超过承认单可申请数量'。
-- 目录承接：与 2906 既有 APPROVE 期规则同款参数的 `qty-not-exceed`（mode=usage-not-exceed）——
--   targetTable=MOU_ACCEPT_M（被引用承认单）、match=本单主表列 ACCEPT_TYPE/ACCEPT_NO 定位、
--   thisQty=本单 QTY、usage=承认单 FINISHED_QTY、limit=承认单 QTY，比较式为
--   `FINISHED_QTY + QTY(本单) > QTY(承认单)`，与旧判据逐字等价（NULL 一律按 0）。
-- 说明：本迁移只播种 SAVE 期规则；C# 侧退役（删族注册/分派/方法）与 `CatalogAfterSaveMap` 登记
--   同步在代码里完成，发布快照后即刻生效。
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

DECLARE @Message NVARCHAR(400) = N'申请数量已超过承认单可申请数量';
DECLARE @Params NVARCHAR(MAX) =
    N'{"mode":"usage-not-exceed","checks":[{"targetTable":"MOU_ACCEPT_M",'
    + N'"match":[{"target":"ACCEPT_TYPE","source":{"scope":"MASTER","field":"ACCEPT_TYPE"}},'
    + N'{"target":"ACCEPT_NO","source":{"scope":"MASTER","field":"ACCEPT_NO"}}],'
    + N'"thisQty":{"scope":"MASTER","terms":[{"field":"QTY","coef":1}]},'
    + N'"usage":{"scope":"TARGET","fields":["FINISHED_QTY"]},'
    + N'"limit":{"scope":"TARGET","fields":["QTY"]},'
    + N'"message":"申请数量已超过承认单可申请数量"}]}';

IF EXISTS (SELECT 1 FROM dbo.MODULE_VALIDATION_RULE
           WHERE MODULE_ID = 2906 AND STAGE = N'SAVE' AND VALIDATION_KEY = N'qty-not-exceed')
BEGIN
    /* 幂等：已播种则核对参数一致（漂移即中止，避免保留一个不同口径的规则） */
    IF NOT EXISTS (SELECT 1 FROM dbo.MODULE_VALIDATION_RULE
                   WHERE MODULE_ID = 2906 AND STAGE = N'SAVE' AND VALIDATION_KEY = N'qty-not-exceed'
                     AND PARAM_STRUCT = @Params AND ISNULL(MESSAGE, N'') = @Message)
        THROW 50001, N'2906 SAVE 期 qty-not-exceed 已存在但与预期参数不一致（漂移），迁移中止。', 1;
    PRINT N'2906 SAVE 期数量校验已存在且参数一致，跳过。';
END
ELSE
BEGIN
    INSERT INTO dbo.MODULE_VALIDATION_RULE
        (MODULE_ID, STAGE, SEQ, VALIDATION_KEY, ENABLED, PARAM_STRUCT, MESSAGE, REMARK, SOURCE_REF, CREATE_PERSON, CREATE_DATE, LAST_UPDATE_BY, LAST_UPDATE_DATE)
    VALUES
        (2906, N'SAVE', 1, N'qty-not-exceed', 1, @Params, @Message,
         N'量产模具完工：申请数量不超承认单可申请数量', N'P_MOU_BATCH', N'DbUp', SYSDATETIME(), N'DbUp', SYSDATETIME());
    PRINT N'2906 SAVE 期数量校验已播种（qty-not-exceed / usage-not-exceed）。';
END

/* 守卫：该模块必须有启用的 SAVE 期规则（目录承接后保存路径才拦得住） */
IF NOT EXISTS (SELECT 1 FROM dbo.MODULE_VALIDATION_RULE
               WHERE MODULE_ID = 2906 AND STAGE = N'SAVE' AND ENABLED = 1)
    THROW 50002, N'2906 缺少启用的 SAVE 期目录规则，迁移中止。', 1;
