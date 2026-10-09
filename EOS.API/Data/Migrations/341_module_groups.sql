-- ============================================================================
-- EOS.ERP migration 341: 模块分组独立成表（MODULE_GROUPS）+ 分组配置面 2315
-- ----------------------------------------------------------------------------
-- 背景：分组原本是 MODULES 上的固定 5 组列（GROUP1..5 / GROUP_EXP1..5 /
-- GROUP_DESC1..5）。上限 5 由列数写死，读取侧一律按下标偏移取值（`9 + i*2`、
-- `11 + i*3`、`GROUP_EXP{i+1}`），于是"第几组"既是排序又是身份。
--
-- 本迁移把 15 列下线，改建 MODULE_GROUPS：一个模块任意多组，行的自增主键
-- GROUP_ID 是身份、SORT_IDX 只决定顺序。原"启用位 + 表达式 + 描述三者齐备"
-- 的三道门收敛为"**有行即生效**"——未启用的行本来就不产生任何界面效果，
-- 留着启用位等于让同一件事有两个真源。
--
-- 模块号取 2315（23xx 段当前最大 2314 的下一个）：**编号不回收、不复用**——2308 曾是旧开发团队的
-- 「工作任务记录」，已由迁移 327 整模块退役；它虽然空着也不能拿来用，否则同一个编号在 git 历史里
-- 会有两个互不相干的身份，"只读仓库的人"再也没法把编号与模块对上。
--
-- 配置面独立成模块 2315「模块分组」：主表 MODULE_GROUPS、承载页统一工作台
-- （/workbench），管理员在此增删改分组。分组表达式在组装工作台定义时**实时读取**
-- （WorkbenchDefinition.GroupExpressions 带 [JsonIgnore]，从不进快照），所以这里是
-- 保存即生效、不进发布流程。2301 模块管理的「分组」页签与 15 行字段元数据随本迁移删除。
--
-- 数据搬迁口径：只搬**当前真正生效**的行（启用位=1 且表达式与描述都非空），
-- 即 170204 其它付款凭证、180651 排班共 4 组。启用位=0 的 20 行是历史残留
-- （界面从未显示过它们），不搬：搬过去会因为"有行即生效"而突然出现在列表分组里。
--
-- 顺带补上 MODULES 剩余 bit 标志列的默认值：M_TAG / DETAIL_NO_SAVE / SEARCH_1 /
-- SEARCH_2 / IF_COPY 都没有 DEFAULT，新建模块漏填即落 NULL，而读取侧是按
-- GetBoolean 取值——这正是 LESSONS.md L27 那类"NULL 变 500"的根因。分组列下线后
-- 这批列就是仅剩的风险面，故在同一个迁移里补上 DEFAULT。
--
-- 幂等：建表、数据搬迁、列删除、默认值均按存在性判断，可重复执行。
-- ============================================================================

SET NOCOUNT ON;
SET XACT_ABORT ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET ARITHABORT ON;

DECLARE @GuardMessage NVARCHAR(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 51860, @GuardMessage, 1;

IF OBJECT_ID(N'dbo.MODULES', N'U') IS NULL
    THROW 51861, N'MODULES 表不存在，迁移中止。', 1;

/* 编号不回收、不复用：2315 若已被别的东西占用（主表不是 MODULE_GROUPS）即中止。 */
IF EXISTS (SELECT 1 FROM dbo.MODULES WHERE M_IDX = 2315 AND ISNULL(MASTER_TABLE, N'') <> N'MODULE_GROUPS')
    THROW 51862, N'模块 2315 已被占用（主表不是 MODULE_GROUPS），迁移中止：编号不回收、不复用。', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.MODULES WHERE M_IDX = 2301)
    THROW 51863, N'参照模块 2301 模块管理不存在，迁移中止（授权镜像以它为准）。', 1;

BEGIN TRANSACTION;

