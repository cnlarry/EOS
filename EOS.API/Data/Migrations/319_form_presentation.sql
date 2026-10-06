-- ============================================================================
-- EOS.ERP migration 319: 统一表单呈现配置（打开方式 / 窗体尺寸 + 页签级一行几列）
-- ----------------------------------------------------------------------------
-- "表单以什么形态打开、一行几列"分两处落，各归其位：
--
--   模块级（MODULES）——模块自身的事实，1:1，与"是否定制过版式"无关
--     FORM_OPEN_MODE      打开方式：'TAB' 本页签（默认）/ 'NEWTAB' 新页签 / 'DIALOG' 弹窗
--     FORM_DIALOG_WIDTH   弹窗宽度（px，仅弹窗方式使用；NULL = 前端默认 720）
--     FORM_DIALOG_HEIGHT  弹窗高度（px，仅弹窗方式使用；NULL = 前端默认 560）
--
--   页签级（MODULE_FORM_TAB）
--     LAYOUT_COLUMNS      该页签的栅格列数 1..4，NOT NULL DEFAULT 4
--                         （同一表单的页签 1 一行两列、页签 2 一行一列都合法）
--
-- 列数为什么在页签表而不在 MODULES：一个模块往往只有一个页签，而字段疏密差异恰恰落在**页签之间**，
-- 模块级单值表达不了；而"页签未声明时回落模块默认列数"会让同一语义有两个名字、其中一处空转。
-- 页面多时列数挂在版式表还有一层好处：它随页签行一起增删（重置版式/套用模板都不会留下一份
-- 无主的列数），而打开方式是模块的开关，不该被"重置版式"带走。
--
-- **本脚本是提交前折成的终态**：原 319（模块级四列）+ 321（页签级列数，可空）+ 322（改成
-- NOT NULL DEFAULT 4 并删掉模块级那列）三份合成一份——模块级从来没发布过 FORM_LAYOUT_COLUMNS，
-- 新库不必"先建后删"。决策过程见 ADR-022 订正 6/7/8/9 与决策台账 #139–#142。
-- 退役内置动作列（FORM_BUTTONS）是另一件事，留在 320。
--
-- 与已退役的字段级排布列（FIELDS.FORM_*）和模块级页签/列数列（MODULES.FORM_TABS /
-- MODULES.FORM_COLUMNS）无关：页签归 MODULE_FORM_TAB、字段排布归 MODULE_FORM_LAYOUT。
--
-- 发布语义：两处都随模块定义快照一并冻结，改了要重发布才在运行态生效（重发布由表单设计器代劳：
-- 它保存版式时同请求内重发布）。取值两侧把关：库内 CHECK 约束（本脚本）+ 保存端点白名单
-- （FormLayoutSubmission）。命名全大写；幂等：COL_LENGTH / sys.* 守卫。
-- ============================================================================

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @GUARD_MESSAGE NVARCHAR(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, @GUARD_MESSAGE, 1;

IF OBJECT_ID(N'dbo.MODULE_FORM_TAB', N'U') IS NULL
    THROW 53010, N'MODULE_FORM_TAB 不存在：先落库迁移 232（版式表）。', 1;

BEGIN TRANSACTION;

-- ① 模块级：打开方式 + 窗体尺寸（幂等）。全部可空——NULL 就是"未配置"，读取侧按默认值兜底。
IF COL_LENGTH(N'dbo.MODULES', N'FORM_OPEN_MODE') IS NULL
    ALTER TABLE dbo.MODULES ADD FORM_OPEN_MODE NVARCHAR(10) NULL
        CONSTRAINT CK_MODULES_FORM_OPEN_MODE
        CHECK (FORM_OPEN_MODE IS NULL OR FORM_OPEN_MODE IN (N'TAB', N'NEWTAB', N'DIALOG'));

IF COL_LENGTH(N'dbo.MODULES', N'FORM_DIALOG_WIDTH') IS NULL
    ALTER TABLE dbo.MODULES ADD FORM_DIALOG_WIDTH INT NULL
        CONSTRAINT CK_MODULES_FORM_DIALOG_WIDTH
        CHECK (FORM_DIALOG_WIDTH IS NULL OR (FORM_DIALOG_WIDTH >= 240 AND FORM_DIALOG_WIDTH <= 4000));

IF COL_LENGTH(N'dbo.MODULES', N'FORM_DIALOG_HEIGHT') IS NULL
    ALTER TABLE dbo.MODULES ADD FORM_DIALOG_HEIGHT INT NULL
        CONSTRAINT CK_MODULES_FORM_DIALOG_HEIGHT
        CHECK (FORM_DIALOG_HEIGHT IS NULL OR (FORM_DIALOG_HEIGHT >= 200 AND FORM_DIALOG_HEIGHT <= 4000));

-- ② 页签级：一行几列（幂等）。NOT NULL + DEFAULT 4：兜底值只此一处，新页签不写列数即落 4。
IF COL_LENGTH(N'dbo.MODULE_FORM_TAB', N'LAYOUT_COLUMNS') IS NULL
    ALTER TABLE dbo.MODULE_FORM_TAB ADD LAYOUT_COLUMNS TINYINT NOT NULL
        CONSTRAINT DF_MODULE_FORM_TAB_LAYOUT_COLUMNS DEFAULT (4);
GO
-- 批分离（GO）：刚加的列在**同一批**里不可引用（SQL Server 整批编译，编译期看不到新列）。
-- 本脚本的前身（322）正是在这里踩过 Msg 207，所以下面引用新列的语句全部落在这一批。

-- ③ 本脚本前身（旧 319）曾在 MODULES 上加过 FORM_LAYOUT_COLUMNS（模块级列数），折成终态后它
--    不该存在——列数是页签级事实。若某个库半途落过旧 319（本机就是），这里顺手删掉（幂等）：
--    先摘挂在它上面的 CHECK 与列注释，再删列。新库上整段跳过。
IF COL_LENGTH(N'dbo.MODULES', N'FORM_LAYOUT_COLUMNS') IS NOT NULL
BEGIN
    IF EXISTS (
        SELECT 1 FROM sys.check_constraints
        WHERE parent_object_id = OBJECT_ID(N'dbo.MODULES') AND name = N'CK_MODULES_FORM_LAYOUT_COLUMNS')
        ALTER TABLE dbo.MODULES DROP CONSTRAINT CK_MODULES_FORM_LAYOUT_COLUMNS;

    DECLARE @dropSql nvarchar(max) = N'';
    SELECT @dropSql = @dropSql + N'EXEC sys.sp_dropextendedproperty @name = N'''
                      + REPLACE(ep.name, N'''', N'''''') + N''', @level0type = N''SCHEMA'', @level0name = N''dbo'''
                      + N', @level1type = N''TABLE'', @level1name = N''MODULES'''
                      + N', @level2type = N''COLUMN'', @level2name = N''FORM_LAYOUT_COLUMNS'';' + CHAR(10)
    FROM sys.extended_properties ep
    WHERE ep.major_id = OBJECT_ID(N'dbo.MODULES')
      AND ep.minor_id = COLUMNPROPERTY(OBJECT_ID(N'dbo.MODULES'), N'FORM_LAYOUT_COLUMNS', 'ColumnId');
    IF @dropSql <> N'' EXEC sp_executesql @dropSql;

    ALTER TABLE dbo.MODULES DROP COLUMN FORM_LAYOUT_COLUMNS;
    PRINT N'== 旧 319 留下的 MODULES.FORM_LAYOUT_COLUMNS 已删除（列数归 MODULE_FORM_TAB）==';
END

-- ③ 该列若还是可空（本脚本前身 321 建的正是可空列）：先补 NULL、摘旧 CHECK，再固化 NOT NULL。
-- 必须先于默认值：ALTER COLUMN 改可空性时，挂在列上的默认约束会挡（"对象 DF_… 依赖于列 …"）。
-- 终态库里这段整体跳过（列已经是 NOT NULL，且带着默认约束），不会去撞既有约束。
IF EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID(N'dbo.MODULE_FORM_TAB') AND name = N'LAYOUT_COLUMNS' AND is_nullable = 1)
BEGIN
    IF EXISTS (
        SELECT 1 FROM sys.check_constraints
        WHERE parent_object_id = OBJECT_ID(N'dbo.MODULE_FORM_TAB') AND name = N'CK_MODULE_FORM_TAB_LAYOUT_COLUMNS')
        ALTER TABLE dbo.MODULE_FORM_TAB DROP CONSTRAINT CK_MODULE_FORM_TAB_LAYOUT_COLUMNS;

    UPDATE dbo.MODULE_FORM_TAB SET LAYOUT_COLUMNS = 4 WHERE LAYOUT_COLUMNS IS NULL;
    DECLARE @filled INT = @@ROWCOUNT;

    ALTER TABLE dbo.MODULE_FORM_TAB ALTER COLUMN LAYOUT_COLUMNS TINYINT NOT NULL;
    PRINT N'== 页签列数由可空固化为 NOT NULL，补 NULL ' + CONVERT(nvarchar(12), @filled) + N' 行 ==';
END

-- ④ 页签列数：默认值 + 1..4 的 CHECK（幂等）。DEFAULT 子句只吃字面量——ALTER TABLE 不接受变量（Msg 112）。
IF NOT EXISTS (
    SELECT 1 FROM sys.default_constraints
    WHERE parent_object_id = OBJECT_ID(N'dbo.MODULE_FORM_TAB') AND name = N'DF_MODULE_FORM_TAB_LAYOUT_COLUMNS')
    ALTER TABLE dbo.MODULE_FORM_TAB ADD CONSTRAINT DF_MODULE_FORM_TAB_LAYOUT_COLUMNS
        DEFAULT (4) FOR LAYOUT_COLUMNS;

IF NOT EXISTS (
    SELECT 1 FROM sys.check_constraints
    WHERE parent_object_id = OBJECT_ID(N'dbo.MODULE_FORM_TAB') AND name = N'CK_MODULE_FORM_TAB_LAYOUT_COLUMNS')
    ALTER TABLE dbo.MODULE_FORM_TAB ADD CONSTRAINT CK_MODULE_FORM_TAB_LAYOUT_COLUMNS
        CHECK (LAYOUT_COLUMNS >= 1 AND LAYOUT_COLUMNS <= 4);

-- ④ 列说明（schema 审阅用）
DECLARE @descriptions TABLE (TABLE_NAME SYSNAME NOT NULL, COLUMN_NAME SYSNAME NOT NULL, DESCRIPTION NVARCHAR(400) NOT NULL);

INSERT INTO @descriptions (TABLE_NAME, COLUMN_NAME, DESCRIPTION) VALUES
    (N'MODULES',         N'FORM_OPEN_MODE',     N'统一表单打开方式：TAB 本页签（默认）/ NEWTAB 新页签 / DIALOG 弹窗；NULL 等同 TAB'),
    (N'MODULES',         N'FORM_DIALOG_WIDTH',  N'弹窗方式下的窗体宽度（px，240..4000）；非弹窗方式忽略；NULL = 前端默认'),
    (N'MODULES',         N'FORM_DIALOG_HEIGHT', N'弹窗方式下的窗体高度（px，200..4000）；非弹窗方式忽略；NULL = 前端默认'),
    (N'MODULE_FORM_TAB', N'LAYOUT_COLUMNS',     N'该页签的布局列数（1..4），NOT NULL DEFAULT 4：一行几列只此一处真源');

DECLARE @tableName SYSNAME, @columnName SYSNAME, @description NVARCHAR(400);
DECLARE description_cursor CURSOR LOCAL FAST_FORWARD FOR
    SELECT TABLE_NAME, COLUMN_NAME, DESCRIPTION FROM @descriptions;
OPEN description_cursor;
FETCH NEXT FROM description_cursor INTO @tableName, @columnName, @description;
WHILE @@FETCH_STATUS = 0
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM sys.extended_properties ep
        WHERE ep.major_id = OBJECT_ID(N'dbo.' + @tableName)
          AND ep.minor_id = COLUMNPROPERTY(OBJECT_ID(N'dbo.' + @tableName), @columnName, 'ColumnId')
          AND ep.name = N'MS_Description')
    BEGIN
        EXEC sys.sp_addextendedproperty @name = N'MS_Description', @value = @description,
            @level0type = N'SCHEMA', @level0name = N'dbo',
            @level1type = N'TABLE', @level1name = @tableName,
            @level2type = N'COLUMN', @level2name = @columnName;
    END
    FETCH NEXT FROM description_cursor INTO @tableName, @columnName, @description;
