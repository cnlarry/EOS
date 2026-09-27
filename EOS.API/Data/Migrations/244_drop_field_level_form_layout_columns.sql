-- 退役字段级排布配置与模块级页签/列数列：表单形态的唯一真源是 MODULE_FORM_LAYOUT / MODULE_FORM_TAB。
--
-- 与 241 的关键差别（241 先于代码落地删过一次，运行中的 API 与门禁 SQL 立刻报错，随即由 242 还原为占位列）：
--   ① 本次删列之前，代码与脚本的读点已**全部摘掉**——SQL 里只剩原位占位别名
--      （`NULL AS FORM_TABS` / `CAST(1 AS int) AS FORM_TAB_NO`），不再有任何语句引用这些列；
--   ② 脚本侧口径已改：check-form-layout 与 check-field-labels 的字段级判据改到 MODULE_FORM_LAYOUT，
--      export-config-snapshots 不再导出这些列，check-retired-db-objects 已登记本批退役列；
--   ③ 版式行已固化（261 个模块，scripts/materialize-form-layouts.ps1）：删列不会让零配置模块的
--      默认版式退化——"无行 = 未定制，字段按元数据顺序渲染"。
--
-- **保留（本次不删）**：
--   FIELDS.FORM_OPTIONS        —— 下拉选项，字段维护与工作台导出仍在读写；
--   SYSDD/SYSDH.FORM_ADJUST_TAG —— 报表版式「微调」权限位（FormDesignPermissionResolver 在用），
--                                 与字段级排布无关（241 误删过，242 已还原）。
--
-- 模块列数列（MODULES.FORM_COLUMNS）也在本批退役：原定"保留"（模块级单值），
-- 但用户 2026-08-31 已拍板统一表单**全局固定一行四列**、忽略该元数据，运行态/设计态/导出
-- 三处均已不再读它，留一列无人读写的死字段只会误导配置者（偏差已登记进实施计划）。

SET XACT_ABORT ON;
BEGIN TRANSACTION;

-- ① 前置自证：目标列必须还在（不在 = 已删过或库结构不同，值得当场炸而不是静默跳过）
IF COL_LENGTH('dbo.FIELDS', 'FORM_ORDER') IS NULL
    THROW 53000, N'FIELDS.FORM_ORDER 不存在：本迁移的前提不成立（库结构已变）。', 1;
IF COL_LENGTH('dbo.MODULES', 'FORM_TABS') IS NULL
    THROW 53000, N'MODULES.FORM_TABS 不存在：本迁移的前提不成立（库结构已变）。', 1;

-- ② 前置自证（棘轮）：持当前快照的模块必须都已有版式行，否则删列后它们会退化成"全字段按元数据顺序"。
--    已知例外与 241 同源（各自有原因，且已单独登记）：110101 物料相关（有 4 个不属于该模块表的字段）、
--    110311 物料主货位（搬运时当前账号无表单设计权）。例外表**只减不增**。
IF EXISTS (
        SELECT 1
        FROM dbo.WORKBENCH_DEFINITION_SNAPSHOT s WITH (NOLOCK)
        WHERE s.IS_CURRENT = 1
          AND s.MODULE_ID NOT IN (110101, 110311)
          AND NOT EXISTS (SELECT 1 FROM dbo.MODULE_FORM_LAYOUT l WHERE l.M_IDX = s.MODULE_ID))
    THROW 53001, N'存在未搬运版式的模块：先跑 scripts/materialize-form-layouts.ps1 -Apply 再删列。', 1;

-- ③ 先摘掉挂在目标列上的扩展属性（列注释），与 241 同法；默认约束不存在（都是可空无默认）
DECLARE @sql nvarchar(max) = N'';
SELECT @sql = @sql + N'EXEC sys.sp_dropextendedproperty @name = N'''
                  + REPLACE(ep.name, N'''', N'''''') + N''', @level0type = N''SCHEMA'', @level0name = N''dbo'''
                  + N', @level1type = N''TABLE'', @level1name = N''' + t.name + N''''
                  + N', @level2type = N''COLUMN'', @level2name = N''' + c.name + N''';' + CHAR(10)
FROM sys.extended_properties ep
JOIN sys.tables t ON t.object_id = ep.major_id
JOIN sys.columns c ON c.object_id = ep.major_id AND c.column_id = ep.minor_id
WHERE ep.minor_id > 0
  AND ((t.name = 'FIELDS' AND c.name IN ('FORM_ORDER', 'FORM_TAB_NO', 'FORM_SPAN', 'FORM_NEW_LINE',
                                         'FORM_CELL_GROUP', 'FORM_CELL_ROLE'))
    OR (t.name = 'MODULES' AND c.name IN ('FORM_TABS', 'FORM_COLUMNS')));
IF LEN(@sql) > 0
    EXEC sp_executesql @sql;

-- ④ 删列：字段级排布（排布归 MODULE_FORM_LAYOUT）+ 模块级页签/列数（页签归 MODULE_FORM_TAB、列数固定四子列）
ALTER TABLE dbo.FIELDS DROP COLUMN FORM_ORDER, FORM_TAB_NO, FORM_SPAN, FORM_NEW_LINE, FORM_CELL_GROUP, FORM_CELL_ROLE;
ALTER TABLE dbo.MODULES DROP COLUMN FORM_TABS, FORM_COLUMNS;

-- ⑤ 后置自证：8 列都必须消失
IF COL_LENGTH('dbo.FIELDS', 'FORM_ORDER') IS NOT NULL
    OR COL_LENGTH('dbo.FIELDS', 'FORM_TAB_NO') IS NOT NULL
    OR COL_LENGTH('dbo.FIELDS', 'FORM_SPAN') IS NOT NULL
    OR COL_LENGTH('dbo.FIELDS', 'FORM_NEW_LINE') IS NOT NULL
    OR COL_LENGTH('dbo.FIELDS', 'FORM_CELL_GROUP') IS NOT NULL
    OR COL_LENGTH('dbo.FIELDS', 'FORM_CELL_ROLE') IS NOT NULL
    OR COL_LENGTH('dbo.MODULES', 'FORM_TABS') IS NOT NULL
    OR COL_LENGTH('dbo.MODULES', 'FORM_COLUMNS') IS NOT NULL
    THROW 53002, N'退役列未被全部删除。', 1;

-- ⑥ 后置自证：保留列必须还在（FORM_OPTIONS 在用；报表版式的微调权限位在用）
IF COL_LENGTH('dbo.FIELDS', 'FORM_OPTIONS') IS NULL
    OR COL_LENGTH('dbo.SYSDD', 'FORM_ADJUST_TAG') IS NULL
    OR COL_LENGTH('dbo.SYSDH', 'FORM_ADJUST_TAG') IS NULL
    THROW 53003, N'不该删的列被删了（FORM_OPTIONS / SYSDD.SYSDH.FORM_ADJUST_TAG 必须保留）。', 1;

COMMIT TRANSACTION;
