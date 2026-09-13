-- ============================================================================
-- EOS.ERP migration 073: 商编变更单（1202）菜单节点隐藏
-- ----------------------------------------------------------------------------
-- 该模块当前没有可执行的批核副作用实现：BOM_REDEPLOY_M/D 无任何数据，其批核过程
-- 在本库与历史库中均不存在，且仓库内无该过程源码可考古。为避免用户进入一个批核必然
-- 报错的单据页，先隐藏菜单入口。
-- 处理方式与既有"页面退役"迁移一致，保留模块本身与权限单元（SYSDD/SYSDH 不动），
-- M_URL 保留为 /workbench，需要恢复时把 M_TAG 置回 1 并重新发布快照即可。
-- 已发布 Definition 快照与脏标记做防御清理（模块不可见时不再占用运行时缓存）。
-- 幂等：以 ISNULL(M_TAG,1)=1 为条件，重复执行为空操作。
-- ============================================================================

SET NOCOUNT ON;

/* WORKBENCH_DEFINITION_SNAPSHOT 上有筛选唯一索引，操作前确保 SET 选项正确。 */
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET ARITHABORT ON;

DECLARE @GUARD_MESSAGE NVARCHAR(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, @GUARD_MESSAGE, 1;

BEGIN TRANSACTION;

UPDATE dbo.MODULES
   SET M_TAG = 0,
       REMARK = LTRIM(RTRIM(ISNULL(REMARK, N'')))
                + N'；菜单入口隐藏：该单据无可用批核实现（明细表无数据、批核过程不存在且无源码可考古），保留模块与权限单元以便恢复',
       LAST_UPDATE_BY = N'DbUp',
       LAST_UPDATE_DATE = GETDATE()
 WHERE M_IDX = 1202
   AND ISNULL(M_TAG, 1) = 1;

DELETE FROM dbo.WORKBENCH_MODULE_DIRTY WHERE MODULE_ID = 1202;
DELETE FROM dbo.WORKBENCH_DEFINITION_SNAPSHOT WHERE MODULE_ID = 1202;

COMMIT TRANSACTION;

PRINT N'== 商编变更单（1202）菜单入口已隐藏，快照与脏标记清理完成 ==';
