-- ============================================================================
-- EOS.ERP migration 120: 2913 状态置位动作前置（满足效果链顺序 lint）
-- ----------------------------------------------------------------------------
-- 背景：2913 模具耗料单的效果链原为 SEQ1 `inventory-move`（库存异动）+ SEQ2
--   `set-state`（申请单/批次单结案盖章）。方案 A 把 SEQ2 下沉为 `field-accumulate`
--   后，配置校验的顺序 lint（`ModuleBusinessConfigValidator.ChainPrerequisites`：
--   `inventory-move` 依赖同链前置 `field-accumulate`）判定"依赖效果排在异动之后"而不放行。
-- 事实：该 `field-accumulate` 只放常量（FINISHED_TAG / FINISHED_PERSON / FINISHED_DATE），
--   写的是 MOU_APPLY_M / MOU_BATCH_M 的状态列；`inventory-move` 写的是库存流水与余额，
--   两动作之间没有数据依赖，先后不影响结果。
-- 处置：把置位动作放到 SEQ=1、库存异动放到 SEQ=2，保持 lint 不放宽。
-- 幂等：已是目标顺序则不动。
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

DECLARE @StateSeq INT, @MoveSeq INT;
SELECT @StateSeq = MAX(CASE WHEN A.EFFECT_KEY = N'field-accumulate' THEN A.SEQ END),
       @MoveSeq  = MAX(CASE WHEN A.EFFECT_KEY = N'inventory-move' THEN A.SEQ END)
FROM dbo.MODULE_BUSINESS_ACTION A
WHERE A.MODULE_ID = 2913 AND A.EVENT_CODE = N'APPROVE_EFFECT';

IF @StateSeq IS NULL OR @MoveSeq IS NULL
    THROW 50001, N'2913 的 APPROVE_EFFECT 链不是"库存异动 + 公式置位"两动作，迁移中止（先核对配置）。', 1;

IF @StateSeq < @MoveSeq
    PRINT N'2913 顺序已符合（置位在前），无需调整。';
ELSE
BEGIN
    /* 先挪到临时序号，避开 (模块,事件,SEQ) 唯一约束 */
    UPDATE dbo.MODULE_BUSINESS_ACTION SET SEQ = 9000
     WHERE MODULE_ID = 2913 AND EVENT_CODE = N'APPROVE_EFFECT' AND SEQ = @StateSeq;
    UPDATE dbo.MODULE_BUSINESS_ACTION SET SEQ = @StateSeq
     WHERE MODULE_ID = 2913 AND EVENT_CODE = N'APPROVE_EFFECT' AND SEQ = @MoveSeq;
    UPDATE dbo.MODULE_BUSINESS_ACTION SET SEQ = @MoveSeq
     WHERE MODULE_ID = 2913 AND EVENT_CODE = N'APPROVE_EFFECT' AND SEQ = 9000;
    PRINT N'2913 已调整：公式置位 SEQ=1，库存异动 SEQ=2。';
END

/* 守卫：置位动作必须早于异动动作，且两动作的公式行/键未被改动 */
IF (SELECT MAX(CASE WHEN A.EFFECT_KEY = N'field-accumulate' THEN A.SEQ END)
    FROM dbo.MODULE_BUSINESS_ACTION A
    WHERE A.MODULE_ID = 2913 AND A.EVENT_CODE = N'APPROVE_EFFECT')
   > (SELECT MAX(CASE WHEN A.EFFECT_KEY = N'inventory-move' THEN A.SEQ END)
      FROM dbo.MODULE_BUSINESS_ACTION A
      WHERE A.MODULE_ID = 2913 AND A.EVENT_CODE = N'APPROVE_EFFECT')
    THROW 50002, N'调整后置位动作仍未前置于异动动作，迁移中止。', 1;
