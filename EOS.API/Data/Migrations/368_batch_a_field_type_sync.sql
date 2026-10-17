-- 368：批 A 的元数据类型同步（F_TYPE: float -> decimal）
-- 迁移 359-367 已把物理列改为 decimal；FIELDS.F_TYPE 是类型登记，必须同步，
-- 否则 RecordPayloadValidator 的精度校验（只对 decimal/numeric 生效）仍会跳过这批字段。
-- 幂等：仅当物理列确为 decimal 且登记仍为 float 时更新。

UPDATE f
SET f.F_TYPE = N'decimal'
FROM [dbo].[FIELDS] AS f
WHERE (LEFT(f.T_ID, 4) = N'COP_' OR LEFT(f.T_ID, 4) = N'PUR_')
  AND f.F_TYPE = N'float'
  AND EXISTS (
      SELECT 1
      FROM sys.columns AS c
      JOIN sys.objects AS o ON c.object_id = o.object_id AND o.type = 'U'
      JOIN sys.schemas AS s ON o.schema_id = s.schema_id
      WHERE s.name = N'dbo' AND o.name = f.T_ID AND c.name = f.F_ID
        AND TYPE_NAME(c.user_type_id) = 'decimal');
GO
