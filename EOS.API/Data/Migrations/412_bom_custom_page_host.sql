-- ============================================================================
-- EOS.ERP migration 412: 模块 1204 的承载面改为定制页 /bom/product（ADR-039 D1）
-- ----------------------------------------------------------------------------
-- 改的是什么：一条 M_URL。但它同时改变 NODE_KIND（WORKBENCH → CUSTOMPAGE），
-- 因为形态就是由"承载页 + 主表"推出来的（dbo.V_MODULE_NODE 实时算，不落库）。
--
-- 为什么能力位必须保持为 1：
--   定制页只接管**列表**，单层维护仍走 `/workbench/1204/view/...`（复用统一表单、
--   写入管线、权限、审计、效果链）。所以 UNIFIED_FORM_ENABLED / UNIFIED_FORM_EDIT_ENABLED
--   必须留 1——它们现在是"有没有单据面"的唯一判据（ADR-039 D2），形态不再管这件事。
--   若被清成 0，列表能打开、双击行却打不开维护面。
--
-- 为什么 MODULE_FORM_LAYOUT 的 46 行**不删**：
--   单层维护仍由统一表单渲染，版式仍在生效。这与"全定制后版式作废"的设想不同——
--   本次是渐进式定制（列表先换壳，维护面后续再搬），ADR-039 第 11 节据此改为
--   "维护面搬走之前，版式对 1204 仍然有效"。
--
-- 前提（迁移 408/409 已落）：
--   · 主键变成 (PRO_NO, EDITION)，所以列表页拼深链要用两段键；
--   · 版次两位数字且只读，定制页的"树/反查"按版次查询才对得上。
--
-- 幂等：M_URL 已是目标值时不再更新；标脏用 MERGE。
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
    THROW 57800, @GUARD_MESSAGE, 1;

DECLARE @ModuleId INT = 1204;
DECLARE @Host NVARCHAR(100) = N'/bom/product';
DECLARE @Mig NVARCHAR(100) = N'DbUp';

/* ---------------------------------------------------------------- 前置断言 */
IF NOT EXISTS (SELECT 1 FROM dbo.MODULES
               WHERE M_IDX = @ModuleId
                 AND LTRIM(RTRIM(ISNULL(MASTER_TABLE, N''))) = N'BOM_STRU_M'
                 AND LTRIM(RTRIM(ISNULL(DETAIL_TABLE, N''))) = N'BOM_STRU_D')
    THROW 57801, N'模块 1204 形态不符（应为 BOM_STRU_M / BOM_STRU_D），迁移中止。', 1;

/* 能力位必须是 1：它是"定制页有没有单据面"的唯一判据（ADR-039 D2）。
   若为 0，改完 M_URL 后列表能开、维护面打不开，属于静默功能缺失。 */
IF NOT EXISTS (SELECT 1 FROM dbo.MODULES WHERE M_IDX = @ModuleId AND ISNULL(UNIFIED_FORM_ENABLED, 0) = 1)
    THROW 57802, N'模块 1204 未启用统一表单能力；定制页将无法打开单层维护，迁移中止。', 1;

/* 版次维度必须已就位（迁移 409）：列表页按 (PRO_NO, EDITION) 拼深链。 */
IF NOT EXISTS (SELECT 1 FROM sys.columns
               WHERE object_id = OBJECT_ID(N'dbo.BOM_STRU_M') AND name = N'EDITION' AND is_nullable = 0)
    THROW 57803, N'BOM_STRU_M.EDITION 仍可空——迁移 409 未执行，本迁移不能先落，中止。', 1;

BEGIN TRANSACTION;

UPDATE dbo.MODULES
   SET M_URL = @Host,
       REMARK = N'主表：BOM_STRU_M；明细表：BOM_STRU_D；主键 (PRO_NO, EDITION)，一个料号可有多版 BOM；'
              + N'承载面为定制页 /bom/product（列表自绘），单层维护仍走统一表单；保存期四条校验与写动作由效果引擎承载',
       LAST_UPDATE_BY = @Mig,
       LAST_UPDATE_DATE = SYSDATETIME()
 WHERE M_IDX = @ModuleId
   AND ISNULL(M_URL, N'') <> @Host;
PRINT N'[412] MODULES.M_URL → /bom/product：' + CAST(@@ROWCOUNT AS NVARCHAR(10)) + N' 行';

/* 形态变更会让已发布快照里的"模块级事实"与当前配置不一致，标脏等发布 */
MERGE dbo.WORKBENCH_MODULE_DIRTY AS T
USING (SELECT @ModuleId AS M_IDX) AS S
   ON T.M_IDX = S.M_IDX
WHEN MATCHED THEN
    UPDATE SET T.DIRTY_TAG = 1, T.LAST_MODIFIED_BY = @Mig, T.LAST_MODIFIED_AT = SYSDATETIME()
WHEN NOT MATCHED THEN
    INSERT (M_IDX, DIRTY_TAG, LAST_MODIFIED_BY, LAST_MODIFIED_AT)
    VALUES (S.M_IDX, 1, @Mig, SYSDATETIME());
PRINT N'[412] 模块 1204 已标脏（形态变更，须重新发布）';

/* ---------------------------------------------------------------- 收口断言 */
IF NOT EXISTS (SELECT 1 FROM dbo.MODULES WHERE M_IDX = @ModuleId AND LTRIM(RTRIM(M_URL)) = @Host)
    THROW 57810, N'M_URL 未改为 /bom/product，迁移中止。', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.MODULES
               WHERE M_IDX = @ModuleId
                 AND ISNULL(UNIFIED_FORM_ENABLED, 0) = 1
                 AND ISNULL(UNIFIED_FORM_EDIT_ENABLED, 0) = 1)
    THROW 57811, N'统一表单能力在改承载页的过程中被清掉了，维护面将打不开，迁移中止。', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.WORKBENCH_MODULE_DIRTY WHERE M_IDX = @ModuleId AND DIRTY_TAG = 1)
    THROW 57812, N'模块 1204 未标脏，迁移中止。', 1;

COMMIT TRANSACTION;

PRINT N'== 1204 承载面已切换为定制页 /bom/product：列表自绘，单层维护仍走统一表单，版式保留 ==';
