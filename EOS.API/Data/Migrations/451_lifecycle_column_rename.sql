/*
 * 生命周期列命名收敛（ADR-043）：物理列、字段元数据、配置引用一起改。
 *
 * 四组语义各用一套名字，历史迁移里出现过 CREATE_×、LAST_UPDATE_×、CONFIRM_×、FINISHED_×
 * 与 CREATE_BY / CREATED_AT / CREATED_DATE / UPDATED_* / LAST_MODIFIED_* / CONFIRMED_* 等写法。
 * 本脚本把它们收敛为：
 *   建立 CREATED_BY   / CREATED_TIME
 *   修改 MODIFIED_BY  / MODIFIED_TIME
 *   批核 APPROVED_BY  / APPROVED_TIME / APPROVED
 *   结案 FINISHED_BY  / FINISHED_TIME / FINISHED
 *
 * 改名走 sp_rename（元数据操作）：数据、默认约束、索引都跟着列对象走，不搬数据。
 * 明确不改的：MODULE_BUSINESS_ACTION.CONFIRM_TAG（自定义动作“点击前确认”的配置位，
 * 与单据批核无关）、权限位 APPROVE_TAG/ENDCASE_TAG 等、业务列 FINISHED_QTY/FINISHED_AMOUNT 等、
 * AUDIT_EVENT.ACTION 动作码、AUDIT_FIELD_CHANGE 与 AUDIT_EVENT.DETAIL_JSON 的历史值。
 */

SET NOCOUNT ON;
SET XACT_ABORT ON;

-- ---------------------------------------------------------------------------
-- 1) 旧名 → 新名映射（唯一真源，后续物理列改名与配置文本替换都取它）
-- ---------------------------------------------------------------------------
DECLARE @map TABLE (OldName sysname NOT NULL PRIMARY KEY, NewName sysname NOT NULL, Seq int NOT NULL);
INSERT @map (OldName, NewName, Seq) VALUES
    (N'CREATE_PERSON',    N'CREATED_BY',    1),
    (N'CREATE_DATE',      N'CREATED_TIME',  2),
    (N'CREATE_BY',        N'CREATED_BY',    3),
    (N'CREATED_AT',       N'CREATED_TIME',  4),
    (N'CREATED_DATE',     N'CREATED_TIME',  5),
    (N'LAST_UPDATE_BY',   N'MODIFIED_BY',   6),
    (N'LAST_UPDATE_DATE', N'MODIFIED_TIME', 7),
    (N'LAST_MODIFIED_BY', N'MODIFIED_BY',   8),
    (N'LAST_MODIFIED_AT', N'MODIFIED_TIME', 9),
    (N'UPDATED_BY',       N'MODIFIED_BY',  10),
    (N'UPDATED_AT',       N'MODIFIED_TIME', 11),
    (N'CONFIRM_PERSON',   N'APPROVED_BY',  12),
    (N'CONFIRM_DATE',     N'APPROVED_TIME',13),
    (N'CONFIRM_TAG',      N'APPROVED',     14),
    (N'FINISHED_PERSON',  N'FINISHED_BY',  15),
    (N'FINISHED_DATE',    N'FINISHED_TIME',16),
    (N'FINISHED_TAG',     N'FINISHED',     17),
    (N'CONFIRMED_BY',     N'APPROVED_BY',  18),
    (N'CONFIRMED_DATE',   N'APPROVED_TIME',19);

-- 唯一允许保留旧名的列：自定义动作的“点击前确认”配置位。
DECLARE @exceptionTable sysname = N'MODULE_BUSINESS_ACTION';
DECLARE @exceptionColumn sysname = N'CONFIRM_TAG';

-- ---------------------------------------------------------------------------
-- 2) 预检：同一张表上新旧名不得并存（并存说明改过一半，盲目改名会丢数据）
-- ---------------------------------------------------------------------------
DECLARE @conflict nvarchar(500) = (
    SELECT TOP 1 CONCAT(t.name, N'.', c.name, N' 与 ', m.NewName, N' 并存')
    FROM @map m
    JOIN sys.columns c ON c.name = m.OldName
    JOIN sys.tables t ON t.object_id = c.object_id
    WHERE EXISTS (SELECT 1 FROM sys.columns x WHERE x.object_id = c.object_id AND x.name = m.NewName)
    ORDER BY t.name, c.name);

IF @conflict IS NOT NULL
BEGIN
    DECLARE @preflight nvarchar(600) = N'生命周期列改名预检失败（新旧列名并存）：' + @conflict;
    THROW 50451, @preflight, 1;
END