/* ---------- 1. 分组表 ---------- */
IF OBJECT_ID(N'dbo.MODULE_GROUPS', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.MODULE_GROUPS
    (
        GROUP_ID         INT IDENTITY (1, 1) NOT NULL,
        M_IDX            INT             NOT NULL,
        SORT_IDX         INT             NOT NULL CONSTRAINT DF_MODULE_GROUPS_SORT_IDX DEFAULT (0),
        GROUP_DESC       NVARCHAR(50)    NOT NULL,
        GROUP_EXP        NVARCHAR(500)   NOT NULL,
        CREATE_PERSON    NCHAR(20)       NULL,
        CREATE_DATE      DATETIME        NULL,
        LAST_UPDATE_BY   NCHAR(20)       NULL,
        LAST_UPDATE_DATE DATETIME        NULL,
        CONSTRAINT PK_MODULE_GROUPS PRIMARY KEY (GROUP_ID)
    );
    CREATE INDEX IX_MODULE_GROUPS_M_IDX ON dbo.MODULE_GROUPS (M_IDX, SORT_IDX);
    PRINT N'== 已建立表 dbo.MODULE_GROUPS ==';
END
ELSE
    PRINT N'== 表 dbo.MODULE_GROUPS 已存在，跳过建表 ==';

/* ---------- 2. 数据搬迁：只搬当前真正生效的分组 ----------
   MODULES 的 15 列此时可能已被删除（重复执行），故整段走动态 SQL：静态引用已删列
   会让整个批编译失败，而不是"条件不成立跳过"。 */
IF COL_LENGTH(N'dbo.MODULES', N'GROUP1') IS NOT NULL
BEGIN
    DECLARE @copy NVARCHAR(MAX) = N'
        INSERT INTO dbo.MODULE_GROUPS (M_IDX, SORT_IDX, GROUP_DESC, GROUP_EXP, CREATE_PERSON, CREATE_DATE, LAST_UPDATE_BY, LAST_UPDATE_DATE)
        SELECT TOP (100000) m.M_IDX, g.SORT_IDX, LTRIM(RTRIM(g.GROUP_DESC)), LTRIM(RTRIM(g.GROUP_EXP)),
               N''migration-341'', SYSDATETIME(), N''migration-341'', SYSDATETIME()
        FROM dbo.MODULES m
        CROSS APPLY (VALUES
            (1, CASE WHEN m.GROUP1 = 1 THEN m.GROUP_DESC1 END, CASE WHEN m.GROUP1 = 1 THEN m.GROUP_EXP1 END),
            (2, CASE WHEN m.GROUP2 = 1 THEN m.GROUP_DESC2 END, CASE WHEN m.GROUP2 = 1 THEN m.GROUP_EXP2 END),
            (3, CASE WHEN m.GROUP3 = 1 THEN m.GROUP_DESC3 END, CASE WHEN m.GROUP3 = 1 THEN m.GROUP_EXP3 END),
            (4, CASE WHEN m.GROUP4 = 1 THEN m.GROUP_DESC4 END, CASE WHEN m.GROUP4 = 1 THEN m.GROUP_EXP4 END),
            (5, CASE WHEN m.GROUP5 = 1 THEN m.GROUP_DESC5 END, CASE WHEN m.GROUP5 = 1 THEN m.GROUP_EXP5 END)
        ) AS g (SORT_IDX, GROUP_DESC, GROUP_EXP)
        WHERE g.GROUP_DESC IS NOT NULL AND LTRIM(RTRIM(g.GROUP_DESC)) <> N''''
          AND g.GROUP_EXP IS NOT NULL AND LTRIM(RTRIM(g.GROUP_EXP)) <> N''''
        ORDER BY m.M_IDX, g.SORT_IDX;';
    IF NOT EXISTS (SELECT 1 FROM dbo.MODULE_GROUPS)
    BEGIN
        EXEC sp_executesql @copy;
        PRINT N'== 已搬迁生效分组 ' + CONVERT(NVARCHAR(10), @@ROWCOUNT) + N' 行 ==';
    END
    ELSE
        PRINT N'== MODULE_GROUPS 已有数据，跳过搬迁 ==';
END
ELSE
    PRINT N'== MODULES.GROUP1 已删除（重复执行），跳过搬迁 ==';

/* ---------- 3. 字段元数据：工作台列表与统一表单全靠它 ---------- */
DECLARE @fields TABLE (
    F_ID NVARCHAR(60), F_DESC NVARCHAR(200), F_TYPE NVARCHAR(20), IS_PK BIT, IS_AUTOINC BIT,
    IS_VISIBLE BIT, IS_QUERY BIT, IS_DEFAULT_FIELDS BIT, IS_READONLY BIT, IS_VERIFY BIT,
    VERIFY_INDEX INT, DISPLAY_LENGTH INT, HEADER_ALIGN NVARCHAR(20), ITEM_ALIGN NVARCHAR(20), F_REMARK NVARCHAR(400));

INSERT INTO @fields (F_ID, F_DESC, F_TYPE, IS_PK, IS_AUTOINC, IS_VISIBLE, IS_QUERY, IS_DEFAULT_FIELDS, IS_READONLY, IS_VERIFY, VERIFY_INDEX, DISPLAY_LENGTH, HEADER_ALIGN, ITEM_ALIGN, F_REMARK) VALUES
    (N'GROUP_ID',         N'分组编号',   N'int',      1, 1, 1, 1, 1, 1, 0, 1,  60, N'center', N'center', N'自增主键，分组的身份；分组筛选链接按它定位'),
    (N'M_IDX',            N'模块编号',   N'int',      0, 0, 1, 1, 1, 0, 1, 2, 100, N'center', N'center', N'分组归属的模块号，见 2301 模块管理'),
    (N'SORT_IDX',         N'排序',       N'int',      0, 0, 1, 1, 1, 0, 1, 3,  54, N'center', N'center', N'同一模块内分组的先后顺序，从 1 起'),
    (N'GROUP_DESC',       N'分组名称',   N'nvarchar', 0, 0, 1, 1, 1, 0, 1, 4, 100, N'center', N'left',   N'列表分组下拉上显示的名字'),
    (N'GROUP_EXP',        N'分组表达式', N'nvarchar', 0, 0, 1, 1, 1, 0, 1, 5, 200, N'center', N'left',   N'受控表达式（GroupExpressionParser），字段白名单 = 模块主表的物理字段'),
    (N'CREATE_PERSON',    N'建立人',     N'nchar',    0, 0, 0, 0, 0, 1, 0, 6,  67, N'center', N'left',   NULL),
    (N'CREATE_DATE',      N'建立日期',   N'datetime', 0, 0, 0, 0, 0, 1, 0, 7, 141, N'center', N'left',   NULL),
    (N'LAST_UPDATE_BY',   N'修改人',     N'nchar',    0, 0, 0, 0, 0, 1, 0, 8,  67, N'center', N'left',   NULL),
    (N'LAST_UPDATE_DATE', N'修改日期',   N'datetime', 0, 0, 0, 0, 0, 1, 0, 9,  79, N'center', N'left',   NULL);

MERGE dbo.FIELDS AS target
USING (SELECT * FROM @fields) AS source
   ON target.T_ID = N'MODULE_GROUPS' AND target.F_ID = source.F_ID
WHEN MATCHED THEN UPDATE SET
    F_DESC = source.F_DESC, F_TYPE = source.F_TYPE, IS_PK = source.IS_PK, IS_AUTOINC = source.IS_AUTOINC,
    IS_VISIBLE = source.IS_VISIBLE, IS_QUERY = source.IS_QUERY, IS_DEFAULT_FIELDS = source.IS_DEFAULT_FIELDS,
    IS_READONLY = source.IS_READONLY, IS_VERIFY = source.IS_VERIFY, VERIFY_INDEX = source.VERIFY_INDEX,
    DISPLAY_LENGTH = source.DISPLAY_LENGTH, HEADER_ALIGN = source.HEADER_ALIGN, ITEM_ALIGN = source.ITEM_ALIGN,
    F_REMARK = source.F_REMARK, LAST_UPDATE_BY = N'migration-341', LAST_UPDATE_DATE = SYSDATETIME()
WHEN NOT MATCHED THEN INSERT
    (T_ID, F_ID, F_DESC, F_TYPE, IS_PK, IS_AUTOINC, IS_VISIBLE, IS_QUERY, IS_DEFAULT_FIELDS, IS_READONLY,
     IS_VERIFY, VERIFY_INDEX, DISPLAY_LENGTH, HEADER_ALIGN, ITEM_ALIGN, F_REMARK, LAST_UPDATE_BY, LAST_UPDATE_DATE)
    VALUES (N'MODULE_GROUPS', source.F_ID, source.F_DESC, source.F_TYPE, source.IS_PK, source.IS_AUTOINC,
            source.IS_VISIBLE, source.IS_QUERY, source.IS_DEFAULT_FIELDS, source.IS_READONLY,
            source.IS_VERIFY, source.VERIFY_INDEX, source.DISPLAY_LENGTH, source.HEADER_ALIGN, source.ITEM_ALIGN,
            source.F_REMARK, N'migration-341', SYSDATETIME());

PRINT N'== 已写入 MODULE_GROUPS 字段元数据 ==';

/* ---------- 4. 字段数据来源：模块编号从 2301 模块管理里挑 ---------- */
IF NOT EXISTS (SELECT 1 FROM dbo.FIELD_DATASOURCE WHERE T_ID = N'MODULE_GROUPS' AND F_ID = N'M_IDX' AND SERIAL_NO = 1)
    INSERT INTO dbo.FIELD_DATASOURCE
        (T_ID, F_ID, SERIAL_NO, ACTIVE_TAG, SOURCE_T_ID, SOURCE_DESC, SOURCE_M_IDX, FILTER_STRUCT, RETURN_ITEMS,
         CREATE_BY, CREATE_DATE, LAST_UPDATE_BY, LAST_UPDATE_DATE)
    VALUES (N'MODULE_GROUPS', N'M_IDX', 1, 1, N'MODULES', N'模块管理', 2301, N'{"logic":"AND","items":[]}',
            N'[{"target":"M_IDX","column":"M_IDX"}]', N'migration-341', SYSDATETIME(), N'migration-341', SYSDATETIME());

PRINT N'== 已登记 M_IDX 的字段数据来源（2301 模块管理）==';

/* ---------- 5. 模块 2315 模块分组（23 系统管理 → 统一工作台）----------
   M_URL 取 /workbench：与同域单据模块同口径，菜单渲染时追加 /{moduleId}。
   工作台定义未发布时按元数据实时装配（BuildFromMetadataAsync），故新模块无需先发布即可用。
   标志列显式给值：M_TAG / DETAIL_NO_SAVE / SEARCH_1 / SEARCH_2 / IF_COPY 在这一行上不容许 NULL。 */
IF NOT EXISTS (SELECT 1 FROM dbo.MODULES WHERE M_IDX = 2315)
BEGIN
    INSERT INTO dbo.MODULES
        (M_IDX, M_DESC, M_URL, M_P_IDX, M_ROOT_IDX, SORT_IDX, M_TAG, MASTER_TABLE, M_ALIAS,
         DETAIL_NO_SAVE, SEARCH_1, SEARCH_2, IF_COPY, AUTO_APPROVE, ERROR_NO_SAVE, EFFECT_ENGINE_TAG,
         REMARK, LAST_UPDATE_BY, LAST_UPDATE_DATE)
    VALUES (2315, N'模块分组', N'/workbench', 23, 23, 15, 1, N'MODULE_GROUPS', N'',
            0, 0, 0, 0, 0, 0, 0,
            N'工作台列表分组的配置面：一个模块可以有任意多组，保存即生效（分组表达式读取在主表定义装配时实时进行，不进发布流程）。',
            N'migration-341', SYSDATETIME());
    PRINT N'== 已登记模块 2315 模块分组（父 23、根 23、序 15）==';
END
ELSE
    PRINT N'== 模块 2315 已存在，跳过登记 ==';

/* ---------- 6. 授权：按 2301 模块管理镜像（同一批配置维护者）---------- */
INSERT INTO dbo.SYSDH
    (G_IDX, M_IDX, EXEC_TAG, ADDNEW_TAG, DELETE_TAG, EDIT_TAG, REPORT_TAG, COST_TAG, SETUP_TAG, SECRECY_TAG,
     ENDCASE_TAG, UNENDCASE_TAG, OTHER1_TAG, OTHER2_TAG, OTHER3_TAG, OTHER4_TAG,
     DENY_VIEW_FIELD_MASTER, DENY_VIEW_FIELD_DETAIL, DENY_NEW_FIELD_MASTER, DENY_NEW_FIELD_DETAIL,
     DENY_MODI_FIELD_MASTER, DENY_MODI_FIELD_DETAIL, DATA_FILTER, OPERFLAG,
     APPROVE_TAG, DEAPPROVE_TAG, FILE_VIEW_TAG, FILE_UPDA_TAG, FILE_EDIT_TAG, FILE_DELE_TAG,
     FORM_DESIGN_TAG, MODULE_CONFIG_TAG)
SELECT h.G_IDX, 2315, h.EXEC_TAG, h.ADDNEW_TAG, h.DELETE_TAG, h.EDIT_TAG, h.REPORT_TAG, h.COST_TAG,
       h.SETUP_TAG, h.SECRECY_TAG, h.ENDCASE_TAG, h.UNENDCASE_TAG, h.OTHER1_TAG, h.OTHER2_TAG,
       h.OTHER3_TAG, h.OTHER4_TAG, h.DENY_VIEW_FIELD_MASTER, h.DENY_VIEW_FIELD_DETAIL,
       h.DENY_NEW_FIELD_MASTER, h.DENY_NEW_FIELD_DETAIL, h.DENY_MODI_FIELD_MASTER, h.DENY_MODI_FIELD_DETAIL,
       h.DATA_FILTER, h.OPERFLAG, h.APPROVE_TAG, h.DEAPPROVE_TAG, h.FILE_VIEW_TAG, h.FILE_UPDA_TAG,
       h.FILE_EDIT_TAG, h.FILE_DELE_TAG, h.FORM_DESIGN_TAG, h.MODULE_CONFIG_TAG
FROM dbo.SYSDH h
WHERE h.M_IDX = 2301
  AND NOT EXISTS (SELECT 1 FROM dbo.SYSDH x WHERE x.G_IDX = h.G_IDX AND x.M_IDX = 2315);

PRINT N'== 已按模块 2301 补组权限 ' + CONVERT(NVARCHAR(10), @@ROWCOUNT) + N' 行 ==';

INSERT INTO dbo.SYSDD
    (USER_ID, M_IDX, EXEC_TAG, ADDNEW_TAG, DELETE_TAG, EDIT_TAG, REPORT_TAG, COST_TAG, SETUP_TAG, SECRECY_TAG,
     ENDCASE_TAG, UNENDCASE_TAG, OTHER1_TAG, OTHER2_TAG, OTHER3_TAG, OTHER4_TAG,
     DENY_VIEW_FIELD_MASTER, DENY_VIEW_FIELD_DETAIL, DENY_NEW_FIELD_MASTER, DENY_NEW_FIELD_DETAIL,
     DENY_MODI_FIELD_MASTER, DENY_MODI_FIELD_DETAIL, DATA_FILTER, OPERFLAG,
     APPROVE_TAG, DEAPPROVE_TAG, FILE_VIEW_TAG, FILE_UPDA_TAG, FILE_EDIT_TAG, FILE_DELE_TAG,
     FORM_DESIGN_TAG, MODULE_CONFIG_TAG)
SELECT d.USER_ID, 2315, d.EXEC_TAG, d.ADDNEW_TAG, d.DELETE_TAG, d.EDIT_TAG, d.REPORT_TAG, d.COST_TAG,
       d.SETUP_TAG, d.SECRECY_TAG, d.ENDCASE_TAG, d.UNENDCASE_TAG, d.OTHER1_TAG, d.OTHER2_TAG,
       d.OTHER3_TAG, d.OTHER4_TAG, d.DENY_VIEW_FIELD_MASTER, d.DENY_VIEW_FIELD_DETAIL,
       d.DENY_NEW_FIELD_MASTER, d.DENY_NEW_FIELD_DETAIL, d.DENY_MODI_FIELD_MASTER, d.DENY_MODI_FIELD_DETAIL,
       d.DATA_FILTER, d.OPERFLAG, d.APPROVE_TAG, d.DEAPPROVE_TAG, d.FILE_VIEW_TAG, d.FILE_UPDA_TAG,
       d.FILE_EDIT_TAG, d.FILE_DELE_TAG, d.FORM_DESIGN_TAG, d.MODULE_CONFIG_TAG
FROM dbo.SYSDD d
WHERE d.M_IDX = 2301
  AND NOT EXISTS (SELECT 1 FROM dbo.SYSDD x WHERE x.USER_ID = d.USER_ID AND x.M_IDX = 2315);

PRINT N'== 已按模块 2301 补个人权限 ' + CONVERT(NVARCHAR(10), @@ROWCOUNT) + N' 行 ==';

/* ---------- 7. 下线 MODULES 的 15 个分组列与其字段元数据 ----------
   列存在性用 COL_LENGTH 判（重复执行安全）；删列走动态 SQL，避免静态引用已删列编译失败。 */
DELETE FROM dbo.FIELDS
 WHERE T_ID = N'MODULES'
   AND F_ID IN (N'GROUP1', N'GROUP2', N'GROUP3', N'GROUP4', N'GROUP5',
                N'GROUP_EXP1', N'GROUP_EXP2', N'GROUP_EXP3', N'GROUP_EXP4', N'GROUP_EXP5',
                N'GROUP_DESC1', N'GROUP_DESC2', N'GROUP_DESC3', N'GROUP_DESC4', N'GROUP_DESC5');
PRINT N'== 已删除 MODULES 分组字段元数据 ' + CONVERT(NVARCHAR(10), @@ROWCOUNT) + N' 行 ==';

DECLARE @drop NVARCHAR(MAX) = N'';
SELECT @drop = @drop + N'ALTER TABLE dbo.MODULES DROP COLUMN [' + name + N'];'
FROM sys.columns
WHERE object_id = OBJECT_ID(N'dbo.MODULES')
  AND (name LIKE N'GROUP[_]EXP%' OR name LIKE N'GROUP[_]DESC%' OR name IN (N'GROUP1', N'GROUP2', N'GROUP3', N'GROUP4', N'GROUP5'))
ORDER BY name;
IF @drop <> N''
BEGIN
    EXEC sp_executesql @drop;
    PRINT N'== 已删除 MODULES 的 15 个分组列 ==';
END
ELSE
    PRINT N'== MODULES 分组列已不存在（重复执行），跳过删列 ==';

/* ---------- 8. 剩余 bit 标志列补默认值 ----------
   这批列没有 DEFAULT，漏填即 NULL，而读取侧按 GetBoolean 取值会直接抛 500（L27 同类根因）。
   补 DEFAULT 不改既有行，只让后续新建模块不再长出 NULL。 */
IF NOT EXISTS (SELECT 1 FROM sys.default_constraints WHERE parent_object_id = OBJECT_ID(N'dbo.MODULES') AND name = N'DF_MODULES_M_TAG')
    ALTER TABLE dbo.MODULES ADD CONSTRAINT DF_MODULES_M_TAG DEFAULT (1) FOR M_TAG;
IF NOT EXISTS (SELECT 1 FROM sys.default_constraints WHERE parent_object_id = OBJECT_ID(N'dbo.MODULES') AND name = N'DF_MODULES_DETAIL_NO_SAVE')
    ALTER TABLE dbo.MODULES ADD CONSTRAINT DF_MODULES_DETAIL_NO_SAVE DEFAULT (0) FOR DETAIL_NO_SAVE;
IF NOT EXISTS (SELECT 1 FROM sys.default_constraints WHERE parent_object_id = OBJECT_ID(N'dbo.MODULES') AND name = N'DF_MODULES_SEARCH_1')
    ALTER TABLE dbo.MODULES ADD CONSTRAINT DF_MODULES_SEARCH_1 DEFAULT (0) FOR SEARCH_1;
IF NOT EXISTS (SELECT 1 FROM sys.default_constraints WHERE parent_object_id = OBJECT_ID(N'dbo.MODULES') AND name = N'DF_MODULES_SEARCH_2')
    ALTER TABLE dbo.MODULES ADD CONSTRAINT DF_MODULES_SEARCH_2 DEFAULT (0) FOR SEARCH_2;
IF NOT EXISTS (SELECT 1 FROM sys.default_constraints WHERE parent_object_id = OBJECT_ID(N'dbo.MODULES') AND name = N'DF_MODULES_IF_COPY')
    ALTER TABLE dbo.MODULES ADD CONSTRAINT DF_MODULES_IF_COPY DEFAULT (0) FOR IF_COPY;

PRINT N'== 已补齐 MODULES 标志列默认值 ==';

/* ---------- 9. 收口断言 ---------- */
IF OBJECT_ID(N'dbo.MODULE_GROUPS', N'U') IS NULL
    THROW 51864, N'MODULE_GROUPS 未建立，迁移中止。', 1;

IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.MODULES') AND name LIKE N'GROUP%')
    THROW 51865, N'MODULES 仍残留分组列，迁移中止。', 1;

IF EXISTS (SELECT 1 FROM dbo.FIELDS WHERE T_ID = N'MODULES' AND F_ID LIKE N'GROUP%')
    THROW 51866, N'MODULES 仍残留分组字段元数据，迁移中止。', 1;

IF (SELECT COUNT(*) FROM dbo.FIELDS WHERE T_ID = N'MODULE_GROUPS' AND F_ID IN
        (N'GROUP_ID', N'M_IDX', N'SORT_IDX', N'GROUP_DESC', N'GROUP_EXP')) <> 5
    THROW 51867, N'MODULE_GROUPS 字段元数据不完整，迁移中止。', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.MODULES WHERE M_IDX = 2315 AND MASTER_TABLE = N'MODULE_GROUPS' AND M_P_IDX = 23 AND ISNULL(M_TAG, 1) = 1)
    THROW 51868, N'模块 2315 未就位或其主表不是 MODULE_GROUPS，迁移中止。', 1;

