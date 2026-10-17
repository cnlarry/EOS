-- 393：batch_d 数值列类型标准化 第 5/10 批
-- 依据 docs/decisions/ADR-038-数值列的语义标准化.md 第 3 节的五族标准类型。
-- ALTER COLUMN 会被 DEFAULT 约束与包含该列的索引阻塞（错误 5074），故按表三段：拆依赖 → 改类型 → 重建。
-- 按表分片是为了避免单事务重写过多表导致文件组/日志暴涨（曾触发错误 1105）。

-- ===== HR_EVECTION_D =====
ALTER TABLE [dbo].[HR_EVECTION_D] ALTER COLUMN [EVECTION_DAYS] decimal(18,6) NULL;
ALTER TABLE [dbo].[HR_EVECTION_D] ALTER COLUMN [COUNT_DAYS] decimal(18,6) NULL;
ALTER TABLE [dbo].[HR_EVECTION_D] ALTER COLUMN [EVECTION_SUM] decimal(18,4) NULL;
GO

-- ===== HR_FOREGIFT_D =====
ALTER TABLE [dbo].[HR_FOREGIFT_D] ALTER COLUMN [FOREGIFT_SUM] decimal(18,4) NULL;
ALTER TABLE [dbo].[HR_FOREGIFT_D] ALTER COLUMN [RETURN_SUM] decimal(18,4) NULL;
GO

-- ===== HR_GRADE =====
ALTER TABLE [dbo].[HR_GRADE] ALTER COLUMN [GRADE_SUM0] decimal(18,4) NULL;
ALTER TABLE [dbo].[HR_GRADE] ALTER COLUMN [GRADE_SUM1] decimal(18,4) NULL;
ALTER TABLE [dbo].[HR_GRADE] ALTER COLUMN [GRADE_SUM2] decimal(18,4) NULL;
ALTER TABLE [dbo].[HR_GRADE] ALTER COLUMN [GRADE_SUM3] decimal(18,4) NULL;
ALTER TABLE [dbo].[HR_GRADE] ALTER COLUMN [GRADE_SUM4] decimal(18,4) NULL;
ALTER TABLE [dbo].[HR_GRADE] ALTER COLUMN [GRADE_SUM5] decimal(18,4) NULL;
ALTER TABLE [dbo].[HR_GRADE] ALTER COLUMN [GRADE_SUM6] decimal(18,4) NULL;
ALTER TABLE [dbo].[HR_GRADE] ALTER COLUMN [GRADE_SUM7] decimal(18,4) NULL;
ALTER TABLE [dbo].[HR_GRADE] ALTER COLUMN [GRADE_SUM8] decimal(18,4) NULL;
ALTER TABLE [dbo].[HR_GRADE] ALTER COLUMN [GRADE_SUM9] decimal(18,4) NULL;
GO

-- ===== HR_HOLIDAY =====
ALTER TABLE [dbo].[HR_HOLIDAY] ALTER COLUMN [COUNT_MULTIPLE] decimal(18,6) NULL;
GO

-- ===== HR_LANGUAGE =====
ALTER TABLE [dbo].[HR_LANGUAGE] ALTER COLUMN [LANGUAGE_SUM] decimal(18,4) NULL;
GO

-- ===== HR_LEAVE =====
ALTER TABLE [dbo].[HR_LEAVE] ALTER COLUMN [LEAVE_MULTIPLE] decimal(18,6) NULL;
GO

-- ===== HR_LEAVE_D =====
ALTER TABLE [dbo].[HR_LEAVE_D] ALTER COLUMN [LEAVE_DAYS] decimal(18,6) NULL;
ALTER TABLE [dbo].[HR_LEAVE_D] ALTER COLUMN [COUNT_DAYS] decimal(18,6) NULL;
GO

-- ===== HR_LEAVE_M =====
ALTER TABLE [dbo].[HR_LEAVE_M] ALTER COLUMN [COUNT_MULTIPLE] decimal(18,6) NULL;
GO

-- 同步本片各表的 F_TYPE（物理已是 decimal）
UPDATE f
SET f.F_TYPE = N'decimal'
FROM [dbo].[FIELDS] AS f
WHERE f.T_ID IN (N'HR_EVECTION_D', N'HR_FOREGIFT_D', N'HR_GRADE', N'HR_HOLIDAY', N'HR_LANGUAGE', N'HR_LEAVE', N'HR_LEAVE_D', N'HR_LEAVE_M')
  AND f.F_TYPE IN (N'float', N'nvarchar')
  AND EXISTS (
      SELECT 1 FROM sys.columns AS c
      JOIN sys.objects AS o ON c.object_id = o.object_id AND o.type = 'U'
      JOIN sys.schemas AS s ON o.schema_id = s.schema_id
      WHERE s.name = N'dbo' AND o.name = f.T_ID AND c.name = f.F_ID
        AND TYPE_NAME(c.user_type_id) = 'decimal');
GO

