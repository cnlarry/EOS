-- ============================================================================
-- EOS.ERP migration 426: 230104 显示名收短为「行为动作」
-- ----------------------------------------------------------------------------
-- 编辑面仍是"行为动作 / 校验规则"两个页签（同一份草稿、一次提交），但菜单名
-- 不必把两个页签并列：这一面配的是"这个模块怎么动"，动作是主体，校验规则是
-- 同一编辑面上的另一片。
--
-- 只改显示名，不动形态 / 承载页 / 数据源锚点 / 授权行；路由与权限常量仍按 230104。
-- 幂等：按当前值判断，可重复执行。
-- ============================================================================

SET NOCOUNT ON;
SET XACT_ABORT ON;

IF DB_NAME() <> N'EOS.ERP'
    THROW 51950, N'本脚本只能在 EOS.ERP 数据库内执行。', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.MODULES WHERE M_IDX = 230104)
    THROW 51951, N'模块 230104 不存在：本迁移是 350 的后续，迁移中止。', 1;

BEGIN TRANSACTION;

UPDATE dbo.MODULES
   SET M_DESC = N'行为动作', LAST_UPDATE_BY = N'426', LAST_UPDATE_DATE = GETDATE()
 WHERE M_IDX = 230104
   AND M_DESC <> N'行为动作';
PRINT N'== 230104 已更名「行为动作」 ' + CONVERT(NVARCHAR(10), @@ROWCOUNT) + N' 行 ==';

IF NOT EXISTS (SELECT 1 FROM dbo.MODULES
               WHERE M_IDX = 230104
                 AND M_DESC = N'行为动作'
                 AND M_URL = N'/admin/module-actions'
                 AND MASTER_TABLE IS NULL)
    THROW 51952, N'230104 的名称、承载页或数据源锚点不符，迁移中止。', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.V_MODULE_NODE WHERE M_IDX = 230104 AND NODE_KIND = N'CUSTOMPAGE')
    THROW 51953, N'230104 的形态不是自定义承载页，迁移中止。', 1;

/* 同族的三个配置面（230101 定义 / 230102 分组 / 230103 按钮）不应受这次改名影响：
   缺任何一行都中止，避免以后复制本脚本时顺带扫到别的模块。 */
IF (SELECT COUNT(*) FROM dbo.MODULES WHERE M_IDX IN (230101, 230102, 230103)) <> 3
    THROW 51954, N'230101 / 230102 / 230103 的同族模块行不齐全，迁移中止。', 1;

COMMIT TRANSACTION;

PRINT N'== 收口：230104 = '
    + ISNULL((SELECT M_DESC FROM dbo.MODULES WHERE M_IDX = 230104), N'(缺失)') + N' ==';
