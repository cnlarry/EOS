-- ============================================================================
-- EOS.ERP migration 191: 退役 SYSDL.G_IDX，用户组关系唯一收敛到 SYSDG_USER
-- ----------------------------------------------------------------------------
-- 用户与用户组是多对多关系，唯一存储为 SYSDG_USER；SYSDL.G_IDX 是旧单组列，
-- 模块权限、菜单、报表权限、数据范围都只读 SYSDG_USER，只有开户接口还在写它，
-- 于是「开户时选的组」落进没人读的列，用户实际不属于任何组。
-- 本迁移把残留值补搬进关联表后删除该列，并清理指向它的字段元数据、默认列配置、
-- 查询关系文本与遗留函数（f_get_user_gidx 直接 SELECT 该列，且全库无调用方）。
--
-- 幂等：列已删除时跳过搬迁与删除步骤；元数据清理可重复执行。
-- ============================================================================

SET NOCOUNT ON;

SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET ARITHABORT ON;

DECLARE @GUARD_MESSAGE NVARCHAR(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 51700, @GUARD_MESSAGE, 1;

/* ---------- 1. 历史值搬迁：旧列有值、关联表缺该组的用户补一条关联 ---------- */
IF COL_LENGTH('dbo.SYSDL', 'G_IDX') IS NOT NULL
BEGIN
    INSERT INTO dbo.SYSDG_USER (G_IDX, USER_ID)
    SELECT LTRIM(RTRIM(g.G_IDX)), LTRIM(RTRIM(l.USER_ID))
    FROM dbo.SYSDL l
    INNER JOIN dbo.SYSDG g ON LTRIM(RTRIM(g.G_IDX)) = LTRIM(RTRIM(l.G_IDX))
    WHERE LTRIM(RTRIM(ISNULL(l.G_IDX, ''))) <> ''
      AND NOT EXISTS (
          SELECT 1 FROM dbo.SYSDG_USER u
          WHERE LTRIM(RTRIM(u.USER_ID)) = LTRIM(RTRIM(l.USER_ID))
            AND LTRIM(RTRIM(u.G_IDX)) = LTRIM(RTRIM(l.G_IDX)));

    PRINT N'== SYSDL.G_IDX → SYSDG_USER 补搬 ' + CONVERT(NVARCHAR(10), @@ROWCOUNT) + N' 条 ==';

    /* 旧列取值指向不存在的组时无法搬迁：显式列出，避免静默丢弃 */
    IF EXISTS (
        SELECT 1 FROM dbo.SYSDL l
        WHERE LTRIM(RTRIM(ISNULL(l.G_IDX, ''))) <> ''
          AND NOT EXISTS (SELECT 1 FROM dbo.SYSDG g WHERE LTRIM(RTRIM(g.G_IDX)) = LTRIM(RTRIM(l.G_IDX))))
        PRINT N'== 注意：仍有 SYSDL.G_IDX 指向不存在的组（这些用户的组需人工确认）==';
END

/* ---------- 2. 清理指向该列的元数据 ---------- */
DELETE FROM dbo.FIELDS
 WHERE LTRIM(RTRIM(T_ID)) = 'SYSDL' AND LTRIM(RTRIM(F_ID)) = 'G_IDX';

DELETE FROM dbo.SYSQL_DEFAULT
 WHERE LTRIM(RTRIM(T_ID)) = 'SYSDL' AND LTRIM(RTRIM(F_ID)) = 'G_IDX';

DELETE FROM dbo.SYSQL_FIELDS
 WHERE LTRIM(RTRIM(T_ID)) = 'SYSDL' AND LTRIM(RTRIM(F_ID)) = 'G_IDX';

PRINT N'== 已清理 SYSDL.G_IDX 的字段元数据与默认列配置 ==';

/* 查询关系文本里的 SYSDG 关联（按该列连接）一并去掉，保留 SYSDG_USER 关联 */
UPDATE dbo.TABLES
   SET QUERY_RELATION = REPLACE(REPLACE(
           QUERY_RELATION,
           N'LEFT JOIN SYSDG WITH (NOLOCK) ON SYSDL.G_IDX=SYSDG.G_IDX', N''),
           CHAR(13) + CHAR(10) + CHAR(13) + CHAR(10), CHAR(13) + CHAR(10))
 WHERE ISNULL(QUERY_RELATION, '') LIKE '%SYSDL.G_IDX%';

PRINT N'== 已清理 ' + CONVERT(NVARCHAR(10), @@ROWCOUNT) + N' 张表的查询关系文本 ==';

/* ---------- 3. 删除遗留函数（唯一直接读该列的库内对象） ---------- */
IF OBJECT_ID('dbo.f_get_user_gidx') IS NOT NULL
BEGIN
    DROP FUNCTION dbo.f_get_user_gidx;
    PRINT N'== 已删除遗留函数 f_get_user_gidx ==';
END

/* ---------- 4. 删除旧列 ---------- */
IF COL_LENGTH('dbo.SYSDL', 'G_IDX') IS NOT NULL
BEGIN
    ALTER TABLE dbo.SYSDL DROP COLUMN G_IDX;
    PRINT N'== 已删除 SYSDL.G_IDX ==';
END

/* ---------- 收口断言 ---------- */
IF COL_LENGTH('dbo.SYSDL', 'G_IDX') IS NOT NULL
    THROW 51701, N'SYSDL.G_IDX 仍然存在，迁移中止。', 1;

IF EXISTS (SELECT 1 FROM dbo.FIELDS WHERE LTRIM(RTRIM(T_ID)) = 'SYSDL' AND LTRIM(RTRIM(F_ID)) = 'G_IDX')
    THROW 51702, N'SYSDL.G_IDX 的字段元数据未清理干净，迁移中止。', 1;

IF OBJECT_ID('dbo.f_get_user_gidx') IS NOT NULL
    THROW 51703, N'遗留函数 f_get_user_gidx 未删除，迁移中止。', 1;

PRINT N'== 收口：SYSDL.G_IDX 已退役，用户组关系唯一来源为 SYSDG_USER ==';
