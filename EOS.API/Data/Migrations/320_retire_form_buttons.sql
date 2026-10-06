-- 退役 MODULES.FORM_BUTTONS（界面上的「内置动作（受控注册码）」）：物理删列。
--
-- 它来自旧系统的"受控注册码"：把内置动作（批核/结案/打印…）按注册码配进工具条白名单。
-- 现代实现里这套白名单**已经没有任何消费方**：
--   ① 工具栏按钮改由「能力 + 权限 + 单据状态」决定（WorkbenchStates/FormEditorPage 的能力判定），
--      白名单为空时前端早就走在同一条回退集上，删列前后运行态逐字节等价；
--   ② 发布校验（WorkbenchDefinitionValidator）读的是 MODULE_BUSINESS_ACTION 的自定义按钮行，
--      与这列无关；生命周期列门禁（WorkflowStates.NeedsApproveColumn）明文排除它；
--   ③ 库内实测该列**全空**（364 个模块无一行配过值），留着只会让配置者以为"配了会生效"。
--
-- 前置自证里那条"非空即拒"是有意为之：真有人在库里配过动作码时，先把它迁到自定义按钮
-- 再删列，而不是静默丢掉配置（配置类迁移不猜）。
--
-- 同批代码侧改动：Models/WorkbenchModels.cs 去掉 WorkbenchButton 与按钮契约段、
-- WorkbenchDefinitionBuilder 不再读该列与解析白名单、MenuAdminRepository/Models 去掉该字段、
-- export-config-snapshots 不再导出、check-retired-db-objects 登记本列。

SET XACT_ABORT ON;
BEGIN TRANSACTION;

-- ① 前置自证：目标列必须还在（不在 = 已删过或库结构不同，值得当场炸而不是静默跳过）
IF COL_LENGTH('dbo.MODULES', 'FORM_BUTTONS') IS NULL
    THROW 53000, N'MODULES.FORM_BUTTONS 不存在：本迁移的前提不成立（库结构已变）。', 1;

-- ② 前置自证（不静默丢配置）：任何一行还配着动作码都不许删
IF EXISTS (SELECT 1 FROM dbo.MODULES WITH (NOLOCK)
           WHERE LTRIM(RTRIM(ISNULL(FORM_BUTTONS, N''))) <> N'')
    THROW 53001, N'仍有模块配着 FORM_BUTTONS：先把这些动作码迁到「自定义按钮」（MODULE_BUSINESS_ACTION）再删列。', 1;

-- ③ 先摘掉挂在目标列上的扩展属性（列注释）；默认约束不存在（可空无默认）
DECLARE @sql nvarchar(max) = N'';
SELECT @sql = @sql + N'EXEC sys.sp_dropextendedproperty @name = N'''
                  + REPLACE(ep.name, N'''', N'''''') + N''', @level0type = N''SCHEMA'', @level0name = N''dbo'''
                  + N', @level1type = N''TABLE'', @level1name = N''' + t.name + N''''
                  + N', @level2type = N''COLUMN'', @level2name = N''' + c.name + N''';' + CHAR(10)
FROM sys.extended_properties ep
JOIN sys.tables t ON t.object_id = ep.major_id
JOIN sys.columns c ON c.object_id = ep.major_id AND c.column_id = ep.minor_id
WHERE ep.minor_id > 0
  AND t.name = 'MODULES'
  AND c.name = 'FORM_BUTTONS';
IF LEN(@sql) > 0
    EXEC sp_executesql @sql;

-- ④ 删列
ALTER TABLE dbo.MODULES DROP COLUMN FORM_BUTTONS;

-- ⑤ 后置自证：目标列必须消失
IF COL_LENGTH('dbo.MODULES', 'FORM_BUTTONS') IS NOT NULL
    THROW 53002, N'退役列未被删除。', 1;

-- ⑥ 后置自证：不该删的列必须还在
--    ① 呈现配置三列（319 新增，设计器在用）；② 效果引擎开关（发布门/引擎在用）。
--    这里**不列 MODULES.FORM_LAYOUT_COLUMNS**：那是 319 自己的事，321/322 折进 319 之后模块级
--    列数已不存在（列数落在 MODULE_FORM_TAB.LAYOUT_COLUMNS）。把它写成"必须保留"，新库上
--    320 会在 319 之后再炸一次（DbUp 会把服务启动带下去）——本脚本只对自己这一列负责。
IF COL_LENGTH('dbo.MODULES', 'FORM_OPEN_MODE') IS NULL
    OR COL_LENGTH('dbo.MODULES', 'FORM_DIALOG_WIDTH') IS NULL
    OR COL_LENGTH('dbo.MODULES', 'FORM_DIALOG_HEIGHT') IS NULL
    OR COL_LENGTH('dbo.MODULES', 'EFFECT_ENGINE_TAG') IS NULL
    THROW 53003, N'不该删的列被删了（FORM_OPEN_MODE / FORM_DIALOG_* / EFFECT_ENGINE_TAG 必须保留）。', 1;

COMMIT TRANSACTION;
