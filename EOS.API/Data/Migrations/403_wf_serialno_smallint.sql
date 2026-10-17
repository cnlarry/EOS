-- 403：工作流项次改为整型（float -> smallint）
-- FIELDS 已把它登记为 F_TYPE='smallint'，物理列却是 float——元数据与列不一致。
-- 该表为空（0 行），float -> smallint 没有取整风险。
-- NUMERIC_ROLE 保持 NON_NUMERIC：项次是标识符，不参与数值计算。
-- 幂等：列已是 smallint 时跳过。

IF EXISTS (SELECT 1
           FROM sys.columns c
           JOIN sys.objects o ON c.object_id = o.object_id
           WHERE o.name = N'WF_FUNCTION_INFO' AND c.name = N'serialno'
             AND TYPE_NAME(c.user_type_id) = 'float')
BEGIN
    ALTER TABLE [dbo].[WF_FUNCTION_INFO] ALTER COLUMN [serialno] smallint NULL;
END
GO