-- ---------------------------------------------------------------------------
-- 3) 物理列改名（sp_rename）
-- ---------------------------------------------------------------------------
DECLARE @renames TABLE (
    Id int IDENTITY(1, 1) PRIMARY KEY,
    TableName sysname NOT NULL,
    OldName sysname NOT NULL,
    NewName sysname NOT NULL,
    Qualified nvarchar(400) NOT NULL);

INSERT @renames (TableName, OldName, NewName, Qualified)
SELECT t.name, c.name, m.NewName,
       QUOTENAME(s.name) + N'.' + QUOTENAME(t.name) + N'.' + QUOTENAME(c.name)
FROM sys.columns c
JOIN sys.tables t ON t.object_id = c.object_id
JOIN sys.schemas s ON s.schema_id = t.schema_id
JOIN @map m ON m.OldName = c.name
WHERE s.name = N'dbo'
  AND NOT (t.name = @exceptionTable AND c.name = @exceptionColumn)
ORDER BY t.name, m.Seq;

DECLARE @total int = (SELECT COUNT(*) FROM @renames);
DECLARE @tableCount int = (SELECT COUNT(DISTINCT TableName) FROM @renames);
DECLARE @i int = 1, @qualified nvarchar(400), @newName sysname;
WHILE @i <= @total
BEGIN
    SELECT @qualified = Qualified, @newName = NewName FROM @renames WHERE Id = @i;
    EXEC sp_rename @qualified, @newName, N'COLUMN';
    SET @i += 1;
END
PRINT CONCAT(N'[451] 物理列改名：', @total, N' 列 / ', @tableCount, N' 张表');

-- 复核：旧名只剩那一个例外列
DECLARE @leftover nvarchar(500) = (
    SELECT TOP 1 CONCAT(t.name, N'.', c.name)
    FROM @map m
    JOIN sys.columns c ON c.name = m.OldName
    JOIN sys.tables t ON t.object_id = c.object_id
    WHERE NOT (t.name = @exceptionTable AND c.name = @exceptionColumn)
    ORDER BY t.name);

IF @leftover IS NOT NULL
BEGIN
    DECLARE @leftoverMsg nvarchar(600) = N'生命周期列改名后仍有旧列名：' + @leftover;
    THROW 50452, @leftoverMsg, 1;
END

-- ---------------------------------------------------------------------------
-- 4) 配置面文本替换（结构化条件、过滤模板、排序、分组、数据范围、影响链）
--
-- 替换按“精确字段名 token”进行：映射表里没有 FINISHED_QTY / APPROVE_TAG / CONFIRM_STATUS
-- 这类同前缀但语义不同的列，所以不会把它们一并改掉。
-- ---------------------------------------------------------------------------
DECLARE @targets TABLE (Id int IDENTITY(1, 1) PRIMARY KEY, TableName sysname NOT NULL, ColumnName sysname NOT NULL);
INSERT @targets (TableName, ColumnName) VALUES
    (N'FIELDS', N'F_ID'),
    (N'FIELDS', N'VIRTUAL_EXP'),
    (N'FIELDS', N'F_REMARK'),
    (N'SYS_DEFAULT_COLUMN', N'F_ID'),
    (N'SYS_USER_COLUMN', N'F_ID'),
    (N'SYS_USER_CHOOSER_MEMORY', N'F_ID'),
    (N'FIELD_DATASOURCE', N'F_ID'),
    (N'FIELD_DATASOURCE', N'FILTER_STRUCT'),
    (N'FIELD_DATASOURCE', N'RETURN_ITEMS'),
    (N'SYS_REPORT_PARAMETER', N'F_ID'),
    (N'SYS_REPORT_PARAMETER', N'F_EXPR'),
    (N'SYS_REPORT_PARAMETER', N'FILTER_TEMPLATE'),
    (N'MODULES', N'FILTER'),
    (N'MODULES', N'SORT_FIELDS'),
    (N'MODULES', N'DETAIL_NO_FIELDS'),
    (N'MODULES', N'NOT_BACK_FIELDS'),
    (N'MODULES', N'NOT_BACK_FIELDS_M'),
    (N'MODULE_BUSINESS_ACTION', N'CONDITION_STRUCT'),
    (N'MODULE_BUSINESS_ACTION', N'PARAM_STRUCT'),
    (N'MODULE_BUSINESS_ACTION', N'REVERSE_STRUCT'),
    (N'MODULE_BUSINESS_ACTION_OP', N'TARGET_FIELD'),
    (N'MODULE_BUSINESS_ACTION_OP', N'SOURCE_FIELD'),
    (N'MODULE_BUSINESS_ACTION_OP', N'CONDITION_STRUCT'),
    (N'MODULE_BUSINESS_ACTION_OP', N'MATCH_STRUCT'),
    (N'MODULE_BUSINESS_ACTION_OP', N'SOURCE_TERMS_STRUCT'),
    (N'MODULE_VALIDATION_RULE', N'PARAM_STRUCT'),
    (N'MODULE_GROUPS', N'GROUP_EXP'),
    (N'REPORT', N'REPORT_FILTER'),
    (N'REPORT_SORT', N'SORT_FIELDS'),
    (N'REPORT_SORT', N'SORT_DESC'),
    (N'REPORT_SORT', N'GROUP_FIELDS'),
    (N'REPORT_METRIC', N'ROW_FILTER'),
    (N'SYS_GROUP_MODULE_PERMISSION', N'DATA_FILTER'),
    (N'SYS_USER_MODULE_PERMISSION', N'DATA_FILTER'),
    (N'TABLES', N'DF_CONDITION');