END
CLOSE description_cursor;
DEALLOCATE description_cursor;

-- ⑤ 后置自证：该有的列都在
IF COL_LENGTH(N'dbo.MODULES', N'FORM_OPEN_MODE') IS NULL
    OR COL_LENGTH(N'dbo.MODULES', N'FORM_DIALOG_WIDTH') IS NULL
    OR COL_LENGTH(N'dbo.MODULES', N'FORM_DIALOG_HEIGHT') IS NULL
    THROW 53011, N'MODULES 表单呈现配置列未被全部创建。', 1;

IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID(N'dbo.MODULE_FORM_TAB') AND name = N'LAYOUT_COLUMNS' AND is_nullable = 0)
    THROW 53013, N'MODULE_FORM_TAB.LAYOUT_COLUMNS 未落成 NOT NULL。', 1;

IF NOT EXISTS (
    SELECT 1 FROM sys.default_constraints
    WHERE parent_object_id = OBJECT_ID(N'dbo.MODULE_FORM_TAB') AND name = N'DF_MODULE_FORM_TAB_LAYOUT_COLUMNS')
    THROW 53014, N'MODULE_FORM_TAB.LAYOUT_COLUMNS 缺默认约束（新页签不写列数时该落 4）。', 1;

IF NOT EXISTS (
    SELECT 1 FROM sys.check_constraints
    WHERE parent_object_id = OBJECT_ID(N'dbo.MODULE_FORM_TAB') AND name = N'CK_MODULE_FORM_TAB_LAYOUT_COLUMNS')
    THROW 53015, N'MODULE_FORM_TAB.LAYOUT_COLUMNS 缺 1..4 的 CHECK 约束。', 1;

