-- ============================================================================
-- EOS.ERP migration 414: 撤销 1204 的「新建版本」MANUAL 按钮，改为定制页端点
-- ----------------------------------------------------------------------------
-- 413 把「新建版本」配成了 MANUAL 动作。实施后发现这是**用错了机制**：
--
--   · MANUAL 动作（ADR-018）是为"标准单据面"设计的——同一张单要给不同客户配不同
--     按钮组合、调顺序、按角色细分授权（fail-closed、需单独授予）。
--   · 1204 已经是定制页。它的动作本来就该由**代码 + 模块权限位**决定，与"保存"
--     同级；「新建版本」的授权语义就是"对这个模块有没有新增权"，不需要按钮级粒度。
--   · 走 MANUAL 的代价是真实的：管理员要在 230103 配按钮行 + 再授一次权 + 重新发布，
--     三步都做完按钮才出现。这层管理负担是机制错配带来的，不是业务需要的。
--
-- 现在的实现：`POST /api/v1/bom/new-version`（`BomController`），鉴权走
-- `PermissionAction.AddNew`，审计写 `BOM_NEW_VERSION`，版次仍由服务端算。
-- 前端按钮按 `definition.hasAdd` 显示——与"新增"同一个判据。
--
-- 本迁移只删那一行按钮；复制逻辑已从 `BomNewVersionHandler` 搬到 `BomRepository`。
-- 幂等：按 模块 + MANUAL + SEQ 精确匹配后再删，重复执行是空操作。
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
    THROW 58000, @GUARD_MESSAGE, 1;

DECLARE @ModuleId INT = 1204;
DECLARE @Mig NVARCHAR(100) = N'DbUp';

BEGIN TRANSACTION;

DELETE FROM dbo.MODULE_BUSINESS_ACTION
 WHERE M_IDX = @ModuleId
   AND EVENT_CODE = N'MANUAL'
   AND LTRIM(RTRIM(EFFECT_KEY)) = N'bom-new-version';
PRINT N'[414] 删除 MANUAL 按钮行（bom-new-version）：' + CAST(@@ROWCOUNT AS NVARCHAR(10)) + N' 行';

/* 按钮行消失，定义要重新下发 */
MERGE dbo.WORKBENCH_MODULE_DIRTY AS T
USING (SELECT @ModuleId AS M_IDX) AS S
   ON T.M_IDX = S.M_IDX
WHEN MATCHED THEN
    UPDATE SET T.DIRTY_TAG = 1, T.LAST_MODIFIED_BY = @Mig, T.LAST_MODIFIED_AT = SYSDATETIME()
WHEN NOT MATCHED THEN
    INSERT (M_IDX, DIRTY_TAG, LAST_MODIFIED_BY, LAST_MODIFIED_AT)
    VALUES (S.M_IDX, 1, @Mig, SYSDATETIME());
PRINT N'[414] 模块 1204 已标脏（按钮行移除，须重新发布）';

/* ---------------------------------------------------------------- 收口断言 */
IF EXISTS (SELECT 1 FROM dbo.MODULE_BUSINESS_ACTION
           WHERE M_IDX = @ModuleId AND EVENT_CODE = N'MANUAL'
             AND LTRIM(RTRIM(EFFECT_KEY)) = N'bom-new-version')
    THROW 58010, N'MANUAL 按钮行未删除，迁移中止。', 1;

/* 同时确认没有把别的按钮误删：1204 的 MANUAL 行应当为空 */
IF EXISTS (SELECT 1 FROM dbo.MODULE_BUSINESS_ACTION
           WHERE M_IDX = @ModuleId AND EVENT_CODE = N'MANUAL')
    THROW 58011, N'1204 仍有其它 MANUAL 行；本迁移只该删 bom-new-version，先人工核对，中止。', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.WORKBENCH_MODULE_DIRTY WHERE M_IDX = @ModuleId AND DIRTY_TAG = 1)
    THROW 58012, N'模块 1204 未标脏，迁移中止。', 1;

COMMIT TRANSACTION;

PRINT N'== 新建版本已改为定制页端点（POST /api/v1/bom/new-version，鉴权 AddNew）；MANUAL 按钮行已撤销 ==';
