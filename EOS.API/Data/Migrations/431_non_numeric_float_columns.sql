-- ============================================================================
-- 6 个「本就不该是数值」的 float 列：按真实语义改类型
--
-- 这 6 列在 ADR-038 的收口里被登记为 `NUMERIC_ROLE='NON_NUMERIC'`、保持 float 未动，
-- 理由是"它们要改的是类型语义（布尔/类别），不是数值精度，属另案"。本迁移就是那件另案。
--
-- 依据：
--   · `HR_WAGE_D.WAGE_ITEM37`、`HRM_WAGE_D.WAGE_ITEM31/36` —— 字段说明是「有无加班费」「有无全勤奖」，
--     同表其余 100 多个 `WAGE_ITEM*` 全是金额/天数/小时，只有这几列是 float：它们是被塞进金额列堆里的开关；
--   · `QC_SAMPLE_M.SAMPLE_SG/LC/XD` —— 参考系统 `ERP/QC/Sample.aspx` 里「量产样」「限度样」两格
--     明确 `TextDataType="Decimal"`（要数字），物理列也一直是数值；按数量族 `decimal(18,6)` 收口，
--     不改成文本（那等于把"本来存不进文字"的列打开成自由文本）。
--
-- 连带处理：
--   · 物理列改类型（`ALTER COLUMN` 前先拆默认约束，改完按原名重建）；
--   · `FIELDS.F_TYPE` 同步（bit / decimal），否则录入控件与精度校验仍按旧类型走；
--   · `NUMERIC_ROLE`：转 bit 的置 NULL（不再是数值列，留着会撞"已登记语义必须是数值类"），
--     转 decimal 的登记 `QTY`；
--   · 标脏使用这些表的 6 个模块，重新发布后界面控件才切换。
--
-- 存量数据：3 个工资列所在表 `HRM_WAGE_D` 0 行、`HR_WAGE_D` 10 行且该列全为 0；
-- `QC_SAMPLE_M` 5 行且三列全为 0——转换无精度损失、无真值语义变化。
-- ============================================================================
SET NOCOUNT ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

DECLARE @targets TABLE (
    Tbl sysname NOT NULL,
    Col sysname NOT NULL,
    NewType NVARCHAR(60) NOT NULL,
    NewFieldType NVARCHAR(20) NOT NULL,
    NewRole NVARCHAR(16) NULL,
    PRIMARY KEY (Tbl, Col));

INSERT INTO @targets (Tbl, Col, NewType, NewFieldType, NewRole) VALUES
    (N'HR_WAGE_D',  N'WAGE_ITEM37', N'bit',           N'bit',     NULL),
    (N'HRM_WAGE_D', N'WAGE_ITEM31', N'bit',           N'bit',     NULL),
    (N'HRM_WAGE_D', N'WAGE_ITEM36', N'bit',           N'bit',     NULL),
    (N'QC_SAMPLE_M', N'SAMPLE_SG',  N'decimal(18,6)', N'decimal', N'QTY'),
    (N'QC_SAMPLE_M', N'SAMPLE_LC',  N'decimal(18,6)', N'decimal', N'QTY'),
    (N'QC_SAMPLE_M', N'SAMPLE_XD',  N'decimal(18,6)', N'decimal', N'QTY');

-- 现状断言：6 列必须都还是 float，字段行必须都在。防止重复执行或假设漂移后做出错事。
IF EXISTS (
    SELECT 1
      FROM @targets t
      LEFT JOIN sys.columns c
        ON c.object_id = OBJECT_ID(N'dbo.' + QUOTENAME(t.Tbl)) AND c.name = t.Col
     WHERE c.column_id IS NULL OR TYPE_NAME(c.user_type_id) <> N'float')
BEGIN
    RAISERROR(N'[431] 目标列不全是 float（或物理列缺失），回滚：先核对当前结构', 16, 1);
    ROLLBACK TRANSACTION;
    RETURN;
END

IF EXISTS (
    SELECT 1
      FROM @targets t
     WHERE NOT EXISTS (SELECT 1 FROM dbo.FIELDS f
                        WHERE LTRIM(RTRIM(f.T_ID)) = t.Tbl AND LTRIM(RTRIM(f.F_ID)) = t.Col))
BEGIN
    RAISERROR(N'[431] 有目标列缺 FIELDS 元数据行，回滚', 16, 1);
    ROLLBACK TRANSACTION;
    RETURN;
END

