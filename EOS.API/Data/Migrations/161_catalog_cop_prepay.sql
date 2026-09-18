-- ============================================================================
-- EOS.ERP migration 162: 预收帐款单入效果目录并退役 C#（cop-prepay：170103）
-- ----------------------------------------------------------------------------
-- 旧过程 `P_COP_PREPAY_After_Save` / C# `CopDomainRules.CopPrepayAfterSaveAsync` 只有一段写：
--   主表 `AMOUNT` ＝ `ROUND(SUM(明细 AMOUNT), 3)`；明细为空时不回写（旧实现为内连接形态）。
--   旧的"客户存在且未停用"前置校验早已在目录里（SAVE 期 `reference-exists`），本迁移不动它。
-- 承载方式：新增服务处理器 **`cop-prepay-rollup`**（两张表 + 各列名 + 舍入位数，全部校验为物理列）。
-- 幂等：动作按 模块+SAVE+SEQ 合并；快照族名仅当仍含 `cop-prepay` 时改写。
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

DECLARE @ModuleId INT = 170103;
DECLARE @Family NVARCHAR(40) = N'cop-prepay';

IF NOT EXISTS (SELECT 1 FROM dbo.MODULES m WHERE m.M_IDX = @ModuleId
                 AND LTRIM(RTRIM(ISNULL(m.MASTER_TABLE, ''))) = N'COP_PREPAY_M'
                 AND LTRIM(RTRIM(ISNULL(m.DETAIL_TABLE, ''))) = N'COP_PREPAY_D')
    THROW 50001, N'模块 170103 形态不符（应为 COP_PREPAY_M / COP_PREPAY_D），迁移中止。', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.MODULE_VALIDATION_RULE
               WHERE MODULE_ID = @ModuleId AND STAGE = N'SAVE' AND VALIDATION_KEY = N'reference-exists' AND ENABLED = 1)
    THROW 50002, N'模块 170103 缺少既有的 SAVE 期引用校验，迁移中止（先核对配置）。', 1;

IF EXISTS (SELECT 1 FROM dbo.MODULE_BUSINESS_ACTION WHERE MODULE_ID = @ModuleId AND EVENT_CODE = N'SAVE')
    THROW 50003, N'模块 170103 已有 SAVE 期业务动作配置，迁移中止（先核对配置）。', 1;

DECLARE @EffectParam NVARCHAR(MAX) =
    N'{"master":{"typeField":"PREPAY_TYPE","noField":"PREPAY_NO","amountField":"AMOUNT"},'
    + N'"detail":{"table":"COP_PREPAY_D","amountField":"AMOUNT"},"roundDigits":3}';

MERGE dbo.MODULE_BUSINESS_ACTION AS T
USING (SELECT @ModuleId AS MODULE_ID) AS S
   ON T.MODULE_ID = S.MODULE_ID AND T.EVENT_CODE = N'SAVE' AND T.SEQ = 1
WHEN MATCHED THEN
    UPDATE SET T.EFFECT_KEY = N'cop-prepay-rollup', T.EFFECT_NAME = N'预收帐款单金额汇总（保存期）',
               T.ENABLED = 1, T.FAIL_MODE = N'BLOCK', T.PARAM_STRUCT = @EffectParam,
               T.REVERSE_STRUCT = N'{"kind":"none"}',
               T.REMARK = N'原 C# 预收帐款单保存后动作的忠实移植（主表金额＝明细金额合计，舍入三位）',
               T.LAST_UPDATE_BY = N'DbUp', T.LAST_UPDATE_DATE = SYSDATETIME()
WHEN NOT MATCHED THEN
    INSERT (MODULE_ID, EVENT_CODE, SEQ, EFFECT_KEY, EFFECT_NAME, ENABLED, FAIL_MODE, CONDITION_STRUCT,
            PARAM_STRUCT, REVERSE_STRUCT, REMARK, SOURCE_REF, CREATE_PERSON, CREATE_DATE, LAST_UPDATE_BY, LAST_UPDATE_DATE)
    VALUES (S.MODULE_ID, N'SAVE', 1, N'cop-prepay-rollup', N'预收帐款单金额汇总（保存期）', 1, N'BLOCK', NULL,
            @EffectParam, N'{"kind":"none"}',
            N'原 C# 预收帐款单保存后动作的忠实移植（主表金额＝明细金额合计，舍入三位）', @Family,
            N'DbUp', SYSDATETIME(), N'DbUp', SYSDATETIME());

UPDATE S
   SET S.DEFINITION_JSON = REPLACE(S.DEFINITION_JSON, N'"DomainRule":"' + @Family + N'"', N'"DomainRule":null')
 FROM dbo.WORKBENCH_DEFINITION_SNAPSHOT S
WHERE S.MODULE_ID = @ModuleId AND S.IS_CURRENT = 1
  AND S.DEFINITION_JSON LIKE N'%"DomainRule":"' + @Family + N'"%';

IF EXISTS (SELECT 1 FROM dbo.WORKBENCH_DEFINITION_SNAPSHOT S
           WHERE S.MODULE_ID = @ModuleId AND S.IS_CURRENT = 1
             AND S.DEFINITION_JSON LIKE N'%"DomainRule":"' + @Family + N'"%')
    THROW 50004, N'当前快照仍残留已退役的族名，迁移中止。', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.MODULE_BUSINESS_ACTION
               WHERE MODULE_ID = @ModuleId AND EVENT_CODE = N'SAVE' AND SEQ = 1
                 AND EFFECT_KEY = N'cop-prepay-rollup' AND ENABLED = 1)
    THROW 50005, N'退役 C# 后缺少启用的 SAVE 期 cop-prepay-rollup 动作，迁移中止。', 1;

PRINT N'== cop-prepay 入效果目录并退役 C# 完成（170103，SAVE 期 cop-prepay-rollup）==';