IF EXISTS (SELECT h.G_IDX FROM dbo.SYSDH h WHERE h.M_IDX = 2301
             AND NOT EXISTS (SELECT 1 FROM dbo.SYSDH x WHERE x.G_IDX = h.G_IDX AND x.M_IDX = 2315))
    THROW 51869, N'模块 2315 的组权限少于参照模块 2301，迁移中止。', 1;

IF EXISTS (SELECT d.USER_ID FROM dbo.SYSDD d WHERE d.M_IDX = 2301
             AND NOT EXISTS (SELECT 1 FROM dbo.SYSDD x WHERE x.USER_ID = d.USER_ID AND x.M_IDX = 2315))
    THROW 51870, N'模块 2315 的个人权限少于参照模块 2301，迁移中止。', 1;

COMMIT TRANSACTION;

DECLARE @GroupRows INT = (SELECT COUNT(*) FROM dbo.MODULE_GROUPS);
DECLARE @Modules INT = (SELECT COUNT(DISTINCT M_IDX) FROM dbo.MODULE_GROUPS);
DECLARE @Rights INT = (SELECT COUNT(*) FROM dbo.SYSDH WHERE M_IDX = 2315);
PRINT N'== 收口：MODULE_GROUPS ' + CONVERT(NVARCHAR(10), @GroupRows) + N' 行 / '
    + CONVERT(NVARCHAR(10), @Modules) + N' 个模块；模块 2315 已登记，组权限 '
    + CONVERT(NVARCHAR(10), @Rights) + N' 行 ==';
