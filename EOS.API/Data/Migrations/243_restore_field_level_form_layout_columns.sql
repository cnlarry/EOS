-- 恢复 241 删掉的 8 列（**列回来，值不回**）：241 先于代码读点清理落地，运行中的 API 仍在
-- 按位置/按名读取这些列，导致菜单与工作台定义重建 500、门禁 SQL 报错。
--
-- 恢复语义：这些列**只作为"读取占位"存在**，值一律为空/默认（= 未配置）。
--   ① 运行时自 241 之前一步起就不再按字段级配置推导（无版式行 = 未定制），故空值不影响渲染；
--   ② 排布信息早已固化进 MODULE_FORM_LAYOUT / MODULE_FORM_TAB（261 个模块），空值不丢业务语义。
-- 正确顺序（下批执行）：先把代码/脚本的读点全部摘掉并编译+用例验证，再重新删列。
-- 这样"已删过的列"与"未清理的读点"不再同时存在。

SET XACT_ABORT ON;
BEGIN TRANSACTION;

-- 前置自证：确认当前确实缺这些列（若已存在，说明本迁移不该跑）
IF COL_LENGTH('dbo.FIELDS', 'FORM_ORDER') IS NOT NULL
    THROW 52000, N'FIELDS.FORM_ORDER 已存在：本恢复迁移不适用。', 1;

-- ① FIELDS：字段级排布占位列
ALTER TABLE dbo.FIELDS ADD
    FORM_ORDER      int           NULL,
    FORM_TAB_NO     tinyint       NULL,
    FORM_SPAN       tinyint       NULL,
    FORM_NEW_LINE   bit           NULL,
    FORM_CELL_GROUP nvarchar(50)  NULL,
    FORM_CELL_ROLE  tinyint       NULL;

-- ② MODULES：页签定义占位列
ALTER TABLE dbo.MODULES ADD FORM_TABS nvarchar(500) NULL;

-- ③ SYSDD / SYSDH：报表版式微调位占位列（含默认约束，与被删前同形）
ALTER TABLE dbo.SYSDD ADD FORM_ADJUST_TAG bit NULL
    CONSTRAINT DF_SYSDD_FORM_ADJUST DEFAULT (0);
ALTER TABLE dbo.SYSDH ADD FORM_ADJUST_TAG bit NULL
    CONSTRAINT DF_SYSDH_FORM_ADJUST DEFAULT (0);

-- ④ 后置自证：8 列都必须回来
IF COL_LENGTH('dbo.FIELDS', 'FORM_ORDER') IS NULL
    OR COL_LENGTH('dbo.FIELDS', 'FORM_TAB_NO') IS NULL
    OR COL_LENGTH('dbo.FIELDS', 'FORM_SPAN') IS NULL
    OR COL_LENGTH('dbo.FIELDS', 'FORM_NEW_LINE') IS NULL
    OR COL_LENGTH('dbo.FIELDS', 'FORM_CELL_GROUP') IS NULL
    OR COL_LENGTH('dbo.FIELDS', 'FORM_CELL_ROLE') IS NULL
    OR COL_LENGTH('dbo.MODULES', 'FORM_TABS') IS NULL
    OR COL_LENGTH('dbo.SYSDD', 'FORM_ADJUST_TAG') IS NULL
    OR COL_LENGTH('dbo.SYSDH', 'FORM_ADJUST_TAG') IS NULL
    THROW 52001, N'占位列未全部恢复。', 1;

COMMIT TRANSACTION;
