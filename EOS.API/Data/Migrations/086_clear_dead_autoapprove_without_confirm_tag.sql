-- ============================================================================
-- EOS.ERP migration 087: 清理无批核能力模块的死 AUTO_APPROVE 标记
-- ----------------------------------------------------------------------------
-- （能力 → 列单向强制）：AUTO_APPROVE=1 要求主表有 CONFIRM_TAG。
-- 存量中一批模块的 AUTO_APPROVE=1 属历史遗留死标记——主表无 CONFIRM_TAG，
-- 且无任何批核能力（UPDATE_SP 为空、无 WFFORM 流程、无启用的批核/解批效果链）：
-- 此前保存路径对此静默返回成功（调用方误以为已批核），现改为显式失败。
-- 本迁移把这类死标记置 0（档案型模块显式声明无生命周期能力），使保存语义诚实；
-- 有批核能力的模块不在此列（缺列由发布期 lifecycle_columns 门拦截并引导补列）。
--
-- 幂等：仅当取值与目标不一致且满足全部守卫时更新；可逆：重新置 1 即可恢复。
-- 生效：MODULES.AUTO_APPROVE 随模块发布进入 Definition 快照，需重发布后生效。
-- ============================================================================

SET NOCOUNT ON;

DECLARE @GUARD_MESSAGE NVARCHAR(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, @GUARD_MESSAGE, 1;

PRINT N'== 开始清理死 AUTO_APPROVE 标记 ==';

UPDATE dbo.MODULES
SET AUTO_APPROVE = 0,
    LAST_UPDATE_BY = N'migration-087',
    LAST_UPDATE_DATE = SYSDATETIME()
WHERE COALESCE(AUTO_APPROVE, 0) = 1
  AND NULLIF(LTRIM(RTRIM(ISNULL(MASTER_TABLE, N''))), N'') IS NOT NULL
  AND NOT EXISTS (
      SELECT 1 FROM sys.columns c
      JOIN sys.objects o ON c.object_id = o.object_id AND o.type IN ('U', 'V')
      JOIN sys.schemas s ON o.schema_id = s.schema_id
      WHERE s.name = N'dbo' AND o.name = MODULES.MASTER_TABLE AND c.name = N'CONFIRM_TAG')
  AND LTRIM(RTRIM(ISNULL(UPDATE_SP, N''))) = N''
  AND NOT EXISTS (SELECT 1 FROM dbo.WFFORM w WITH (NOLOCK) WHERE w.WF_M_IDX = MODULES.M_IDX)
  AND NOT EXISTS (
      SELECT 1 FROM dbo.MODULE_BUSINESS_ACTION a WITH (NOLOCK)
      WHERE a.MODULE_ID = MODULES.M_IDX AND a.ENABLED = 1
        AND a.EVENT_CODE IN ('APPROVE_EFFECT', 'DEAPPROVE'));

PRINT N'-- 清理后分布 --';
SELECT CONCAT(N'  AUTO_APPROVE=1 且主表无 CONFIRM_TAG 的模块数=', COUNT(*)) AS SUMMARY
FROM dbo.MODULES m
WHERE COALESCE(m.AUTO_APPROVE, 0) = 1
  AND NULLIF(LTRIM(RTRIM(ISNULL(m.MASTER_TABLE, N''))), N'') IS NOT NULL
  AND NOT EXISTS (
      SELECT 1 FROM sys.columns c
      JOIN sys.objects o ON c.object_id = o.object_id AND o.type IN ('U', 'V')
      JOIN sys.schemas s ON o.schema_id = s.schema_id
      WHERE s.name = N'dbo' AND o.name = m.MASTER_TABLE AND c.name = N'CONFIRM_TAG');

PRINT N'== 死 AUTO_APPROVE 标记清理完成 ==';
