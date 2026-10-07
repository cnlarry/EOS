-- ============================================================================
-- EOS.ERP migration 334: 数据归属三列（CI / OWNER / OWNER_G）全库下线
-- ----------------------------------------------------------------------------
-- 这三列自旧系统沿用，2026-09 被 ADR-013 收编进"单据生命周期列"的归属分组。
-- 真库复核（2026-10-07）表明它们已无信息量、也无独立消费端：
--
--   · CI（行公司）：单公司部署，COMPANY 只有哨兵行 DEFAULT，250 张业务表的
--     CI 恒为 'DEFAULT'（仅 PRODUCT 2 行、INV_CHECK_STOCK_M 1 行为空串，等价于无归属）。
--   · OWNER / OWNER_G（建单人 / 建单人主组）：唯一消费端是 EXEC_TAG 的 B/C/D/E
--     数据范围过滤。全库只有 5 条组权限记录在用（SYSDH：D 4 条、C 1 条），
--     其中 4 条落在没有主表的模块上，实际生效的只有组 CN 的 2 个账号 @ 模块 1204
--     （BOM_STRU_M），而这 2 人在该表上本就一行都看不到。
--   · SYSDD / SYSDH 自身也带 CI，那是另一语义域的权限标记（旧系统的公司级授权），
--     现代代码已不读（ModuleRightsRepository 只取 EXEC_TAG 等），随本迁移一并退役。
--
-- 决策：放弃"按执行范围看数据"能力，EXEC_TAG 的 B/C/D/E 收敛为 Z（保持可浏览、
-- 不再有范围语义），三列物理下线。
--
-- 保留（本迁移不动）：
--   · DEPT.CI / SYSDN.CI：部门 / 员工所属公司，是公司主档现成的锚点——支撑登录
--     company_id 声明、用户管理页"公司"列与 COMPANY_NAME 虚拟字段，与其它表那 250 个
--     常量 CI 不是同一件事。
--   · WF_APPROVE.OWNER：同名列，语义是审批待办人（QUERY_RELATION 里 JOIN
--     V_SYSDL_SYSDN），不是行归属。
--   · COMPANY 表整体（含哨兵行 DEFAULT）。
--
-- 为什么安全：sys.sql_expression_dependencies 对这三列零引用（无视图/过程/函数依赖），
-- 这三列上无索引、无外键；"谁建的单"另在 CREATE_PERSON（生命周期列，保留）上留档。
-- 命名全大写；**非幂等**：待下线列少于 200 张表即报错（缺列说明有人动过库结构，
-- 该由台账而不是守卫记着）。
-- ============================================================================

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @GUARD_MESSAGE NVARCHAR(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, @GUARD_MESSAGE, 1;

PRINT N'== 数据归属三列（CI / OWNER / OWNER_G）下线开始 ==';

BEGIN TRANSACTION;

-- ① 前置自证：要保留的三处必须在，否则库结构不是本脚本预期的起点
IF COL_LENGTH(N'dbo.DEPT', N'CI') IS NULL
    OR COL_LENGTH(N'dbo.SYSDN', N'CI') IS NULL
    OR COL_LENGTH(N'dbo.WF_APPROVE', N'OWNER') IS NULL
    THROW 53400, N'保留列不齐（DEPT.CI / SYSDN.CI / WF_APPROVE.OWNER）：本迁移的前提不成立。', 1;

-- ② 待下线清单：按表聚合成"一次 ALTER 删该表所有目标列"
DECLARE @Work TABLE (SEQ INT IDENTITY(1,1) PRIMARY KEY, TABLE_NAME SYSNAME NOT NULL, COLUMNS NVARCHAR(MAX) NOT NULL);
INSERT INTO @Work (TABLE_NAME, COLUMNS)
SELECT o.name,
       STRING_AGG(CONVERT(NVARCHAR(300), QUOTENAME(c.name)) COLLATE DATABASE_DEFAULT, N', ')
           WITHIN GROUP (ORDER BY c.name)
FROM sys.columns c
JOIN sys.objects o ON c.object_id = o.object_id AND o.type = N'U'
JOIN sys.schemas s ON o.schema_id = s.schema_id
WHERE s.name = N'dbo'
  AND (   (c.name = N'CI'      AND o.name NOT IN (N'DEPT', N'SYSDN'))
       OR (c.name = N'OWNER'   AND o.name <> N'WF_APPROVE')
       OR (c.name = N'OWNER_G') )
GROUP BY o.name;

DECLARE @TableCount INT = (SELECT COUNT(*) FROM @Work);
DECLARE @ColumnCount INT =
    (SELECT COUNT(*) FROM sys.columns c
     JOIN sys.objects o ON c.object_id = o.object_id AND o.type = N'U'
     JOIN sys.schemas s ON o.schema_id = s.schema_id
     WHERE s.name = N'dbo'
       AND (   (c.name = N'CI'      AND o.name NOT IN (N'DEPT', N'SYSDN'))
            OR (c.name = N'OWNER'   AND o.name <> N'WF_APPROVE')
            OR (c.name = N'OWNER_G') ));

PRINT N'  待下线：' + CONVERT(NVARCHAR(20), @TableCount) + N' 张表 / '
    + CONVERT(NVARCHAR(20), @ColumnCount) + N' 个列';

IF @TableCount < 200
    THROW 53401, N'待下线表不足 200 张：本迁移的前提不成立（库结构已变或已执行过）。', 1;

-- ③ 内容体检：归属域表的 CI 不得还有 DEFAULT 之外的取值
--    （空串是 nchar 定长列的填充，等于"未归属"，不算内容；SYSDD/SYSDH 的 CI 是
--      权限标记，另一语义域，不在归属域体检范围内）
DECLARE @CiCheck NVARCHAR(MAX) = N'';
SELECT @CiCheck = @CiCheck
    + N' UNION ALL SELECT N''' + w.TABLE_NAME COLLATE DATABASE_DEFAULT + N''', COUNT(*) FROM dbo.'
    + QUOTENAME(w.TABLE_NAME) + N' WITH (NOLOCK) WHERE NULLIF(LTRIM(RTRIM(ISNULL([CI], N''''))), N'''') IS NOT NULL'
    + N' AND LTRIM(RTRIM([CI])) <> N''DEFAULT'''
FROM @Work w
WHERE w.TABLE_NAME NOT IN (N'SYSDD', N'SYSDH', N'SYSDF')
  AND EXISTS (SELECT 1 FROM sys.columns c
              WHERE c.object_id = OBJECT_ID(N'dbo.' + QUOTENAME(w.TABLE_NAME)) AND c.name = N'CI');

IF @CiCheck <> N''
BEGIN
    SET @CiCheck = STUFF(@CiCheck, 1, 11, N'');
    DECLARE @CiHits TABLE (T SYSNAME NOT NULL, N INT NOT NULL);
    INSERT INTO @CiHits EXEC sp_executesql @CiCheck;
    IF EXISTS (SELECT 1 FROM @CiHits WHERE N > 0)
    BEGIN
        DECLARE @CiTrail NVARCHAR(MAX) = N'';
        SELECT @CiTrail = @CiTrail + N'    ' + T + N' 行=' + CONVERT(NVARCHAR(20), N) + CHAR(10)
        FROM @CiHits WHERE N > 0;
        PRINT N'== CI 仍有区分信息的表（非 DEFAULT 且非空）==' + CHAR(10) + @CiTrail;
        THROW 53402, N'仍有表的 CI 不是 DEFAULT：那是多公司隔离信息，删列会丢，先迁走再删。', 1;
    END
    PRINT N'  CI 体检通过：归属域表无 DEFAULT 之外的取值。';
END

-- ④ 留痕：随能力退役的填充量（"谁建的单"在 CREATE_PERSON 上另有留档，不阻断删列）
DECLARE @OwnerCheck NVARCHAR(MAX) = N'';
SELECT @OwnerCheck = @OwnerCheck
    + N' UNION ALL SELECT COUNT(*) AS T, COUNT([OWNER]) AS NN FROM dbo.' + QUOTENAME(w.TABLE_NAME) + N' WITH (NOLOCK)'
FROM @Work w
WHERE EXISTS (SELECT 1 FROM sys.columns c
              WHERE c.object_id = OBJECT_ID(N'dbo.' + QUOTENAME(w.TABLE_NAME)) AND c.name = N'OWNER');

IF @OwnerCheck <> N''
BEGIN
    SET @OwnerCheck = STUFF(@OwnerCheck, 1, 11, N'');
    DECLARE @OwnerFill TABLE (T INT NOT NULL, NN INT NOT NULL);
    INSERT INTO @OwnerFill EXEC sp_executesql @OwnerCheck;
END

DECLARE @OwnerGCheck NVARCHAR(MAX) = N'';
SELECT @OwnerGCheck = @OwnerGCheck
    + N' UNION ALL SELECT COUNT(*) AS T, COUNT([OWNER_G]) AS NN FROM dbo.' + QUOTENAME(w.TABLE_NAME) + N' WITH (NOLOCK)'
FROM @Work w
WHERE EXISTS (SELECT 1 FROM sys.columns c
              WHERE c.object_id = OBJECT_ID(N'dbo.' + QUOTENAME(w.TABLE_NAME)) AND c.name = N'OWNER_G');

IF @OwnerGCheck <> N''
BEGIN
    SET @OwnerGCheck = STUFF(@OwnerGCheck, 1, 11, N'');
    DECLARE @OwnerGFill TABLE (T INT NOT NULL, NN INT NOT NULL);
    INSERT INTO @OwnerGFill EXEC sp_executesql @OwnerGCheck;
END

-- PRINT 里不能嵌子查询（Msg 1046：只允许标量表达式），所以先落到变量再拼串。
DECLARE @OwnerFilled INT = (SELECT ISNULL(SUM(NN), 0) FROM @OwnerFill);
DECLARE @OwnerTotal INT = (SELECT ISNULL(SUM(T), 0) FROM @OwnerFill);
DECLARE @OwnerGFilled INT = (SELECT ISNULL(SUM(NN), 0) FROM @OwnerGFill);
DECLARE @OwnerGTotal INT = (SELECT ISNULL(SUM(T), 0) FROM @OwnerGFill);

PRINT N'== 随列退役的归属填充量 =='
    + CHAR(10) + N'   OWNER 有值 ' + CONVERT(NVARCHAR(20), @OwnerFilled)
    + N' / ' + CONVERT(NVARCHAR(20), @OwnerTotal) + N' 行'
    + N'；OWNER_G 有值 ' + CONVERT(NVARCHAR(20), @OwnerGFilled)
    + N' / ' + CONVERT(NVARCHAR(20), @OwnerGTotal) + N' 行'
    + CHAR(10) + N'   （建成人另有 CREATE_PERSON 留档；范围过滤能力随列下线）';

-- ⑤ 摘默认约束与列注释，再删列（默认约束必须先摘，否则 DROP COLUMN 被 5074 拦）
DECLARE @Seq INT = 1;
WHILE @Seq <= @TableCount
BEGIN
    DECLARE @TableName NVARCHAR(128) = (SELECT TABLE_NAME COLLATE DATABASE_DEFAULT FROM @Work WHERE SEQ = @Seq);
    DECLARE @ColumnList NVARCHAR(MAX) = (SELECT COLUMNS FROM @Work WHERE SEQ = @Seq);
    DECLARE @Ddl NVARCHAR(MAX) = N'';

    SELECT @Ddl = @Ddl + N'ALTER TABLE dbo.' + QUOTENAME(@TableName) + N' DROP CONSTRAINT '
                        + QUOTENAME(d.name) COLLATE DATABASE_DEFAULT + N';' + CHAR(10)
    FROM sys.default_constraints d
    JOIN sys.columns c ON d.parent_object_id = c.object_id AND d.parent_column_id = c.column_id
    WHERE d.parent_object_id = OBJECT_ID(N'dbo.' + QUOTENAME(@TableName))
      AND (   (c.name = N'CI'      AND @TableName NOT IN (N'DEPT', N'SYSDN'))
           OR (c.name = N'OWNER'   AND @TableName <> N'WF_APPROVE')
           OR (c.name = N'OWNER_G') );

    SELECT @Ddl = @Ddl + N'EXEC sys.sp_dropextendedproperty @name = N'''
                        + REPLACE(CONVERT(NVARCHAR(200), ep.name) COLLATE DATABASE_DEFAULT, N'''', N'''''')
                        + N''', @level0type = N''SCHEMA'', @level0name = N''dbo'''
                        + N', @level1type = N''TABLE'', @level1name = N''' + @TableName + N''''
                        + N', @level2type = N''COLUMN'', @level2name = N''' + c.name COLLATE DATABASE_DEFAULT + N''';' + CHAR(10)
    FROM sys.extended_properties ep
    JOIN sys.columns c ON c.object_id = ep.major_id AND c.column_id = ep.minor_id
    WHERE ep.major_id = OBJECT_ID(N'dbo.' + QUOTENAME(@TableName))
      AND ep.minor_id > 0
      AND (   (c.name = N'CI'      AND @TableName NOT IN (N'DEPT', N'SYSDN'))
           OR (c.name = N'OWNER'   AND @TableName <> N'WF_APPROVE')
           OR (c.name = N'OWNER_G') );

    SET @Ddl = @Ddl + N'ALTER TABLE dbo.' + QUOTENAME(@TableName) + N' DROP COLUMN ' + @ColumnList + N';';
    EXEC sp_executesql @Ddl;
    SET @Seq += 1;
END

-- ⑥ 元数据级联清理：登记的物理列已不存在，登记行随列走
--    （DEPT.CI / SYSDN.CI / WF_APPROVE.OWNER 的登记因列仍在而自动保留）
DECLARE @FieldsGone INT = (SELECT COUNT(*) FROM dbo.FIELDS f
    WHERE f.F_ID IN (N'CI', N'OWNER', N'OWNER_G')
      AND NOT EXISTS (SELECT 1 FROM sys.columns c
                      JOIN sys.objects o ON o.object_id = c.object_id AND o.type IN (N'U', N'V')
                      WHERE LTRIM(RTRIM(o.name)) = LTRIM(RTRIM(f.T_ID)) AND c.name = f.F_ID));
PRINT N'  随列删除的字段登记：' + CONVERT(NVARCHAR(20), @FieldsGone) + N' 行';

DELETE f FROM dbo.FIELDS f
WHERE f.F_ID IN (N'CI', N'OWNER', N'OWNER_G')
  AND NOT EXISTS (SELECT 1 FROM sys.columns c
                  JOIN sys.objects o ON o.object_id = c.object_id AND o.type IN (N'U', N'V')
                  WHERE LTRIM(RTRIM(o.name)) = LTRIM(RTRIM(f.T_ID)) AND c.name = f.F_ID);

-- 字段数据源登记（选择器来源）挂在已退役字段上的行
DELETE d FROM dbo.FIELD_DATASOURCE d
WHERE d.F_ID IN (N'CI', N'OWNER', N'OWNER_G')
  AND NOT EXISTS (SELECT 1 FROM dbo.FIELDS f
                  WHERE LTRIM(RTRIM(f.T_ID)) = LTRIM(RTRIM(d.T_ID)) AND f.F_ID = d.F_ID);

-- 用户列配置与查询条件
DELETE x FROM dbo.SYSQL_FIELDS x
WHERE x.F_ID IN (N'CI', N'OWNER', N'OWNER_G')
  AND NOT EXISTS (SELECT 1 FROM dbo.FIELDS f
                  WHERE f.F_ID = x.F_ID
                    AND (LTRIM(RTRIM(f.T_ID)) = LTRIM(RTRIM(x.T_ID))
                      OR LTRIM(RTRIM(f.T_ID)) = LTRIM(RTRIM(x.T_ID_R))));

DELETE x FROM dbo.SYSQL_DEFAULT x
WHERE x.F_ID IN (N'CI', N'OWNER', N'OWNER_G')
  AND NOT EXISTS (SELECT 1 FROM dbo.FIELDS f
                  WHERE f.F_ID = x.F_ID
                    AND (LTRIM(RTRIM(f.T_ID)) = LTRIM(RTRIM(x.T_ID))
                      OR LTRIM(RTRIM(f.T_ID)) = LTRIM(RTRIM(x.T_ID_R))));

DELETE x FROM dbo.SYSQL_CONDITION x
WHERE x.F_ID IN (N'CI', N'OWNER', N'OWNER_G')
  AND NOT EXISTS (SELECT 1 FROM dbo.FIELDS f
                  WHERE f.F_ID = x.F_ID
                    AND (LTRIM(RTRIM(f.T_ID)) = LTRIM(RTRIM(x.T_ID))
                      OR LTRIM(RTRIM(f.T_ID)) = LTRIM(RTRIM(x.T_ID_R))));

DELETE x FROM dbo.SYSQL_COND_DFT x
WHERE x.F_ID IN (N'CI', N'OWNER', N'OWNER_G')
  AND NOT EXISTS (SELECT 1 FROM dbo.FIELDS f
                  WHERE f.F_ID = x.F_ID
                    AND (LTRIM(RTRIM(f.T_ID)) = LTRIM(RTRIM(x.T_ID))
                      OR LTRIM(RTRIM(f.T_ID)) = LTRIM(RTRIM(x.T_ID_R))));

DELETE x FROM dbo.SYSQD_CONDITION x
WHERE x.F_ID IN (N'CI', N'OWNER', N'OWNER_G')
  AND NOT EXISTS (SELECT 1 FROM dbo.FIELDS f
                  WHERE f.F_ID = x.F_ID
                    AND (LTRIM(RTRIM(f.T_ID)) = LTRIM(RTRIM(x.T_ID))
                      OR LTRIM(RTRIM(f.T_ID)) = LTRIM(RTRIM(x.T_ID_R))));

DELETE FROM dbo.SYSQQ
WHERE F_ID IN (N'CI', N'OWNER', N'OWNER_G')
  AND NOT EXISTS (SELECT 1 FROM dbo.FIELDS f
                  WHERE f.F_ID = dbo.SYSQQ.F_ID AND LTRIM(RTRIM(f.T_ID)) = LTRIM(RTRIM(dbo.SYSQQ.T_ID)));

DELETE FROM dbo.SYSQR_DEFAULT
WHERE F_ID IN (N'CI', N'OWNER', N'OWNER_G');

-- ⑦ EXEC_TAG 收敛：行归属列已下线，B/C/D/E 的范围语义不再存在
DECLARE @TagTrail NVARCHAR(MAX) = N'';
SELECT @TagTrail = @TagTrail + N'    SYSDH G=' + LTRIM(RTRIM(G_IDX)) COLLATE DATABASE_DEFAULT
                 + N' M=' + CONVERT(NVARCHAR(20), M_IDX)
                 + N' EXEC_TAG=' + LTRIM(RTRIM(EXEC_TAG)) COLLATE DATABASE_DEFAULT + CHAR(10)
FROM dbo.SYSDH WITH (NOLOCK)
WHERE LTRIM(RTRIM(EXEC_TAG)) IN (N'B', N'C', N'D', N'E');
IF LEN(@TagTrail) > 0
    PRINT N'== 随能力退役的组权限执行范围（B/C/D/E -> Z）==' + CHAR(10) + @TagTrail;

UPDATE dbo.SYSDH SET EXEC_TAG = N'Z' WHERE LTRIM(RTRIM(EXEC_TAG)) IN (N'B', N'C', N'D', N'E');
UPDATE dbo.SYSDD SET EXEC_TAG = N'Z' WHERE LTRIM(RTRIM(EXEC_TAG)) IN (N'B', N'C', N'D', N'E');

-- ⑧ 后置自证：目标列必须消失，保留列必须还在
IF EXISTS (SELECT 1 FROM sys.columns c
           JOIN sys.objects o ON o.object_id = c.object_id AND o.type = N'U'
           JOIN sys.schemas s ON o.schema_id = s.schema_id
           WHERE s.name = N'dbo'
             AND (   (c.name = N'CI'      AND o.name NOT IN (N'DEPT', N'SYSDN'))
                  OR (c.name = N'OWNER'   AND o.name <> N'WF_APPROVE')
                  OR (c.name = N'OWNER_G') ))
    THROW 53410, N'目标列未被删除干净。', 1;

IF COL_LENGTH(N'dbo.DEPT', N'CI') IS NULL
    OR COL_LENGTH(N'dbo.SYSDN', N'CI') IS NULL
    OR COL_LENGTH(N'dbo.WF_APPROVE', N'OWNER') IS NULL
    THROW 53411, N'保留列被误删（DEPT.CI / SYSDN.CI / WF_APPROVE.OWNER）。', 1;

IF EXISTS (SELECT 1 FROM dbo.SYSDH WITH (NOLOCK) WHERE LTRIM(RTRIM(EXEC_TAG)) IN (N'B', N'C', N'D', N'E'))
    THROW 53412, N'SYSDH 仍有 B/C/D/E 执行范围记录：范围过滤已随行归属列下线，收敛未完成。', 1;

COMMIT TRANSACTION;

PRINT N'== 数据归属三列下线完成：CI（DEPT/SYSDN 除外）/ OWNER（WF_APPROVE 除外）/ OWNER_G 已物理删除 ==';
