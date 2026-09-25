-- 模块 ID 外键列统一为 M_IDX：10 张表的 MODULE_ID 列改名。
--
-- 口径：模块号在库里的原生列名是 MODULES.M_IDX，SYSDD / SYSDH / SYSDF / SYSTEMP / TASK /
-- WF_APPROVE 等既有表同为 M_IDX；这 10 张表的 MODULE_ID 是后来引入的异名，跨表按模块号关联
-- 时必须记住"哪张表用哪个名"。统一为 M_IDX 后这一处记忆负担消失。
--
-- 只改名：不动列的类型、可空性、默认值、索引与约束对象名（索引与约束的**定义**由 sp_rename
-- 同步更新，其对象名不含列名，故不跟着改）。列说明（扩展属性）随列保留。
--
-- 时序：本迁移与使用新列名的代码同一次提交，随重启由 DbUp 落地。改名是原子的——旧二进制读
-- MODULE_ID、新代码读 M_IDX，二者不可能同时成立，因此不要在仍在运行的旧实例上手工执行本迁移。
--
-- 幂等：已是 M_IDX 的表跳过；两列都不存在说明库结构与本迁移的预期不符，报错而不是静默跳过。

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @GUARD_MESSAGE NVARCHAR(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, @GUARD_MESSAGE, 1;

BEGIN TRANSACTION;

DECLARE @targets TABLE (SEQ int IDENTITY(1, 1) PRIMARY KEY, TABLE_NAME sysname NOT NULL);
INSERT INTO @targets (TABLE_NAME) VALUES
    (N'ATTACHMENT'),
    (N'AUDIT_EVENT'),
    (N'FORM_CHOOSER_SOURCE_MEMO'),
    (N'MODULE_BUSINESS_ACTION'),
    (N'MODULE_VALIDATION_RULE'),
    (N'REPORT_INBOX'),
    (N'REPORT_SUBSCRIPTION'),
    (N'WORKBENCH_DEFINITION_SNAPSHOT'),
    (N'WORKBENCH_IDEMPOTENCY'),
    (N'WORKBENCH_MODULE_DIRTY');

-- ① 基线：改名前的索引列与外键列命中数，用于证明它们随列改名而不是被落下
DECLARE @indexBefore int, @foreignKeyBefore int;
SELECT @indexBefore = COUNT(*)
FROM sys.index_columns ic
JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
JOIN @targets t ON t.TABLE_NAME = OBJECT_NAME(ic.object_id)
WHERE c.name = N'MODULE_ID';
SELECT @foreignKeyBefore = COUNT(*)
FROM sys.foreign_key_columns fkc
JOIN sys.columns c ON c.object_id = fkc.parent_object_id AND c.column_id = fkc.parent_column_id
JOIN @targets t ON t.TABLE_NAME = OBJECT_NAME(fkc.parent_object_id)
WHERE c.name = N'MODULE_ID';

-- ② 逐表改名
DECLARE @seq int = 1, @tableName sysname, @renameSql NVARCHAR(400), @errorMessage NVARCHAR(400);
WHILE @seq <= (SELECT MAX(SEQ) FROM @targets)
BEGIN
    SELECT @tableName = TABLE_NAME FROM @targets WHERE SEQ = @seq;

    IF COL_LENGTH('dbo.' + @tableName, 'MODULE_ID') IS NOT NULL
       AND COL_LENGTH('dbo.' + @tableName, 'M_IDX') IS NULL
    BEGIN
        SET @renameSql = N'EXEC sys.sp_rename N''dbo.' + @tableName
            + N'.MODULE_ID'', N''M_IDX'', N''COLUMN'';';
        EXEC sp_executesql @renameSql;
    END
    ELSE IF COL_LENGTH('dbo.' + @tableName, 'MODULE_ID') IS NULL
        AND COL_LENGTH('dbo.' + @tableName, 'M_IDX') IS NULL
    BEGIN
        SET @errorMessage = N'dbo.' + @tableName + N' 既无 MODULE_ID 也无 M_IDX，与本迁移的预期不符。';
        THROW 55000, @errorMessage, 1;
    END;

    SET @seq += 1;
END;

-- ③ 后置自证：旧列名全部消失、新列名全部就位
DECLARE @oldRemaining int, @newPresent int;
SELECT @oldRemaining = COUNT(*) FROM @targets t
WHERE COL_LENGTH('dbo.' + t.TABLE_NAME, 'MODULE_ID') IS NOT NULL;
SELECT @newPresent = COUNT(*) FROM @targets t
WHERE COL_LENGTH('dbo.' + t.TABLE_NAME, 'M_IDX') IS NOT NULL;

IF @oldRemaining > 0 OR @newPresent < (SELECT COUNT(*) FROM @targets)
    THROW 55001, N'模块 ID 列改名未全部完成：仍有旧列残留或新列缺失。', 1;

-- ④ 后置自证：依赖该列的约束定义已随改名同步（残留旧名会让写入直接报错）
IF EXISTS (
    SELECT 1
    FROM sys.check_constraints cc
    JOIN @targets t ON OBJECT_ID('dbo.' + t.TABLE_NAME) = cc.parent_object_id
    WHERE OBJECT_DEFINITION(cc.object_id) LIKE N'%MODULE_ID%')
    THROW 55002, N'CHECK 约束定义仍引用旧列名 MODULE_ID。', 1;

-- ⑤ 后置自证：索引列与外键列跟着改名，命中数与基线一致
DECLARE @indexAfter int, @foreignKeyAfter int;
SELECT @indexAfter = COUNT(*)
FROM sys.index_columns ic
JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
JOIN @targets t ON t.TABLE_NAME = OBJECT_NAME(ic.object_id)
WHERE c.name = N'M_IDX';
SELECT @foreignKeyAfter = COUNT(*)
FROM sys.foreign_key_columns fkc
JOIN sys.columns c ON c.object_id = fkc.parent_object_id AND c.column_id = fkc.parent_column_id
JOIN @targets t ON t.TABLE_NAME = OBJECT_NAME(fkc.parent_object_id)
WHERE c.name = N'M_IDX';

IF @indexAfter <> @indexBefore OR @foreignKeyAfter <> @foreignKeyBefore
    THROW 55003, N'索引列或外键列未随列改名同步：命中数与改名前不一致。', 1;

COMMIT TRANSACTION;

SELECT N'MODULE_ID 残留' AS CHECK_NAME, @oldRemaining AS CNT
UNION ALL SELECT N'M_IDX 就位数', @newPresent
UNION ALL SELECT N'M_IDX 索引列命中数', @indexAfter
UNION ALL SELECT N'M_IDX 外键列命中数', @foreignKeyAfter;
