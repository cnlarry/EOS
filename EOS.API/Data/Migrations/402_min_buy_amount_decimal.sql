-- 402：最小采购金额改为金额族标准类型（int -> decimal(18,4)）
-- MIN_BUY_AMOUNT 是「最小采购金额」——采购总额低于该值供应商不接单，属 AMOUNT 族。
-- 原类型 int 表达不了小数金额，且 FIELDS 里没有 NUMERIC_ROLE 登记。
-- int -> decimal 不丢数据（存量 436 行全为 0），可安全就地改。
-- 幂等：列已是 decimal 且已登记时，各步均为空操作。

DECLARE @df sysname, @sql nvarchar(max);

SELECT @df = dc.name
FROM sys.default_constraints dc
JOIN sys.columns c ON dc.parent_object_id = c.object_id AND dc.parent_column_id = c.column_id
JOIN sys.objects o ON c.object_id = o.object_id
WHERE o.name = N'SUPPLIER' AND c.name = N'MIN_BUY_AMOUNT';

IF @df IS NOT NULL
BEGIN
    SET @sql = N'ALTER TABLE [dbo].[SUPPLIER] DROP CONSTRAINT ' + QUOTENAME(@df);
    EXEC sp_executesql @sql;
END
GO

IF EXISTS (SELECT 1
           FROM sys.columns c
           JOIN sys.objects o ON c.object_id = o.object_id
           WHERE o.name = N'SUPPLIER' AND c.name = N'MIN_BUY_AMOUNT'
             AND TYPE_NAME(c.user_type_id) = 'int')
BEGIN
    ALTER TABLE [dbo].[SUPPLIER] ALTER COLUMN [MIN_BUY_AMOUNT] decimal(18,4) NULL;
END
GO

IF NOT EXISTS (SELECT 1
               FROM sys.default_constraints dc
               JOIN sys.columns c ON dc.parent_object_id = c.object_id AND dc.parent_column_id = c.column_id
               JOIN sys.objects o ON c.object_id = o.object_id
               WHERE o.name = N'SUPPLIER' AND c.name = N'MIN_BUY_AMOUNT')
BEGIN
    ALTER TABLE [dbo].[SUPPLIER]
        ADD CONSTRAINT [DF_SUPPLIER_MIN_BUY_AMOUNT] DEFAULT ((0)) FOR [MIN_BUY_AMOUNT];
END
GO

UPDATE f
SET f.F_TYPE = N'decimal', f.NUMERIC_ROLE = N'AMOUNT'
FROM [dbo].[FIELDS] f
WHERE f.T_ID = N'SUPPLIER' AND f.F_ID = N'MIN_BUY_AMOUNT';
GO
