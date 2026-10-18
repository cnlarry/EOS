-- ============================================================================
-- EOS.ERP migration 415: 清掉 1204 的历史定义快照，定制页改走实时装配
-- ----------------------------------------------------------------------------
-- 1204 现在是定制页（CUSTOMPAGE）：界面自绘、业务逻辑自持，**不参与发布流程**
-- （230101 对该形态本来就不提供发布入口）。但它当初作为工作台模块时发布过 27 个
-- 版本，最后一份 IS_CURRENT=1 仍在库里。
--
-- 为什么不删会坏：`WorkbenchDefinitionBuilder` 的判据是"**有快照就一定用快照**"，
-- 脏标记不参与（它是待发布清单的输入，不是运行期回退开关）。那份快照里的
-- `MasterPkOrder` 还是单列 `PRO_NO`，而迁移 409 已把主键改成 `(PRO_NO, EDITION)`。
-- 结果是定制页按旧主键拼深链，打开记录时主键列数与值数量不符，整条记录打不开。
--
-- 只删 IS_CURRENT=1 的当前快照，历史版本保留作审计。删掉后
-- `WorkbenchDefinitionProvider.TryGetBaseline` 取不到基线，装配自动落到
-- `BuildFromMetadataAsync`（每请求实时读 MODULES/FIELDS/版式），这正是定制页要的语义。
--
-- 顺带清掉脏标记：没有快照就没有"待发布"这回事，留着只会永久挂在待发布清单里。
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
    THROW 58100, @GUARD_MESSAGE, 1;

DECLARE @ModuleId INT = 1204;
DECLARE @Mig NVARCHAR(100) = N'DbUp';

/* ---------------------------------------------------------------- 前置 */
/* 必须是定制页：如果 1204 还是 WORKBENCH，删快照等于让它退回"每次实时装配"，
   那是另一个决定，不该由本迁移替代。 */
IF NOT EXISTS (SELECT 1 FROM dbo.V_MODULE_NODE WHERE M_IDX = @ModuleId AND NODE_KIND = N'CUSTOMPAGE')
    THROW 58101, N'模块 1204 当前不是 CUSTOMPAGE 形态（迁移 412 未执行？），本迁移中止。', 1;

IF OBJECT_ID(N'dbo.WORKBENCH_DEFINITION_SNAPSHOT', N'U') IS NULL
    THROW 58102, N'WORKBENCH_DEFINITION_SNAPSHOT 表不存在，迁移中止。', 1;

BEGIN TRANSACTION;

DELETE FROM dbo.WORKBENCH_DEFINITION_SNAPSHOT
 WHERE M_IDX = @ModuleId AND IS_CURRENT = 1;
PRINT N'[415] 删除 1204 的当前快照：' + CAST(@@ROWCOUNT AS NVARCHAR(10)) + N' 行（历史版本保留）';

DELETE FROM dbo.WORKBENCH_MODULE_DIRTY WHERE M_IDX = @ModuleId;
PRINT N'[415] 清除 1204 的待发布标记：' + CAST(@@ROWCOUNT AS NVARCHAR(10)) + N' 行';

/* ---------------------------------------------------------------- 收口 */
IF EXISTS (SELECT 1 FROM dbo.WORKBENCH_DEFINITION_SNAPSHOT WHERE M_IDX = @ModuleId AND IS_CURRENT = 1)
    THROW 58110, N'1204 仍有当前快照，运行期会继续用旧定义（主键序是旧的），迁移中止。', 1;

IF EXISTS (SELECT 1 FROM dbo.WORKBENCH_MODULE_DIRTY WHERE M_IDX = @ModuleId)
    THROW 58111, N'1204 仍有待发布标记，迁移中止。', 1;

COMMIT TRANSACTION;

PRINT N'== 1204 已脱离发布流程：无当前快照、无待发布标记，定义每请求实时装配 ==';
