/* ============================================================================
   一次性｜锚点：迁移 436（框架表改名后重建快照基线）
   ----------------------------------------------------------------------------
   用途：在 SSMS 里手工清空工作台快照版本历史与操作审计，随后用 backfill 端点
         全量重发布，使每个模块的版本号从 1 重新累积。

   前置：已导出快照备份（scripts/oneshot/rebuild-workbench-snapshots.ps1 -WhatIf
         会把当前 274 条快照写成 JSON）。审计数据按"当前阶段无关痛痒"处理，不备份。

   执行后必须做：调一次 POST /api/v1/workbench-definitions/backfill。
         只清空不发布时，库里没有当前快照，运行期回落实时装配——功能可用，但没有
         冻结基线，且管理界面看不到"待发布"（脏标记也一起清了）。

   说明：
     · AUDIT_FIELD_CHANGE 有 FK 指向 AUDIT_EVENT（NO_ACTION），所以父表只能 DELETE，
       不能 TRUNCATE；子表用 TRUNCATE 更快且自动重置 identity。
     · WORKBENCH_DEFINITION_SNAPSHOT 与 WORKBENCH_MODULE_DIRTY 无外键引用，用 TRUNCATE。
     · 周期为开发环境的一次性重置，不写进迁移：迁移会在每个新库上重演。
   ============================================================================ */

SET NOCOUNT ON;
SET XACT_ABORT ON;
GO

IF DB_NAME() <> N'EOS.ERP'
BEGIN
    THROW 50000, N'本脚本只能在 EOS.ERP 数据库内执行。', 1;
END
GO

/* ---------------------------------------------------------------- 可选备份
   需要留一份可回滚副本时取消注释。表名带 _BAK 会被 export-bootstrap-schema.ps1
   的排除规则跳过，不会进建库基线；确认不再需要后手工 DROP。

SELECT * INTO dbo.WORKBENCH_DEFINITION_SNAPSHOT_BAK FROM dbo.WORKBENCH_DEFINITION_SNAPSHOT;
SELECT * INTO dbo.AUDIT_EVENT_BAK FROM dbo.AUDIT_EVENT;
SELECT * INTO dbo.AUDIT_FIELD_CHANGE_BAK FROM dbo.AUDIT_FIELD_CHANGE;
*/

/* ------------------------------------------------------------ 执行前计数 */
SELECT 'AUDIT_EVENT' AS OBJECT_NAME, COUNT(*) AS ROWS_BEFORE FROM dbo.AUDIT_EVENT
UNION ALL
SELECT 'AUDIT_FIELD_CHANGE', COUNT(*) FROM dbo.AUDIT_FIELD_CHANGE
UNION ALL
SELECT 'WORKBENCH_DEFINITION_SNAPSHOT', COUNT(*) FROM dbo.WORKBENCH_DEFINITION_SNAPSHOT
UNION ALL
SELECT 'WORKBENCH_MODULE_DIRTY', COUNT(*) FROM dbo.WORKBENCH_MODULE_DIRTY;
GO

/* ---------------------------------------------------------------- 清空 */
BEGIN TRANSACTION;

TRUNCATE TABLE dbo.AUDIT_FIELD_CHANGE;

DELETE FROM dbo.AUDIT_EVENT;
DBCC CHECKIDENT ('dbo.AUDIT_EVENT', RESEED, 0) WITH NO_INFOMSGS;

TRUNCATE TABLE dbo.WORKBENCH_DEFINITION_SNAPSHOT;
TRUNCATE TABLE dbo.WORKBENCH_MODULE_DIRTY;

COMMIT TRANSACTION;
GO

/* ------------------------------------------------------------ 执行后核对 */
SELECT 'AUDIT_EVENT' AS OBJECT_NAME, COUNT(*) AS ROWS_AFTER FROM dbo.AUDIT_EVENT
UNION ALL
SELECT 'AUDIT_FIELD_CHANGE', COUNT(*) FROM dbo.AUDIT_FIELD_CHANGE
UNION ALL
SELECT 'WORKBENCH_DEFINITION_SNAPSHOT', COUNT(*) FROM dbo.WORKBENCH_DEFINITION_SNAPSHOT
UNION ALL
SELECT 'WORKBENCH_MODULE_DIRTY', COUNT(*) FROM dbo.WORKBENCH_MODULE_DIRTY;

SELECT 'AUDIT_EVENT' AS SEQ_NAME, IDENT_CURRENT('dbo.AUDIT_EVENT') AS CURRENT_VALUE
UNION ALL
SELECT 'WORKBENCH_DEFINITION_SNAPSHOT', IDENT_CURRENT('dbo.WORKBENCH_DEFINITION_SNAPSHOT');
GO