IF EXISTS (SELECT 1 FROM dbo.MODULE_FORM_TAB WHERE LAYOUT_COLUMNS IS NULL)
    THROW 53016, N'仍有页签的 LAYOUT_COLUMNS 为空：NOT NULL 的前提不成立。', 1;

-- ⑥ 后置自证：退役列不得被本脚本"顺手"加回来（FORM_BUTTONS 由 320 负责，这里不管它）
IF COL_LENGTH(N'dbo.MODULES', N'FORM_COLUMNS') IS NOT NULL
    OR COL_LENGTH(N'dbo.MODULES', N'FORM_TABS') IS NOT NULL
    OR COL_LENGTH(N'dbo.MODULES', N'FORM_LAYOUT_COLUMNS') IS NOT NULL
    THROW 53012, N'已退役的 MODULES.FORM_COLUMNS / FORM_TABS / FORM_LAYOUT_COLUMNS 不得复活。', 1;

-- 守卫式提交：本脚本没有跳过路径，正常路径下事务一定开着（写成守卫是防手工执行时的混淆）
IF @@TRANCOUNT > 0
    COMMIT TRANSACTION;

PRINT N'== 表单呈现配置就位：MODULES 打开方式/窗体尺寸三列 + MODULE_FORM_TAB.LAYOUT_COLUMNS（NOT NULL DEFAULT 4）==';
