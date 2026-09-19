-- ============================================================================
-- EOS.ERP migration 192: 清理用户组关联里的空组代脏数据
-- ----------------------------------------------------------------------------
-- SYSDG_USER.G_IDX 应指向 SYSDG 中的组代号；历史数据里存在 G_IDX 为空串的
-- 关联行，它们在组管理页会表现为一个无名组、在开户选择器里也会出现。
-- 本迁移删除这些空组代关联（组本身不存在，关联没有任何权限含义）。
--
-- 另有「关联表有成员、SYSDG 里没有组定义」的组（YW1/YW2/YW3/ZG 等），
-- 它们承载着真实归属，删除会丢业务数据，故本迁移只做提示、不擅自处理。
--
-- 幂等：满足条件的行删完即无副作用。
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

/* ---------- 1. 删除空组代的用户关联 ---------- */
DELETE FROM dbo.SYSDG_USER
 WHERE G_IDX IS NULL OR LTRIM(RTRIM(G_IDX)) = '';

PRINT N'== 已清理空组代用户关联 ' + CONVERT(NVARCHAR(10), @@ROWCOUNT) + N' 条 ==';

/* ---------- 2. 组定义表里的空组代行（当前不存在，防御性清理） ---------- */
DELETE FROM dbo.SYSDG
 WHERE G_IDX IS NULL OR LTRIM(RTRIM(G_IDX)) = '';

PRINT N'== 已清理空组代组定义 ' + CONVERT(NVARCHAR(10), @@ROWCOUNT) + N' 条 ==';

/* ---------- 3. 提示：仍指向未定义组的关联（真实归属，需人工确认是否补建组） ---------- */
IF EXISTS (
    SELECT 1 FROM dbo.SYSDG_USER u
    WHERE LTRIM(RTRIM(ISNULL(u.G_IDX, ''))) <> ''
      AND NOT EXISTS (SELECT 1 FROM dbo.SYSDG g WHERE LTRIM(RTRIM(g.G_IDX)) = LTRIM(RTRIM(u.G_IDX))))
BEGIN
    PRINT N'== 注意：仍有用户关联到 SYSDG 中未定义的组（组管理页看不到这些组，需确认补建或调整归属）==';
    SELECT LTRIM(RTRIM(u.G_IDX)) AS UNDEFINED_GROUP, COUNT(1) AS MEMBERS
    FROM dbo.SYSDG_USER u
    WHERE LTRIM(RTRIM(ISNULL(u.G_IDX, ''))) <> ''
      AND NOT EXISTS (SELECT 1 FROM dbo.SYSDG g WHERE LTRIM(RTRIM(g.G_IDX)) = LTRIM(RTRIM(u.G_IDX)))
    GROUP BY u.G_IDX;
END

/* ---------- 收口断言 ---------- */
IF EXISTS (SELECT 1 FROM dbo.SYSDG_USER WHERE G_IDX IS NULL OR LTRIM(RTRIM(G_IDX)) = '')
    THROW 51701, N'SYSDG_USER 仍有空组代关联，迁移中止。', 1;

IF EXISTS (SELECT 1 FROM dbo.SYSDG WHERE G_IDX IS NULL OR LTRIM(RTRIM(G_IDX)) = '')
    THROW 51702, N'SYSDG 仍有空组代行，迁移中止。', 1;

PRINT N'== 收口：用户组关联不再包含空组代 ==';
