-- ============================================================================
-- EOS.ERP migration 410: 制令单保存时把产品 BOM 快照成工单 BOM（ADR-039 D7）
-- ----------------------------------------------------------------------------
-- 产品 BOM（1204）是标准，工单 BOM（1506）是「这一单实际用什么」。
-- 本迁移把两者接上：制令单（1502）首次保存时，把该产品**当前版次**的完整 BOM
-- 展开写进工单 BOM；此后工单 BOM 允许人工调整，不再回写产品 BOM。
--
-- 为什么挂在 1502 的 SAVE：
--   · 「计算用料」与「展开子制令」都在 1502 上操作，工单 BOM 是它们的共同输入；
--   · 建单那一刻快照一次，后续所有动作看到的都是同一份结构，不会中途换版。
--
-- 为什么 FAIL_MODE=BLOCK 却不阻断"产品没 BOM"的场景：
--   · 处理器在"产品没有维护 BOM"时返回 0 而不是抛异常——先下制令、后补 BOM
--     是常见流程，为此阻断保存会把人卡死；
--   · BLOCK 拦的是"该有快照却写不进去"（表/列配置错、SQL 失败），那种情况
--     说明系统状态不一致，必须暴露而不是静默跳过。
--
-- 幂等：动作按 模块+SAVE+SEQ 合并；已存在同键动作时只更新参数与开关。
-- 收口断言校验动作已启用、参数可被物理列校验器解析、1506 两张表存在。
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
    THROW 57600, @GUARD_MESSAGE, 1;

DECLARE @ModuleId INT = 1502;          -- 制令单
DECLARE @OrderBomModuleId INT = 1506;  -- 工单 B.O.M
DECLARE @Seq INT = 2;                  -- SAVE 期 seq=1 已被 link-stamp 占用
DECLARE @Mig NVARCHAR(100) = N'DbUp';

/* ---------------------------------------------------------------- 前置 */
IF NOT EXISTS (SELECT 1 FROM dbo.MODULES m WHERE m.M_IDX = @ModuleId
                 AND LTRIM(RTRIM(ISNULL(m.MASTER_TABLE, N''))) = N'MOC_PRODUCE_M')
    THROW 57601, N'模块 1502 形态不符（应为 MOC_PRODUCE_M 主表），迁移中止。', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.MODULES m WHERE m.M_IDX = @OrderBomModuleId
                 AND LTRIM(RTRIM(ISNULL(m.MASTER_TABLE, N''))) = N'MOC_BOM_STRU_M'
                 AND LTRIM(RTRIM(ISNULL(m.DETAIL_TABLE, N''))) = N'MOC_BOM_STRU_D')
    THROW 57602, N'模块 1506 形态不符（应为 MOC_BOM_STRU_M / MOC_BOM_STRU_D），迁移中止。', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.MODULE_BUSINESS_ACTION
               WHERE M_IDX = @ModuleId AND EVENT_CODE = N'SAVE' AND SEQ = 1
                 AND EFFECT_KEY = N'link-stamp' AND ENABLED = 1)
    THROW 57603, N'1502 的 SAVE 期 seq=1（link-stamp）不在，口径已变，迁移中止。', 1;

IF OBJECT_ID(N'dbo.BOM_STRU_M', N'U') IS NULL OR OBJECT_ID(N'dbo.BOM_STRU_D', N'U') IS NULL
    THROW 57604, N'产品 BOM 表不存在，迁移中止（D7 依赖迁移 409 的版次维度）。', 1;

/* 版次维度必须先就位：没有 EDITION 列的 NOT NULL / 两位数字约束，快照的选版会落到空值上。 */
IF NOT EXISTS (SELECT 1 FROM sys.columns
               WHERE object_id = OBJECT_ID(N'dbo.BOM_STRU_M') AND name = N'EDITION' AND is_nullable = 0)
    THROW 57605, N'BOM_STRU_M.EDITION 仍可空——迁移 409 未执行，本迁移不能先落，中止。', 1;

/* ---------------------------------------------------------------- 动作配置 */
BEGIN TRANSACTION;

