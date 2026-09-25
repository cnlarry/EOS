-- 退役字段级表单排布配置：模块级版式（MODULE_FORM_LAYOUT / MODULE_FORM_TAB）成为表单形态的唯一真源。
--
-- 顺序前提（都已就绪）：
--   ① 版式数据已搬运：261 个模块的版式行 = 搬运当刻的渲染结果（scripts/materialize-form-layouts.ps1）；
--   ② 运行时不再按字段级配置推导（无版式行 = 未定制，字段按元数据顺序渲染）；
--   ③ 库内对象（视图/存储过程/函数/触发器）对以下列零引用；FORM_NEW_LINE 与 FORM_ADJUST_TAG 全库零使用。
--
-- 唯一真源之后，"无行"不再等价于"按字段级配置推导"，故这些列留着只会误导配置者。

SET XACT_ABORT ON;
BEGIN TRANSACTION;

-- ① 前置自证：目标列必须还在（不在 = 已删过或库结构不同，值得当场炸而不是静默跳过）
IF COL_LENGTH('dbo.FIELDS', 'FORM_ORDER') IS NULL
    THROW 51000, N'FIELDS.FORM_ORDER 不存在：本迁移的前提不成立（库结构已变）。', 1;
IF COL_LENGTH('dbo.MODULES', 'FORM_TABS') IS NULL
    THROW 51000, N'MODULES.FORM_TABS 不存在：本迁移的前提不成立（库结构已变）。', 1;

-- ② 前置自证（棘轮）：持当前快照的模块必须都已有版式行，否则删列后它们会退化成"全字段按元数据顺序"。
--    已知例外（各自有原因，且已单独登记）：
--      110101 物料相关：有 4 个不属于该模块表的字段，搬运被校验拦下，待订正元数据；
--      110311 物料主货位：当前账号对该模块没有表单设计权，搬运时 403。
--    例外表**只减不增**：新增一个未搬运模块即在此失败，防止批量遗漏被静默吞掉。
IF EXISTS (
        SELECT 1
        FROM dbo.WORKBENCH_DEFINITION_SNAPSHOT s WITH (NOLOCK)
        WHERE s.IS_CURRENT = 1
          AND s.MODULE_ID NOT IN (110101, 110311)
          AND NOT EXISTS (SELECT 1 FROM dbo.MODULE_FORM_LAYOUT l WHERE l.M_IDX = s.MODULE_ID))
    THROW 51001, N'存在未搬运版式的模块：先跑 scripts/materialize-form-layouts.ps1 -Apply 再删列。', 1;

-- ③ 先摘掉挂在目标列上的默认约束与扩展属性（否则删列会失败）
IF OBJECT_ID('dbo.DF_SYSDD_FORM_ADJUST', 'D') IS NOT NULL
    ALTER TABLE dbo.SYSDD DROP CONSTRAINT DF_SYSDD_FORM_ADJUST;
IF OBJECT_ID('dbo.DF_SYSDH_FORM_ADJUST', 'D') IS NOT NULL
    ALTER TABLE dbo.SYSDH DROP CONSTRAINT DF_SYSDH_FORM_ADJUST;

DECLARE @sql nvarchar(max) = N'';
SELECT @sql = @sql + N'EXEC sys.sp_dropextendedproperty @name = N'''
                  + REPLACE(ep.name, N'''', N'''''') + N''', @level0type = N''SCHEMA'', @level0name = N''dbo'''
                  + N', @level1type = N''TABLE'', @level1name = N''' + t.name + N''''
                  + N', @level2type = N''COLUMN'', @level2name = N''' + c.name + N''';' + CHAR(10)
FROM sys.extended_properties ep
JOIN sys.tables t ON t.object_id = ep.major_id
JOIN sys.columns c ON c.object_id = ep.major_id AND c.column_id = ep.minor_id
WHERE ep.minor_id > 0
  AND ((t.name = 'SYSDD' AND c.name = 'FORM_ADJUST_TAG') OR (t.name = 'SYSDH' AND c.name = 'FORM_ADJUST_TAG'));
IF LEN(@sql) > 0
    EXEC sp_executesql @sql;

-- ④ 删列
ALTER TABLE dbo.FIELDS DROP COLUMN FORM_ORDER, FORM_TAB_NO, FORM_SPAN, FORM_NEW_LINE, FORM_CELL_GROUP, FORM_CELL_ROLE;
ALTER TABLE dbo.MODULES DROP COLUMN FORM_TABS;
ALTER TABLE dbo.SYSDD DROP COLUMN FORM_ADJUST_TAG;
ALTER TABLE dbo.SYSDH DROP COLUMN FORM_ADJUST_TAG;

-- ⑤ 后置自证：列必须都已消失
IF COL_LENGTH('dbo.FIELDS', 'FORM_ORDER') IS NOT NULL
    THROW 51002, N'FIELDS.FORM_ORDER 未被删除。', 1;
IF COL_LENGTH('dbo.FIELDS', 'FORM_CELL_ROLE') IS NOT NULL
    THROW 51002, N'FIELDS.FORM_CELL_ROLE 未被删除。', 1;
IF COL_LENGTH('dbo.MODULES', 'FORM_TABS') IS NOT NULL
    THROW 51002, N'MODULES.FORM_TABS 未被删除。', 1;
IF COL_LENGTH('dbo.SYSDD', 'FORM_ADJUST_TAG') IS NOT NULL OR COL_LENGTH('dbo.SYSDH', 'FORM_ADJUST_TAG') IS NOT NULL
    THROW 51002, N'SYSDD/SYSDH.FORM_ADJUST_TAG 未被删除。', 1;

COMMIT TRANSACTION;
