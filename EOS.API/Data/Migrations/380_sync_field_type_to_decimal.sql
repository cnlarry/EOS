-- 380：物理列已是 decimal 的字段，同步 FIELDS.F_TYPE
-- 批 B（369-378）把 429 列改为 decimal，但 F_TYPE 仍登记为 float（424 个）；
-- 另有 3 个字段（SFC_PROCEDURE 的 PERSON_COST_UNIT / PERSON_HOUR_UNIT / PERSON_UNIT_HOUR）
-- 登记为 nvarchar，物理列却是 decimal——元数据本就写错，一并修正。
-- 元数据不同步会让精度校验（RecordPayloadValidator 只认 decimal/numeric）继续跳过这些字段。
-- 幂等：仅当物理列确为 decimal 且登记不是数值类型时更新。

UPDATE f
SET f.F_TYPE = N'decimal'
FROM [dbo].[FIELDS] AS f
WHERE f.F_TYPE IN (N'float', N'nvarchar')
  AND EXISTS (
      SELECT 1
      FROM sys.columns AS c
      JOIN sys.objects AS o ON c.object_id = o.object_id AND o.type = 'U'
      JOIN sys.schemas AS s ON o.schema_id = s.schema_id
      WHERE s.name = N'dbo' AND o.name = f.T_ID AND c.name = f.F_ID
        AND TYPE_NAME(c.user_type_id) = 'decimal');
GO