DECLARE @Params NVARCHAR(MAX) =
    N'{"orderProductField":"PRO_NO","orderEditionField":"EDITION",'
    + N'"targetMasterTable":"MOC_BOM_STRU_M","targetDetailTable":"MOC_BOM_STRU_D",'
    + N'"sourceMasterTable":"BOM_STRU_M","sourceDetailTable":"BOM_STRU_D",'
    + N'"maxDepth":20}';

MERGE dbo.MODULE_BUSINESS_ACTION AS T
USING (SELECT @ModuleId AS M_IDX) AS S
   ON T.M_IDX = S.M_IDX AND T.EVENT_CODE = N'SAVE' AND T.SEQ = @Seq
WHEN MATCHED THEN
    UPDATE SET T.EFFECT_KEY = N'bom-snapshot-to-order',
               T.EFFECT_NAME = N'产品BOM快照到工单BOM（保存期，仅首次）',
               T.ENABLED = 1, T.FAIL_MODE = N'BLOCK', T.PARAM_STRUCT = @Params,
               T.REVERSE_STRUCT = N'{"kind":"none"}',
               T.REMARK = N'制令单首次保存时按产品当前版次展开并写入工单 BOM；已有工单 BOM 则跳过',
               T.LAST_UPDATE_BY = @Mig, T.LAST_UPDATE_DATE = SYSDATETIME()
WHEN NOT MATCHED THEN
    INSERT (M_IDX, EVENT_CODE, SEQ, EFFECT_KEY, EFFECT_NAME, ENABLED, FAIL_MODE, CONDITION_STRUCT,
            PARAM_STRUCT, REVERSE_STRUCT, REMARK, SOURCE_REF, CREATE_PERSON, CREATE_DATE, LAST_UPDATE_BY, LAST_UPDATE_DATE)
    VALUES (S.M_IDX, N'SAVE', @Seq, N'bom-snapshot-to-order', N'产品BOM快照到工单BOM（保存期，仅首次）',
            1, N'BLOCK', NULL, @Params, N'{"kind":"none"}',
            N'制令单首次保存时按产品当前版次展开并写入工单 BOM；已有工单 BOM 则跳过',
            N'bom-order-snapshot', @Mig, SYSDATETIME(), @Mig, SYSDATETIME());
PRINT N'[410] 1502 SAVE 期快照动作已配置（seq=2，bom-snapshot-to-order）';

/* 模块备注补一句数据来源，避免下一个人以为工单 BOM 只能手工维护 */
UPDATE dbo.MODULES
   SET REMARK = N'主表：MOC_BOM_STRU_M；明细表：MOC_BOM_STRU_D；首次随制令单保存由产品 BOM 快照生成，此后可人工调整',
       LAST_UPDATE_BY = @Mig,
       LAST_UPDATE_DATE = SYSDATETIME()
 WHERE M_IDX = @OrderBomModuleId
   AND ISNULL(REMARK, N'') NOT LIKE N'%由产品 BOM 快照生成%';
PRINT N'[410] 1506 模块备注已注明数据来源';

/* ---------------------------------------------------------------- 收口断言 */
IF NOT EXISTS (SELECT 1 FROM dbo.MODULE_BUSINESS_ACTION
               WHERE M_IDX = @ModuleId AND EVENT_CODE = N'SAVE' AND SEQ = @Seq
                 AND EFFECT_KEY = N'bom-snapshot-to-order' AND ENABLED = 1 AND FAIL_MODE = N'BLOCK')
    THROW 57610, N'快照动作未启用，迁移中止。', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.MODULE_BUSINESS_ACTION
               WHERE M_IDX = @ModuleId AND EVENT_CODE = N'SAVE' AND SEQ = 1
                 AND EFFECT_KEY = N'link-stamp' AND ENABLED = 1)
    THROW 57611, N'原有的 link-stamp 动作被挤掉了，迁移中止。', 1;

IF EXISTS (SELECT 1 FROM dbo.MODULE_BUSINESS_ACTION
           WHERE M_IDX = @ModuleId AND EVENT_CODE = N'SAVE' AND SEQ = @Seq
             AND ISNULL(PARAM_STRUCT, N'') = N'')
    THROW 57612, N'快照动作缺少参数，迁移中止。', 1;

COMMIT TRANSACTION;

PRINT N'== 产品 BOM → 工单 BOM 快照已接线（1502 SAVE seq=2；仅首次，已有工单 BOM 则跳过）==';
