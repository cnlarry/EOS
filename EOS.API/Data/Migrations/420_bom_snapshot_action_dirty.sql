-- ============================================================
-- EOS.ERP migration 420: 制令单的当前快照缺产品 BOM 快照动作时，把它标脏
-- ------------------------------------------------------------
-- 410 给 1502 的 SAVE 链挂了 bom-snapshot-to-order，但**快照是运行期真源**：
-- 已经发布过 1502 的库会继续按旧快照执行，新动作不生效，而脏标记为空又让
-- "待发布"清单看不见它——这是静默不执行的典型形态。
--
-- 只在该模块**有当前快照、且快照里没有该动作**时标脏：
--   · 已重新发布过的库：条件不成立，不加脏标记（下次启动也不会误标）；
--   · 没有快照的库（定义每请求实时装配，配置表即真值）：同样不标。
-- ============================================================
SET NOCOUNT ON;
SET XACT_ABORT ON;
GO

DECLARE @ModuleId INT = 1502;
DECLARE @Mig NVARCHAR(100) = N'DbUp';
DECLARE @EffectKey NVARCHAR(100) = N'bom-snapshot-to-order';

IF NOT EXISTS (SELECT 1 FROM dbo.MODULES WHERE M_IDX = @ModuleId)
    THROW 57801, N'模块 1502 不存在，迁移中止。', 1;

IF EXISTS (SELECT 1 FROM dbo.WORKBENCH_DEFINITION_SNAPSHOT s
           WHERE s.M_IDX = @ModuleId AND s.IS_CURRENT = 1
             AND CHARINDEX(@EffectKey, CAST(s.DEFINITION_JSON AS NVARCHAR(MAX))) = 0)
BEGIN
    MERGE dbo.WORKBENCH_MODULE_DIRTY AS t
    USING (SELECT @ModuleId AS M_IDX) AS s ON t.M_IDX = s.M_IDX
    WHEN MATCHED THEN
        UPDATE SET DIRTY_TAG = 1, LAST_MODIFIED_BY = @Mig, LAST_MODIFIED_AT = SYSDATETIME()
    WHEN NOT MATCHED THEN
        INSERT (M_IDX, DIRTY_TAG, LAST_MODIFIED_BY, LAST_MODIFIED_AT)
        VALUES (s.M_IDX, 1, @Mig, SYSDATETIME());
    PRINT N'[420] 1502 的当前快照未含 bom-snapshot-to-order，已标脏待发布。';
END
ELSE
    PRINT N'[420] 1502 无需标脏（无当前快照，或快照已含该动作）。';
GO