DECLARE @t int = (SELECT COUNT(*) FROM @targets);
DECLARE @j int = 1,
        @table sysname, @column sysname,
        @expr nvarchar(max), @where nvarchar(max), @sql nvarchar(max),
        @old sysname, @new sysname;

WHILE @j <= @t
BEGIN
    SELECT @table = TableName, @column = ColumnName FROM @targets WHERE Id = @j;

    -- 嵌套 REPLACE：映射表 Seq 最小者最内层（先执行），保证 FINISHED_PERSON 先于 FINISHED_TAG、
    -- CONFIRM_PERSON 先于 CONFIRM_TAG 之类的包含关系不会互相吃掉。
    SET @expr = N'[' + @column + N']';
    SET @where = N'';
    DECLARE @k int = 1, @n int = (SELECT COUNT(*) FROM @map);
    WHILE @k <= @n
    BEGIN
        SELECT @old = OldName, @new = NewName FROM @map WHERE Seq = @k;
        SET @expr = N'REPLACE(' + @expr + N', N''' + @old + N''', N''' + @new + N''')';
        SET @where = @where + CASE WHEN @k = 1 THEN N'' ELSE N' OR ' END + N'CHARINDEX(N''' + @old + N''', [' + @column + N']) > 0';
        SET @k += 1;
    END

    SET @sql = N'UPDATE dbo.' + QUOTENAME(@table) + N' SET ' + QUOTENAME(@column) + N' = ' + @expr
             + N' WHERE [' + @column + N'] IS NOT NULL AND (' + @where + N');';

    EXEC sys.sp_executesql @sql;
    SET @j += 1;
END
PRINT N'[451] 配置面文本替换完成（F_ID 与结构化条件、过滤模板、排序分组、数据范围、影响链）';

-- ---------------------------------------------------------------------------
-- 5) 字段元数据标签归一
-- ---------------------------------------------------------------------------
UPDATE dbo.FIELDS SET F_DESC = N'建立人'   WHERE F_ID = N'CREATED_BY';
UPDATE dbo.FIELDS SET F_DESC = N'建立时间' WHERE F_ID = N'CREATED_TIME' AND T_ID <> N'AUDIT_EVENT';
UPDATE dbo.FIELDS SET F_DESC = N'修改人'   WHERE F_ID = N'MODIFIED_BY';
UPDATE dbo.FIELDS SET F_DESC = N'修改时间' WHERE F_ID = N'MODIFIED_TIME';
UPDATE dbo.FIELDS SET F_DESC = N'批核人'   WHERE F_ID = N'APPROVED_BY';
UPDATE dbo.FIELDS SET F_DESC = N'批核时间' WHERE F_ID = N'APPROVED_TIME';
UPDATE dbo.FIELDS SET F_DESC = N'批核状态' WHERE F_ID = N'APPROVED';
UPDATE dbo.FIELDS SET F_DESC = N'结案人'   WHERE F_ID = N'FINISHED_BY';
UPDATE dbo.FIELDS SET F_DESC = N'结案时间' WHERE F_ID = N'FINISHED_TIME';
UPDATE dbo.FIELDS SET F_DESC = N'结案状态' WHERE F_ID = N'FINISHED';

-- ---------------------------------------------------------------------------
-- 6) 表单版式里的生命周期行：ADR-042 起生命周期字段不再参与版式，
--    这几行本就不渲染；改名后会变成指向不存在字段的孤儿行，直接删除。
-- ---------------------------------------------------------------------------
DELETE lay
FROM dbo.MODULE_FORM_LAYOUT AS lay
JOIN @map m ON m.OldName = lay.F_ID;

PRINT CONCAT(N'[451] 清理版式生命周期行：', @@ROWCOUNT, N' 行');

-- ---------------------------------------------------------------------------
-- 7) 视图与存储过程重建
--
-- 直接引用旧列名的库对象只有 10 个视图与 1 个存储过程。改名后它们的定义文本仍是旧名，
-- 仅 sp_refreshview 不够——要把定义里的旧 token 换掉再把首部 CREATE 改成 ALTER 重放。
-- ---------------------------------------------------------------------------
DECLARE @modules TABLE (Id int IDENTITY(1, 1) PRIMARY KEY, ObjectName sysname NOT NULL);
INSERT @modules (ObjectName) VALUES
    (N'V_CLIENT'),
    (N'V_COP_ACCOUNT_M'),
    (N'V_COP_ORDER_D_UNDELIVERED'),
    (N'V_COP_ORDER_D_UNPRODUCED'),
    (N'V_COP_SEND_D_UNRECONCILED'),
    (N'V_INV_MONTH_SNAPSHOT_RECON'),
    (N'V_PUR_DUE_M'),
    (N'V_PUR_RECEIVE_D_UNRECONCILED'),
    (N'V_TODAY_SEND'),
    (N'V_UNUSUAL_ORDER'),
    (N'P_HRM_WAGE_CALC');

DECLARE @m int = (SELECT COUNT(*) FROM @modules);
DECLARE @p int = 1, @objectName sysname, @definition nvarchar(max), @rewritten nvarchar(max),
        @keywordPosition int, @keywordTail nvarchar(40);

WHILE @p <= @m
BEGIN
    SELECT @objectName = ObjectName FROM @modules WHERE Id = @p;

    SELECT @definition = sm.definition
    FROM sys.sql_modules sm
    WHERE sm.object_id = OBJECT_ID(N'dbo.' + @objectName);

    IF @definition IS NULL
    BEGIN
        DECLARE @missing nvarchar(300) = N'找不到需要重建的库对象：dbo.' + @objectName;
        THROW 50453, @missing, 1;
    END

    SET @rewritten = @definition;
    SET @k = 1;
    WHILE @k <= @n
    BEGIN
        SELECT @old = OldName, @new = NewName FROM @map WHERE Seq = @k;
        SET @rewritten = REPLACE(@rewritten, @old, @new);
        SET @k += 1;
    END

    -- 定义首部的 CREATE 换成 ALTER：SP/视图都按“已存在对象”重放，不重建、不丢权限。
    SET @keywordPosition = CHARINDEX(N'CREATE', @rewritten);
    SET @keywordTail = UPPER(LTRIM(SUBSTRING(@rewritten, @keywordPosition + 6, 40)));
    IF @keywordPosition = 0
       OR (@keywordTail NOT LIKE N'VIEW%' AND @keywordTail NOT LIKE N'PROCEDURE%')
    BEGIN
        DECLARE @badShape nvarchar(300) = N'库对象定义的 CREATE 关键字无法定位，拒绝盲目重放：dbo.' + @objectName;
        THROW 50454, @badShape, 1;
    END

    SET @rewritten = STUFF(@rewritten, @keywordPosition, 6, N'ALTER ');
    EXEC sys.sp_executesql @rewritten;
    SET @p += 1;
END
PRINT CONCAT(N'[451] 重建视图/存储过程：', @m, N' 个对象');

-- ---------------------------------------------------------------------------
-- 8) 收尾复核：视图可查询（引用旧列名会直接报错，这里让迁移当场失败）
-- ---------------------------------------------------------------------------
DECLARE @probe TABLE (Id int IDENTITY(1, 1) PRIMARY KEY, ObjectName sysname NOT NULL);
INSERT @probe (ObjectName) VALUES
    (N'V_CLIENT'), (N'V_COP_ACCOUNT_M'), (N'V_COP_ORDER_D_UNDELIVERED'), (N'V_COP_ORDER_D_UNPRODUCED'),
    (N'V_COP_SEND_D_UNRECONCILED'), (N'V_INV_MONTH_SNAPSHOT_RECON'), (N'V_PUR_DUE_M'),
    (N'V_PUR_RECEIVE_D_UNRECONCILED'), (N'V_TODAY_SEND'), (N'V_UNUSUAL_ORDER');

DECLARE @q int = 1, @probeName sysname, @probeSql nvarchar(300);
WHILE @q <= (SELECT COUNT(*) FROM @probe)
BEGIN
    SELECT @probeName = ObjectName FROM @probe WHERE Id = @q;
    SET @probeSql = N'SELECT TOP (1) 1 FROM dbo.' + QUOTENAME(@probeName);
    EXEC sys.sp_executesql @probeSql;
    SET @q += 1;
END
PRINT N'[451] 视图复核通过';
