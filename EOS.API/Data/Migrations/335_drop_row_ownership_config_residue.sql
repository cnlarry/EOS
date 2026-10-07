-- ============================================================================
-- EOS.ERP migration 335: 清掉配置面里引用行归属三列的残留
-- ----------------------------------------------------------------------------
-- 迁移 334 把 CI / OWNER / OWNER_G 从全库物理删列（DEPT.CI / SYSDN.CI 是部门与员工的
-- 公司归属、WF_APPROVE.OWNER 是审批待办人，三者保留），并清掉了 FIELDS /
-- FIELD_DATASOURCE / SYSQL_* / SYSQR_* 里**以这三列为被描述字段**的登记。
-- 还有三处是"配置里引用了这三列"，不在那批清理的口径内：
--
--   ① 表单版式行（MODULE_FORM_LAYOUT）：773 行引用了这三列，其中 771 行**在删列前就已是
--      "字段未注册"的幽灵**——FIELDS 里本就没有这些表的对应登记（版式行引用的是
--      PRODUCT.CI 这类从未登记过的列），check-form-layout 的幽灵检测一直报着这 771 条。
--   ② 选择器回填映射（FIELD_DATASOURCE.RETURN_ITEMS 里的 target）：PRODUCT.PRO_NO 的
--      数据源把回填目标写成 `CI`，该列已不存在 ⇒ 发布校验报"选择器数据源损坏"、
--      模块 1201 无法发布。
--   ③ 模块分组表达式（MODULES.GROUP_EXP1..5）：模块 1401 的 GROUP_EXP1 / GROUP_EXP3
--      引用 CLIENT.OWNER。表达式编译时该列已不存在 ⇒ 用户一选中该分组档位即 403。
--
-- 判定一律落在"目标列在物理上是否存在"（COL_LENGTH），因此 DEPT.CI / SYSDN.CI /
-- WF_APPROVE.OWNER 上的合法引用（含 DEPT/SYSDN 公司选择器的回填目标 CI）原样保留。
-- 幂等：清完即 0 命中。
-- ============================================================================

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @GUARD_MESSAGE NVARCHAR(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, @GUARD_MESSAGE, 1;

DECLARE @Quote CHAR(1) = CHAR(34);

PRINT N'== 清理配置面里引用行归属三列的残留 ==';

BEGIN TRANSACTION;

-- ① 前置自证：三列在物理上必须已经不在（本迁移的前提是 334 已落地），保留列必须仍在
IF EXISTS (SELECT 1 FROM sys.columns c
           JOIN sys.objects o ON o.object_id = c.object_id AND o.type = N'U'
           JOIN sys.schemas s ON o.schema_id = s.schema_id
           WHERE s.name = N'dbo'
             AND (   (c.name = N'CI'      AND o.name NOT IN (N'DEPT', N'SYSDN'))
                  OR (c.name = N'OWNER'   AND o.name <> N'WF_APPROVE')
                  OR (c.name = N'OWNER_G') ))
    THROW 53500, N'归属三列仍在库中：迁移 334 未落地，本迁移的前提不成立。', 1;

IF COL_LENGTH(N'dbo.DEPT', N'CI') IS NULL
    OR COL_LENGTH(N'dbo.SYSDN', N'CI') IS NULL
    OR COL_LENGTH(N'dbo.WF_APPROVE', N'OWNER') IS NULL
    THROW 53501, N'保留列不齐（DEPT.CI / SYSDN.CI / WF_APPROVE.OWNER）：本迁移的前提不成立。', 1;

-- ② 留痕：版式行
DECLARE @LayoutTrail INT = (SELECT COUNT(*) FROM dbo.MODULE_FORM_LAYOUT l
    WHERE l.F_ID IN (N'CI', N'OWNER', N'OWNER_G')
      AND NOT EXISTS (SELECT 1 FROM dbo.FIELDS f
                      WHERE LTRIM(RTRIM(f.T_ID)) = LTRIM(RTRIM(l.T_ID)) AND f.F_ID = l.F_ID));
PRINT N'  表单版式：待删除 ' + CONVERT(NVARCHAR(20), @LayoutTrail) + N' 行';

-- ③ 清版式行：FIELDS 已无登记的（这些行指向的字段在设计器里既选不出来、也渲染不出东西）
DELETE l FROM dbo.MODULE_FORM_LAYOUT l
WHERE l.F_ID IN (N'CI', N'OWNER', N'OWNER_G')
  AND NOT EXISTS (SELECT 1 FROM dbo.FIELDS f
                  WHERE LTRIM(RTRIM(f.T_ID)) = LTRIM(RTRIM(l.T_ID)) AND f.F_ID = l.F_ID);

-- ④ 清选择器回填映射里指向已删列的目标项
--    判据**限定在这三列上**并叠加"该列在物理上不存在"：RETURN_ITEMS 里另有若干历史坏映射
--    （target 写成了来源表的列名之类），它们不是本次退役造成的，不在本迁移的处理范围。
DECLARE @ReturnTrail NVARCHAR(MAX) = N'';
SELECT @ReturnTrail = @ReturnTrail + N'    ' + LTRIM(RTRIM(d.T_ID)) + N'.' + LTRIM(RTRIM(d.F_ID)) + CHAR(10)
FROM dbo.FIELD_DATASOURCE d
WHERE d.RETURN_ITEMS IS NOT NULL AND d.RETURN_ITEMS <> N''
  AND EXISTS (SELECT 1 FROM OPENJSON(d.RETURN_ITEMS) WITH ([target] NVARCHAR(200) '$.target') t
              WHERE LTRIM(RTRIM(t.[target])) IN (N'CI', N'OWNER', N'OWNER_G')
                AND COL_LENGTH(N'dbo.' + LTRIM(RTRIM(d.T_ID)), LTRIM(RTRIM(t.[target]))) IS NULL);
IF LEN(@ReturnTrail) > 0
    PRINT N'  选择器回填映射（目标为已删的行归属列）：' + CHAR(10) + @ReturnTrail;

UPDATE d
SET d.RETURN_ITEMS = (
        SELECT j.[target], j.[column]
        FROM OPENJSON(d.RETURN_ITEMS) WITH ([target] NVARCHAR(200) '$.target', [column] NVARCHAR(200) '$.column') j
        WHERE j.[target] IS NOT NULL AND LTRIM(RTRIM(j.[target])) <> N''
          AND NOT (LTRIM(RTRIM(j.[target])) IN (N'CI', N'OWNER', N'OWNER_G')
                   AND COL_LENGTH(N'dbo.' + LTRIM(RTRIM(d.T_ID)), LTRIM(RTRIM(j.[target]))) IS NULL)
        FOR JSON PATH)
FROM dbo.FIELD_DATASOURCE d
WHERE d.RETURN_ITEMS IS NOT NULL AND d.RETURN_ITEMS <> N''
  AND EXISTS (SELECT 1 FROM OPENJSON(d.RETURN_ITEMS) WITH ([target] NVARCHAR(200) '$.target') t
              WHERE LTRIM(RTRIM(t.[target])) IN (N'CI', N'OWNER', N'OWNER_G')
                AND COL_LENGTH(N'dbo.' + LTRIM(RTRIM(d.T_ID)), LTRIM(RTRIM(t.[target]))) IS NULL);

-- ⑤ 清模块分组表达式里引用已删列的档位（连同档位开关与显示名一起清，避免留下选不了的空档）
DECLARE @Index INT = 1;
WHILE @Index <= 5
BEGIN
    DECLARE @Slot NVARCHAR(2) = CONVERT(NVARCHAR(2), @Index);
    DECLARE @Sql NVARCHAR(MAX) = N'
UPDATE dbo.MODULES
SET GROUP' + @Slot + N' = 0, GROUP_EXP' + @Slot + N' = NULL, GROUP_DESC' + @Slot + N' = NULL
WHERE MASTER_TABLE IS NOT NULL AND LTRIM(RTRIM(MASTER_TABLE)) <> N''''
  AND (   (CHARINDEX(N''.OWNER'', ISNULL(GROUP_EXP' + @Slot + N', N'''')) > 0
           AND COL_LENGTH(N''dbo.'' + LTRIM(RTRIM(MASTER_TABLE)), N''OWNER'') IS NULL)
       OR (CHARINDEX(N''.OWNER_G'', ISNULL(GROUP_EXP' + @Slot + N', N'''')) > 0
           AND COL_LENGTH(N''dbo.'' + LTRIM(RTRIM(MASTER_TABLE)), N''OWNER_G'') IS NULL)
       OR (CHARINDEX(N''.CI'', ISNULL(GROUP_EXP' + @Slot + N', N'''')) > 0
           AND COL_LENGTH(N''dbo.'' + LTRIM(RTRIM(MASTER_TABLE)), N''CI'') IS NULL));';
    EXEC sp_executesql @Sql;
    SET @Index += 1;
END

-- ⑥ 后置自证：不得再有引用已删列的配置残留
IF EXISTS (SELECT 1 FROM dbo.MODULE_FORM_LAYOUT l
           WHERE l.F_ID IN (N'CI', N'OWNER', N'OWNER_G')
             AND NOT EXISTS (SELECT 1 FROM dbo.FIELDS f
                             WHERE LTRIM(RTRIM(f.T_ID)) = LTRIM(RTRIM(l.T_ID)) AND f.F_ID = l.F_ID))
    THROW 53510, N'表单版式里仍有三列的幽灵行。', 1;

IF EXISTS (SELECT 1 FROM dbo.FIELD_DATASOURCE d
           WHERE d.RETURN_ITEMS IS NOT NULL AND d.RETURN_ITEMS <> N''
             AND EXISTS (SELECT 1 FROM OPENJSON(d.RETURN_ITEMS) WITH ([target] NVARCHAR(200) '$.target') t
                         WHERE LTRIM(RTRIM(t.[target])) IN (N'CI', N'OWNER', N'OWNER_G')
                           AND COL_LENGTH(N'dbo.' + LTRIM(RTRIM(d.T_ID)), LTRIM(RTRIM(t.[target]))) IS NULL))
    THROW 53511, N'选择器回填映射里仍有指向已删行归属列的目标。', 1;

IF EXISTS (SELECT 1 FROM dbo.MODULES
           WHERE (CHARINDEX(N'.OWNER', ISNULL(GROUP_EXP1,N'') + ISNULL(GROUP_EXP2,N'') + ISNULL(GROUP_EXP3,N'')
                          + ISNULL(GROUP_EXP4,N'') + ISNULL(GROUP_EXP5,N'')) > 0
                  AND COL_LENGTH(N'dbo.' + LTRIM(RTRIM(MASTER_TABLE)), N'OWNER') IS NULL)
              OR (CHARINDEX(N'.OWNER_G', ISNULL(GROUP_EXP1,N'') + ISNULL(GROUP_EXP2,N'') + ISNULL(GROUP_EXP3,N'')
                          + ISNULL(GROUP_EXP4,N'') + ISNULL(GROUP_EXP5,N'')) > 0
                  AND COL_LENGTH(N'dbo.' + LTRIM(RTRIM(MASTER_TABLE)), N'OWNER_G') IS NULL)
              OR (CHARINDEX(N'.CI', ISNULL(GROUP_EXP1,N'') + ISNULL(GROUP_EXP2,N'') + ISNULL(GROUP_EXP3,N'')
                          + ISNULL(GROUP_EXP4,N'') + ISNULL(GROUP_EXP5,N'')) > 0
                  AND COL_LENGTH(N'dbo.' + LTRIM(RTRIM(MASTER_TABLE)), N'CI') IS NULL))
    THROW 53512, N'仍有模块分组表达式引用已删列。', 1;

COMMIT TRANSACTION;

PRINT N'== 配置面残留清理完成（版式行 / 选择器回填映射 / 分组表达式）==';