-- 逐列：拆默认约束 → 改类型 → 按原名重建默认值（ADR-038 §5.2 第 2 步：默认约束会挡住 ALTER COLUMN）
DECLARE @tbl sysname, @col sysname, @newType NVARCHAR(60), @dfName sysname, @sql NVARCHAR(MAX);
DECLARE cur CURSOR LOCAL FAST_FORWARD FOR SELECT Tbl, Col, NewType FROM @targets;
OPEN cur;
FETCH NEXT FROM cur INTO @tbl, @col, @newType;
WHILE @@FETCH_STATUS = 0
BEGIN
    SET @dfName = NULL;
    SELECT @dfName = d.name
      FROM sys.default_constraints d
      JOIN sys.columns c ON c.object_id = d.parent_object_id AND c.column_id = d.parent_column_id
     WHERE d.parent_object_id = OBJECT_ID(N'dbo.' + QUOTENAME(@tbl)) AND c.name = @col;

    IF @dfName IS NOT NULL
    BEGIN
        SET @sql = N'ALTER TABLE dbo.' + QUOTENAME(@tbl) + N' DROP CONSTRAINT ' + QUOTENAME(@dfName);
        EXEC(@sql);
    END

    SET @sql = N'ALTER TABLE dbo.' + QUOTENAME(@tbl) + N' ALTER COLUMN ' + QUOTENAME(@col)
             + N' ' + @newType + N' NULL';
    EXEC(@sql);

    IF @dfName IS NOT NULL
    BEGIN
        SET @sql = N'ALTER TABLE dbo.' + QUOTENAME(@tbl) + N' ADD CONSTRAINT ' + QUOTENAME(@dfName)
                 + N' DEFAULT ((0)) FOR ' + QUOTENAME(@col);
        EXEC(@sql);
    END

    FETCH NEXT FROM cur INTO @tbl, @col, @newType;
END
CLOSE cur;
DEALLOCATE cur;

-- 元数据同步：展示类型与数值语义
UPDATE f
   SET F_TYPE = t.NewFieldType,
       NUMERIC_ROLE = t.NewRole,
       LAST_UPDATE_BY = N'EOS-MIG',
       LAST_UPDATE_DATE = SYSDATETIME()
  FROM dbo.FIELDS f
  JOIN @targets t
    ON LTRIM(RTRIM(f.T_ID)) = t.Tbl AND LTRIM(RTRIM(f.F_ID)) = t.Col;

-- 收口断言 ①：物理类型
IF EXISTS (
    SELECT 1 FROM @targets t
      JOIN sys.columns c ON c.object_id = OBJECT_ID(N'dbo.' + QUOTENAME(t.Tbl)) AND c.name = t.Col
     WHERE LOWER(TYPE_NAME(c.user_type_id)) <> CASE WHEN t.NewType = N'bit' THEN N'bit' ELSE N'decimal' END)
BEGIN
    RAISERROR(N'[431] 物理类型未按目标改到位，回滚', 16, 1);
    ROLLBACK TRANSACTION;
    RETURN;
END

-- 收口断言 ②：元数据（F_TYPE 与 NUMERIC_ROLE 同口径）
IF EXISTS (
    SELECT 1
      FROM @targets t
      JOIN dbo.FIELDS f ON LTRIM(RTRIM(f.T_ID)) = t.Tbl AND LTRIM(RTRIM(f.F_ID)) = t.Col
     WHERE LOWER(ISNULL(f.F_TYPE, N'')) <> LOWER(t.NewFieldType)
        OR ISNULL(f.NUMERIC_ROLE, N'') <> ISNULL(t.NewRole, N''))
BEGIN
    RAISERROR(N'[431] FIELDS 的 F_TYPE/NUMERIC_ROLE 与目标不一致，回滚', 16, 1);
    ROLLBACK TRANSACTION;
    RETURN;
END

-- 收口断言 ③：默认约束必须仍在（拆完要重建，不能悄悄丢掉 0 默认值）
IF EXISTS (
    SELECT 1 FROM @targets t
     WHERE NOT EXISTS (
            SELECT 1 FROM sys.default_constraints d
              JOIN sys.columns c ON c.object_id = d.parent_object_id AND c.column_id = d.parent_column_id
             WHERE d.parent_object_id = OBJECT_ID(N'dbo.' + QUOTENAME(t.Tbl)) AND c.name = t.Col))
BEGIN
    RAISERROR(N'[431] 有默认约束未重建，回滚', 16, 1);
    ROLLBACK TRANSACTION;
    RETURN;
END

-- 标脏：重新发布后录入控件才切换（bit → 复选框、decimal → 数字框）
MERGE dbo.WORKBENCH_MODULE_DIRTY AS t
USING (SELECT M_IDX FROM (VALUES
        (3307), (180309), (180310), (1803091), (1803101), (180504)) v(M_IDX)) AS s
   ON t.M_IDX = s.M_IDX
WHEN MATCHED THEN
    UPDATE SET DIRTY_TAG = 1, LAST_MODIFIED_BY = N'EOS-MIG', LAST_MODIFIED_AT = SYSDATETIME()
WHEN NOT MATCHED BY TARGET THEN
    INSERT (M_IDX, DIRTY_TAG, LAST_MODIFIED_BY, LAST_MODIFIED_AT)
    VALUES (s.M_IDX, 1, N'EOS-MIG', SYSDATETIME());

PRINT N'[431] 6 列已改类型（3 个 bit / 3 个 decimal(18,6)），元数据同步、默认约束已重建、标脏 6 个模块。';

COMMIT;
